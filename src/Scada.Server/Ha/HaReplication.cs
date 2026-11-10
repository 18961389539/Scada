using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Server.Services;

namespace Scada.Server.Ha;

public sealed record HaManifest(string NodeId, string Role, long Term, string Schema, string ConfigSha, long ConfigBytes, IReadOnlyList<HaHistoryCursor> History, string? RuntimeSha = null, long? RuntimeBytes = null);

// 本节点私有状态：任期、已应用的配置摘要、历史复制游标。放在 StateDir，不在配置库里，因此不会被复制覆盖。
public sealed class HaNodeState
{
    public long Term { get; set; }
    public string? ConfigSha { get; set; }
    public string? RuntimeSha { get; set; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> Cursors { get; set; } = new(StringComparer.Ordinal);
}

// 应用层复制。评估结论见 docs/high-availability.md：
// - 配置库：快照传送。主机用 SQLite 在线备份生成一致快照并按 SHA256 去重，备机拉取后用在线备份写回本地库，再执行迁移并重载。
// - 历史库：通过 IHistoryBackend 增量拉取（SQLite 后端内部按文件与 rowid 实现；协议字段不变）。
// 不采用 Litestream：它不支持 Windows，且 WAL 帧级复制要求接管页面检查点，与现有按月分库、运行中迁移的结构冲突。
public sealed class HaReplication(IOptions<ScadaSettings> settings, IDbContextFactory<ConfigDbContext> factory, IHistoryBackend history,
    IServiceProvider services, ILogger<HaReplication> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly ScadaSettings _settings = settings.Value;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1), _applyGate = new(1, 1);
    private (string Key, string Sha, long Bytes)? _snapshot, _runtimeSnapshot;

    public string ConfigPath => Path.GetFullPath(_settings.ConfigDbPath);
    public string RuntimePath => DataServiceCollectionExtensions.ResolveRuntimePath(_settings);
    public string HistoryRoot => Path.GetFullPath(_settings.HistoryRoot);
    public string? LastError { get; private set; }
    public long LastConfigSyncTs { get; private set; }
    public long LastHistorySyncTs { get; private set; }
    public long HistoryRowsBehind { get; private set; }
    public long ConfigApplied { get; private set; }
    public long HistoryRowsApplied { get; private set; }

    // ---------- 主机侧 ----------

