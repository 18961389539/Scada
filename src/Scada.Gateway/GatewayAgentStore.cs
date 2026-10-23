using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scada.Core.Models;

namespace Scada.Gateway;

public sealed record GatewaySpoolStatus(Guid StreamId, long PendingBatches, long PendingBytes, long? OldestTs, long LastAcknowledged, long NextSequence);
public sealed record GatewayPendingTelemetry(long Sequence, string Json, string Sha256, GatewayTelemetry Packet);
public sealed record GatewayStoredCommand(GatewayCommand Command, string State, string? ProtectedExecutionToken, GatewayCommandResult? Result, bool Reported);
public sealed class GatewaySpoolFullException(string message) : IOException(message);

// This file belongs to one gateway installation. Packet sequences and command tombstones survive restart.
public sealed class GatewayAgentStore
{
    private const int ApplicationId = 0x53434757;
    private readonly string _path, _connectionString;
    private readonly long _maxPendingBytes;
    private readonly int _maxCommands;
    public Guid StreamId { get; }

    public GatewayAgentStore(string path, long maxPendingBytes = 256L * 1024 * 1024, int maxCommands = 100_000)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("网关存储路径不能为空", nameof(path));
        if (maxPendingBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxPendingBytes));
        if (maxCommands is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxCommands));
        _path = Path.GetFullPath(path); _maxPendingBytes = maxPendingBytes; _maxCommands = maxCommands;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, DefaultTimeout = 5 }.ToString();
        using var db = Open();
        var existingApp = Convert.ToInt32(Scalar(db, null, "PRAGMA application_id")); var existingVersion = Convert.ToInt32(Scalar(db, null, "PRAGMA user_version"));
        if (existingApp != 0 && existingApp != ApplicationId || existingVersion is < 0 or > 1 || existingVersion != 0 && existingApp != ApplicationId
            || existingVersion == 0 && Convert.ToInt64(Scalar(db, null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")) != 0)
            throw new InvalidDataException("此文件不是受支持的网关存储，未进行修改");
        Execute(db, null, "PRAGMA journal_mode=WAL");
        using var tx = db.BeginTransaction(deferred: false);
        var app = Convert.ToInt32(Scalar(db, tx, "PRAGMA application_id")); var version = Convert.ToInt32(Scalar(db, tx, "PRAGMA user_version"));
        if (app != 0 && app != ApplicationId || version is < 0 or > 1 || version != 0 && app != ApplicationId) throw new InvalidDataException("此文件不是受支持的网关存储，未进行修改");
        if (version == 0)
        {
            if (Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")) != 0)
                throw new InvalidDataException("网关存储路径指向已有数据库，未进行修改");
            Execute(db, tx, """
                CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE Outbox (Sequence INTEGER PRIMARY KEY, DeviceId INTEGER NOT NULL, Revision TEXT NOT NULL,
                    CreatedTs INTEGER NOT NULL, Payload TEXT NOT NULL, Bytes INTEGER NOT NULL, Sha256 TEXT NOT NULL);
                CREATE TABLE CommandJournal (Id TEXT PRIMARY KEY, Body TEXT NOT NULL, BodyHash TEXT NOT NULL, State TEXT NOT NULL,
                    ExpiresTs INTEGER NOT NULL, ProtectedToken TEXT NULL, Result TEXT NULL, Reported INTEGER NOT NULL DEFAULT 0);
                """);
            Execute(db, tx, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1");
            SetMeta(db, tx, "StreamId", Guid.NewGuid().ToString("D")); SetMeta(db, tx, "NextSequence", "1");
            SetMeta(db, tx, "PendingBytes", "0"); SetMeta(db, tx, "LastAcknowledged", "0");
        }
        StreamId = Guid.Parse(Meta(db, tx, "StreamId")); tx.Commit();
    }

    public GatewayProcessLease AcquireProcessLease() => new(this, new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));

    public Task<long> AppendAsync(int deviceId, string revision, IReadOnlyList<TagValue> values, CancellationToken ct = default)
    {
        ValidateBatch(deviceId, revision, values); ct.ThrowIfCancellationRequested();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); ct.ThrowIfCancellationRequested();
        var sequence = Number(db, tx, "NextSequence");
        var json = JsonSerializer.Serialize(new GatewayTelemetry(GatewayProtocol.Version, StreamId, sequence, deviceId, revision, ScadaTime.NowMs(), values), GatewayProtocol.Json);
        var bytes = Encoding.UTF8.GetByteCount(json); var pending = Number(db, tx, "PendingBytes");
        if (bytes > _maxPendingBytes - pending) throw new GatewaySpoolFullException("网关持久队列已满，采集暂停；已保存数据不会被自动丢弃");
        Execute(db, tx, "INSERT INTO Outbox VALUES ($seq,$device,$revision,$ts,$payload,$bytes,$hash)",
            ("$seq", sequence), ("$device", deviceId), ("$revision", revision), ("$ts", ScadaTime.NowMs()), ("$payload", json), ("$bytes", bytes), ("$hash", Hash(json)));
        SetMeta(db, tx, "NextSequence", checked(sequence + 1).ToString(CultureInfo.InvariantCulture));
        SetMeta(db, tx, "PendingBytes", checked(pending + bytes).ToString(CultureInfo.InvariantCulture));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.FromResult(sequence);
    }

    public Task<GatewayPendingTelemetry?> PeekAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var command = Command(db, null, "SELECT Sequence,Payload,Sha256 FROM Outbox ORDER BY Sequence LIMIT 1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<GatewayPendingTelemetry?>(null);
        var json = reader.GetString(1); var packet = JsonSerializer.Deserialize<GatewayTelemetry>(json, GatewayProtocol.Json) ?? throw new InvalidDataException("网关队列包为空");
        if (Hash(json) != reader.GetString(2) || packet.StreamId != StreamId || packet.Sequence != reader.GetInt64(0)) throw new InvalidDataException("网关队列包校验失败，未删除数据");
        return Task.FromResult<GatewayPendingTelemetry?>(new(reader.GetInt64(0), json, reader.GetString(2), packet));
    }

    public Task AcknowledgeAsync(GatewayAcknowledgement acknowledgement, CancellationToken ct = default)
    {
        if (acknowledgement.StreamId != StreamId || acknowledgement.Sequence < 1) throw new ArgumentException("确认不属于当前数据流");
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var last = Number(db, tx, "LastAcknowledged"); if (acknowledgement.Sequence <= last) return Task.CompletedTask;
        using var command = Command(db, tx, "SELECT Sequence,Bytes FROM Outbox ORDER BY Sequence LIMIT 1");
        long sequence, bytes;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(0) != acknowledgement.Sequence) throw new InvalidOperationException("确认必须匹配队首数据包，不能跳过未确认数据");
            sequence = reader.GetInt64(0); bytes = reader.GetInt64(1);
        }
        Execute(db, tx, "DELETE FROM Outbox WHERE Sequence=$seq", ("$seq", sequence));
        SetMeta(db, tx, "PendingBytes", checked(Number(db, tx, "PendingBytes") - bytes).ToString(CultureInfo.InvariantCulture));
        SetMeta(db, tx, "LastAcknowledged", sequence.ToString(CultureInfo.InvariantCulture));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.CompletedTask;
    }

    public Task<GatewaySpoolStatus> StatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        var count = Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM Outbox")); var oldest = Scalar(db, tx, "SELECT MIN(CreatedTs) FROM Outbox");
        return Task.FromResult(new GatewaySpoolStatus(StreamId, count, Number(db, tx, "PendingBytes"), oldest is DBNull ? null : Convert.ToInt64(oldest), Number(db, tx, "LastAcknowledged"), Number(db, tx, "NextSequence")));
    }

    public Task SaveProtectedConfigurationAsync(string protectedConfiguration, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(protectedConfiguration) || protectedConfiguration.Length > 8 * 1024 * 1024) throw new ArgumentException("受保护配置为空或过大");
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        SetMeta(db, tx, "ProtectedConfiguration", protectedConfiguration); tx.Commit(); return Task.CompletedTask;
    }
    public Task<string?> LoadProtectedConfigurationAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open();
        return Task.FromResult(Scalar(db, null, "SELECT Value FROM Meta WHERE Key='ProtectedConfiguration'") as string);
    }

    public Task<GatewayStoredCommand> ReserveCommandAsync(GatewayCommand request, long serverNowTs, CancellationToken ct = default)
    {
        ValidateCommand(request); ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var body = JsonSerializer.Serialize(request, GatewayProtocol.Json); var existing = ReadCommand(db, tx, request.Id);
        if (existing is not null)
        {
            if (Hash(JsonSerializer.Serialize(existing.Command, GatewayProtocol.Json)) != Hash(body)) throw new InvalidOperationException("相同命令 ID 的内容已变化");
            return Task.FromResult(existing);
        }
        if (Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM CommandJournal")) >= _maxCommands)
            throw new GatewaySpoolFullException("网关命令日志已满，拒绝新命令；已有执行记录不会被自动覆盖");
        var expired = request.ExpiresTs <= serverNowTs;
        var result = expired ? new GatewayCommandResult(GatewayCommandOutcome.NotSent, "命令到达网关时已经过期，未下发") : null;
        Execute(db, tx, "INSERT INTO CommandJournal (Id,Body,BodyHash,State,ExpiresTs,Result) VALUES ($id,$body,$hash,$state,$expires,$result)",
            ("$id", request.Id.ToString("D")), ("$body", body), ("$hash", Hash(body)), ("$state", expired ? "Completed" : "Reserved"), ("$expires", request.ExpiresTs),
            ("$result", result is null ? null : JsonSerializer.Serialize(result, GatewayProtocol.Json)));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.FromResult(new GatewayStoredCommand(request, expired ? "Completed" : "Reserved", null, result, false));
    }

    public Task<bool> MarkCommandStartedAsync(Guid id, string protectedExecutionToken, long serverNowTs, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(protectedExecutionToken) || protectedExecutionToken.Length > 16384) throw new ArgumentException("缺少受保护的执行授权");
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var command = ReadCommand(db, tx, id) ?? throw new KeyNotFoundException("命令未预留");
        if (command.State != "Reserved") return Task.FromResult(false);
        if (command.Command.ExpiresTs <= serverNowTs)
        {
            var expired = new GatewayCommandResult(GatewayCommandOutcome.NotSent, "命令在执行前已经过期，未下发");
            Execute(db, tx, "UPDATE CommandJournal SET State='Completed',Result=$result WHERE Id=$id", ("$id", id.ToString("D")), ("$result", JsonSerializer.Serialize(expired, GatewayProtocol.Json)));
            tx.Commit(); return Task.FromResult(false);
        }
        Execute(db, tx, "UPDATE CommandJournal SET State='Started',ProtectedToken=$token WHERE Id=$id", ("$id", id.ToString("D")), ("$token", protectedExecutionToken));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.FromResult(true);
    }

    public Task CompleteCommandAsync(Guid id, GatewayCommandResult result, CancellationToken ct = default)
    {
        ValidateResult(result); ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var command = ReadCommand(db, tx, id) ?? throw new KeyNotFoundException("命令不存在");
        if (command.Result is not null)
        {
            if (command.Result != result) throw new InvalidOperationException("命令已经有终态结果，不能覆盖");
            return Task.CompletedTask;
        }
        if (command.State != "Started") throw new InvalidOperationException("命令没有执行授权");
        Execute(db, tx, "UPDATE CommandJournal SET State=$state,Result=$result,Reported=0 WHERE Id=$id", ("$id", id.ToString("D")),
            ("$state", result.Outcome == GatewayCommandOutcome.Unknown ? "Unknown" : "Completed"), ("$result", JsonSerializer.Serialize(result, GatewayProtocol.Json)));
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.CompletedTask;
    }

    public Task<int> RecoverInterruptedCommandsAsync(GatewayProcessLease lease, CancellationToken ct = default)
    {
        lock (lease.Sync)
        {
            lease.Verify(this); ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            var unknown = JsonSerializer.Serialize(new GatewayCommandResult(GatewayCommandOutcome.Unknown, "网关在命令执行期间重启，执行结果未知；不会自动重发"), GatewayProtocol.Json);
            var notSent = JsonSerializer.Serialize(new GatewayCommandResult(GatewayCommandOutcome.NotSent, "网关在执行授权落盘前重启，命令未下发"), GatewayProtocol.Json);
            var count = Execute(db, tx, "UPDATE CommandJournal SET State='Unknown',Result=$result,Reported=0 WHERE State='Started'", ("$result", unknown));
            count += Execute(db, tx, "UPDATE CommandJournal SET State='Completed',Result=$result,Reported=0 WHERE State='Reserved'", ("$result", notSent));
            ct.ThrowIfCancellationRequested(); tx.Commit(); return Task.FromResult(count);
        }
    }

    public Task<IReadOnlyList<GatewayStoredCommand>> UnreportedResultsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var command = Command(db, null, "SELECT Body,State,ProtectedToken,Result,Reported,BodyHash FROM CommandJournal WHERE Result IS NOT NULL AND Reported=0 ORDER BY ExpiresTs LIMIT 100");
        using var reader = command.ExecuteReader(); var results = new List<GatewayStoredCommand>();
        while (reader.Read()) results.Add(StoredCommand(reader)); return Task.FromResult<IReadOnlyList<GatewayStoredCommand>>(results);
    }
    public Task AcknowledgeCommandResultAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var db = Open();
        Execute(db, null, "UPDATE CommandJournal SET Reported=1 WHERE Id=$id AND Result IS NOT NULL", ("$id", id.ToString("D"))); return Task.CompletedTask;
    }

    public Task<int> PruneAcknowledgedCommandsAsync(long serverNowTs, int retentionDays = 7, CancellationToken ct = default)
    {
        if (serverNowTs < 1 || retentionDays is < 1 or > 365) throw new ArgumentException("命令日志保留参数无效");
        ct.ThrowIfCancellationRequested(); using var db = Open();
        return Task.FromResult(Execute(db, null, "DELETE FROM CommandJournal WHERE Reported=1 AND Result IS NOT NULL AND ExpiresTs<$cutoff",
            ("$cutoff", serverNowTs - retentionDays * 86_400_000L)));
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        try { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA synchronous=FULL"; command.ExecuteNonQuery(); return db; }
        catch { db.Dispose(); throw; }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command;
    }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(db, tx, sql, parameters); return command.ExecuteScalar(); }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(db, tx, sql, parameters); return command.ExecuteNonQuery(); }
    private static string Meta(SqliteConnection db, SqliteTransaction tx, string key) => (string?)Scalar(db, tx, "SELECT Value FROM Meta WHERE Key=$key", ("$key", key)) ?? throw new InvalidDataException("网关存储元数据缺失");
    private static long Number(SqliteConnection db, SqliteTransaction tx, string key) => long.Parse(Meta(db, tx, key), CultureInfo.InvariantCulture);
    private static void SetMeta(SqliteConnection db, SqliteTransaction tx, string key, string value) => Execute(db, tx, "INSERT INTO Meta VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$key", key), ("$value", value));
    private static GatewayStoredCommand? ReadCommand(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var command = Command(db, tx, "SELECT Body,State,ProtectedToken,Result,Reported,BodyHash FROM CommandJournal WHERE Id=$id", ("$id", id.ToString("D")));
        using var reader = command.ExecuteReader(); return reader.Read() ? StoredCommand(reader) : null;
    }
    private static GatewayStoredCommand StoredCommand(SqliteDataReader reader)
    {
        if (Hash(reader.GetString(0)) != reader.GetString(5)) throw new InvalidDataException("网关命令内容校验失败");
        return new(JsonSerializer.Deserialize<GatewayCommand>(reader.GetString(0), GatewayProtocol.Json) ?? throw new InvalidDataException("命令内容无效"), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<GatewayCommandResult>(reader.GetString(3), GatewayProtocol.Json), reader.GetBoolean(4));
    }
    private static void ValidateBatch(int deviceId, string revision, IReadOnlyList<TagValue> values)
    {
        if (deviceId < 1 || !GatewayProtocol.IsRevision(revision) || values is null || values.Count is < 1 or > GatewayProtocol.MaxValuesPerBatch
            || values.Any(x => x is null || x.TagId < 1 || !double.IsFinite(x.Value) || !Enum.IsDefined(x.Quality) || x.Ts < 1 || x.SourceTs is < 0 || x.Error?.Length > 1000)
            || values.Select(x => x.TagId).Distinct().Count() != values.Count) throw new ArgumentException("网关采集批次无效");
    }
    private static void ValidateCommand(GatewayCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Id == Guid.Empty || command.DeviceId < 1 || command.TagId < 1 || !double.IsFinite(command.Value) || !GatewayProtocol.IsRevision(command.ConfigurationRevision)
            || command.CreatedTs < 1 || command.ExpiresTs <= command.CreatedTs || command.ExpiresTs - command.CreatedTs > 120_000) throw new ArgumentException("网关命令无效");
    }
    private static void ValidateResult(GatewayCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(result.Outcome) || string.IsNullOrWhiteSpace(result.Message) || result.Message.Length > 2000 || result.ReadBack is double number && !double.IsFinite(number)) throw new ArgumentException("命令结果无效");
    }
}

public sealed class GatewayProcessLease : IDisposable
{
    internal object Sync { get; } = new();
    private readonly GatewayAgentStore _owner;
    private FileStream? _stream;
    internal GatewayProcessLease(GatewayAgentStore owner, FileStream stream) { _owner = owner; _stream = stream; }
    internal void Verify(GatewayAgentStore owner) { if (!ReferenceEquals(owner, _owner) || _stream is null) throw new InvalidOperationException("恢复命令需要当前网关进程的独占存储租约"); }
    public void Dispose() { lock (Sync) { _stream?.Dispose(); _stream = null; } }
}