    public async Task<string> SchemaAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "";
    }

    public async Task<(string Path, string Sha, long Bytes)> ConfigSnapshotAsync(string stateDir, CancellationToken ct)
    {
        await _snapshotGate.WaitAsync(ct);
        try
        {
            var target = Path.Combine(stateDir, "outgoing-config.db");
            var key = Stamp(ConfigPath) + "|" + Stamp(ConfigPath + "-wal");
            if (_snapshot is { } s && s.Key == key && File.Exists(target)) return (target, s.Sha, s.Bytes);
            Directory.CreateDirectory(stateDir);
            var temp = target + ".tmp";
            File.Delete(temp);
            using (var from = new SqliteConnection($"Data Source={ConfigPath};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var cmd = to.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=DELETE"; cmd.ExecuteNonQuery();
            }
            File.Move(temp, target, true);
            var sha = Hash(target); var bytes = new FileInfo(target).Length;
            _snapshot = (key, sha, bytes);
            return (target, sha, bytes);
        }
        finally { _snapshotGate.Release(); }
    }

    public async Task<(string Path, string Sha, long Bytes)?> RuntimeSnapshotAsync(string stateDir, CancellationToken ct)
    {
        if (!File.Exists(RuntimePath)) return null;
        await _snapshotGate.WaitAsync(ct);
        try
        {
            var target = Path.Combine(stateDir, "outgoing-runtime.db");
            var key = Stamp(RuntimePath) + "|" + Stamp(RuntimePath + "-wal");
            if (_runtimeSnapshot is { } s && s.Key == key && File.Exists(target)) return (target, s.Sha, s.Bytes);
            Directory.CreateDirectory(stateDir);
            var temp = target + ".tmp";
            File.Delete(temp);
            using (var from = new SqliteConnection($"Data Source={RuntimePath};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var cmd = to.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=DELETE"; cmd.ExecuteNonQuery();
            }
            File.Move(temp, target, true);
            var sha = Hash(target); var bytes = new FileInfo(target).Length;
            _runtimeSnapshot = (key, sha, bytes);
            return (target, sha, bytes);
        }
        finally { _snapshotGate.Release(); }
    }

    public IReadOnlyList<HaHistoryCursor> HistoryCursors() => history.GetReplicaCursors();

    public HaHistoryRows ReadHistory(string file, string table, long after, int limit)
    {
        try { return history.ReadReplica(file, table, after, limit); }
        catch (HistoryReplicaException ex) { throw new HaException(ex.Message, ex.Status); }
    }

    // ---------- 备机侧 ----------

    // 拉取并应用。force=true 用于交接前的最终同步：不论对端角色，追平全部历史。
    public async Task SyncAsync(HaPeerClient peer, HaNodeState state, Func<Task> save, bool config, bool historyDue, bool force, CancellationToken ct)
    {
        await _applyGate.WaitAsync(ct);
        try
        {
            var manifest = await peer.ManifestAsync(ct);
            if (!force && manifest.Role != "Primary") return;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                if (!db.Database.GetMigrations().Contains(manifest.Schema) && !string.IsNullOrEmpty(manifest.Schema))
                    throw new HaException($"对端数据结构版本较新（{manifest.Schema}），已暂停复制；请先升级本节点");
            }
            if ((config || force) && manifest.RuntimeSha is { } runtimeSha && runtimeSha != state.RuntimeSha)
            {
                await ApplyRuntimeAsync(peer, manifest, ct);
                state.RuntimeSha = runtimeSha; await save();
            }
            if ((config || force) && manifest.ConfigSha != state.ConfigSha)
            {
                if (manifest.RuntimeSha is null && File.Exists(RuntimePath)) File.Delete(RuntimePath);
                await ApplyConfigAsync(peer, manifest, ct);
                state.ConfigSha = manifest.ConfigSha; await save();
            }
            if (config || force) LastConfigSyncTs = Scada.Core.Models.ScadaTime.NowMs();
            if (historyDue || force)
            {
                var deadline = Environment.TickCount64 + (force ? 20000 : 2000);
                long behind = 0;
                foreach (var cursor in manifest.History)
                {
                    var key = cursor.File + "|" + cursor.Table;
                    var at = state.Cursors.GetValueOrDefault(key);
                    if (cursor.MaxRowId < at) at = 0; // 对端重建了该文件（如被保留策略删除后重建）
                    while (at < cursor.MaxRowId && Environment.TickCount64 < deadline)
                    {
                        var batch = await peer.HistoryAsync(cursor.File, cursor.Table, at, ct);
                        if (batch.Rows.Count == 0) break;
                        ApplyHistory(batch);
                        at = (long)batch.Rows[^1][0]; HistoryRowsApplied += batch.Rows.Count;
                        state.Cursors[key] = at; await save();
                    }
                    behind += Math.Max(0, cursor.MaxRowId - at);
                }
                HistoryRowsBehind = behind;
                LastHistorySyncTs = Scada.Core.Models.ScadaTime.NowMs();
            }
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            if (force) throw;
            logger.LogWarning("主备复制失败：{Message}", ex.Message);
        }
        finally { _applyGate.Release(); }
    }

    private async Task ApplyRuntimeAsync(HaPeerClient peer, HaManifest manifest, CancellationToken ct)
    {
        var incoming = Path.Combine(Path.GetDirectoryName(RuntimePath)!, ".ha-incoming-runtime.db");
        File.Delete(incoming);
        try
        {
            await peer.DownloadRuntimeAsync(incoming, ct);
            var sha = Hash(incoming);
            if (!string.Equals(sha, manifest.RuntimeSha, StringComparison.OrdinalIgnoreCase))
                throw new HaException("运行库快照在传输中已更新，稍后重试");
            SqliteConnection.ClearAllPools();
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimePath)!);
            using (var from = new SqliteConnection($"Data Source={incoming};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={RuntimePath};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var wal = to.CreateCommand(); wal.CommandText = "PRAGMA journal_mode=WAL"; wal.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            await using var db = await services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync(ct);
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("已从主机同步运行库（{Bytes} 字节）", manifest.RuntimeBytes);
        }
        finally { try { File.Delete(incoming); } catch { } }
    }

    private async Task ApplyConfigAsync(HaPeerClient peer, HaManifest manifest, CancellationToken ct)
    {
        var incoming = Path.Combine(Path.GetDirectoryName(ConfigPath)!, ".ha-incoming-config.db");
        File.Delete(incoming);
        try
        {
            await peer.DownloadConfigAsync(incoming, ct);
            var sha = Hash(incoming);
            if (!string.Equals(sha, manifest.ConfigSha, StringComparison.OrdinalIgnoreCase))
            {
                // 下载期间主机又生成了新快照：下次再取，不应用不一致的文件。
                throw new HaException("配置快照在传输中已更新，稍后重试");
            }
            SqliteConnection.ClearAllPools();
            using (var from = new SqliteConnection($"Data Source={incoming};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={ConfigPath};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var wal = to.CreateCommand(); wal.CommandText = "PRAGMA journal_mode=WAL"; wal.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            await using (var db = await factory.CreateDbContextAsync(ct)) await db.Database.MigrateAsync(ct); // 本节点版本更新时就地升级
            await services.GetRequiredService<ConfigReloader>().ReloadAsync(ct);
            ConfigApplied++;
            logger.LogInformation("已从主机同步配置库（{Bytes} 字节）", manifest.ConfigBytes);
        }
        finally { try { File.Delete(incoming); } catch { } }
    }

    private void ApplyHistory(HaHistoryRows batch)
    {
        try { history.ApplyReplica(batch); }
        catch (HistoryReplicaException ex) { throw new HaException(ex.Message, ex.Status); }
    }

    private static string Stamp(string path) => File.Exists(path) ? $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}" : "-";

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
