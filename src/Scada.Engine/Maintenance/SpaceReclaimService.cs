using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data.Infrastructure;

namespace Scada.Engine.Maintenance;

public sealed record SpaceReclaimTarget(
    string Id,
    string Path,
    string AutoVacuumMode,
    long PageSize,
    long PageCount,
    long FreelistPages,
    long FileBytes,
    long FreelistBytes);

public sealed record SpaceReclaimRun(
    string TargetId,
    string Action,
    bool Ok,
    string? Reason,
    long BeforeBytes,
    long AfterBytes,
    long DurationMs,
    DateTimeOffset Ts);

public sealed record SpaceReclaimStatus(
    IReadOnlyList<SpaceReclaimTarget> Targets,
    IReadOnlyList<SpaceReclaimRun> LastRuns,
    DateTimeOffset? LastOkTs,
    DateTimeOffset CheckedAt);

/// <summary>
/// 配置库、运行库和历史聚合分片的空间回收。
/// 增量回收可随时尝试；一次性 VACUUM 会独占锁定，必须在历史查询空闲时、限时执行，失败可跳过。
/// 尚未切换 incremental 的老库需要先成功 VACUUM 才能改模式。
/// </summary>
public sealed class SpaceReclaimService(
    IOptions<ScadaSettings> options,
    ILogger<SpaceReclaimService> logger,
    IHistoryDbFactory? historyFactory = null,
    Func<int>? activeHistoryQueries = null)
{
    public const string ActionIncremental = "incremental";
    public const string ActionVacuum = "vacuum";
    public const string ActionSkipped = "skipped";
    public const string ActionFailed = "failed";

    private readonly ScadaSettings _settings = options.Value;
    private readonly object _gate = new();
    private IReadOnlyList<SpaceReclaimRun> _lastRuns = [];

    public string LogPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_settings.ConfigDbPath)) ?? "data", "space-reclaim-runs.json");

    public int VacuumTimeoutSeconds => Math.Clamp(_settings.SpaceReclaimVacuumTimeoutSeconds, 5, 600);

    public SpaceReclaimStatus Snapshot()
    {
        var targets = ProbeAll();
        var runs = LoadRuns();
        var lastOk = runs.Where(x => x.Ok).Select(x => (DateTimeOffset?)x.Ts).DefaultIfEmpty().Max();
        return new SpaceReclaimStatus(targets, runs, lastOk, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<SpaceReclaimRun>> ReclaimAsync(CancellationToken cancellationToken, bool allowVacuum = true)
    {
        var runs = new List<SpaceReclaimRun>();
        foreach (var target in ProbeAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            runs.Add(await ReclaimOneAsync(target, allowVacuum, cancellationToken));
        }

        SaveRuns(runs);
        return runs;
    }

    public async Task<SpaceReclaimRun> ReclaimOneAsync(SpaceReclaimTarget target, bool allowVacuum, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var before = target.FileBytes;
        try
        {
            var busy = activeHistoryQueries?.Invoke() ?? 0;
            var needsVacuum = !string.Equals(target.AutoVacuumMode, "incremental", StringComparison.OrdinalIgnoreCase)
                              || (target.FreelistPages > 0 && target.AutoVacuumMode == "none");

            if (needsVacuum)
            {
                if (!allowVacuum)
                    return Finish(target.Id, ActionSkipped, true, "未允许执行独占 VACUUM", before, before, sw, started);
                if (busy > 0)
                    return Finish(target.Id, ActionSkipped, true, $"历史查询正忙（{busy}），跳过独占 VACUUM", before, before, sw, started);
                return await VacuumAsync(target, sw, started, cancellationToken);
            }

            var afterIncremental = await IncrementalAsync(target, cancellationToken);
            if (afterIncremental.FreelistPages > 0 && allowVacuum && busy == 0)
            {
                // 增量模式仍有空闲页时再试 incremental_vacuum 即可，不必升级到全库 VACUUM。
                afterIncremental = await IncrementalAsync(target, cancellationToken);
            }

            var after = FileBytes(target.Path);
            return Finish(target.Id, ActionIncremental, true, null, before, after, sw, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "空间回收失败：{Target}", target.Id);
            return Finish(target.Id, ActionFailed, false, ex.Message, before, FileBytes(target.Path), sw, started);
        }
    }

    public IReadOnlyList<SpaceReclaimTarget> ProbeAll()
    {
        var list = new List<SpaceReclaimTarget>();
        AddIfExists(list, "config", _settings.ConfigDbPath);
        AddIfExists(list, "runtime", _settings.RuntimeDbPath);
        var historyRoot = historyFactory?.HistoryRoot ?? _settings.HistoryRoot;
        if (Directory.Exists(historyRoot))
        {
            foreach (var path in Directory.EnumerateFiles(historyRoot, "agg_*.db").Order(StringComparer.Ordinal))
                AddIfExists(list, "history:" + Path.GetFileName(path), path);
        }
        return list;
    }

    public static SpaceReclaimTarget Probe(string id, string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return new SpaceReclaimTarget(id, full, "missing", 0, 0, 0, 0, 0);

        using var connection = Open(full, timeoutSeconds: 5);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_size; PRAGMA page_count; PRAGMA freelist_count; PRAGMA auto_vacuum;";
        using var reader = command.ExecuteReader();
        // 多条 PRAGMA 在同一 Command 里会以多个结果集返回。
        long pageSize = ReadScalar(reader), pageCount = NextScalar(reader), freelist = NextScalar(reader), mode = NextScalar(reader);
        var auto = mode switch { 1 => "full", 2 => "incremental", _ => "none" };
        var bytes = FileBytes(full);
        return new SpaceReclaimTarget(id, full, auto, pageSize, pageCount, freelist, bytes, freelist * pageSize);
    }

    private async Task<SpaceReclaimTarget> IncrementalAsync(SpaceReclaimTarget target, CancellationToken cancellationToken)
    {
        await using var connection = Open(target.Path, timeoutSeconds: 15);
        await ExecAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        await ExecAsync(connection, "PRAGMA incremental_vacuum;", cancellationToken);
        return Probe(target.Id, target.Path);
    }

    private async Task<SpaceReclaimRun> VacuumAsync(SpaceReclaimTarget target, System.Diagnostics.Stopwatch sw, DateTimeOffset started, CancellationToken cancellationToken)
    {
        var before = FileBytes(target.Path);
        await using var connection = Open(target.Path, timeoutSeconds: VacuumTimeoutSeconds);
        // 先做 quick_check：损坏的库不能 VACUUM，避免把故障扩大。
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            check.CommandTimeout = Math.Min(30, VacuumTimeoutSeconds);
            var result = (await check.ExecuteScalarAsync(cancellationToken))?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                return Finish(target.Id, ActionSkipped, true, "quick_check 未通过：" + result, before, before, sw, started);
        }

        await ExecAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        // 先声明 incremental，再一次 VACUUM：既回收空闲页，又把模式从 none 切换过来。
        await ExecAsync(connection, "PRAGMA auto_vacuum=INCREMENTAL;", cancellationToken);
        await ExecAsync(connection, "VACUUM;", cancellationToken);

        var after = FileBytes(target.Path);
        var probed = Probe(target.Id, target.Path);
        logger.LogInformation("空间回收 VACUUM 完成：{Target} {Before} → {After}，模式 {Mode}",
            target.Id, before, after, probed.AutoVacuumMode);
        return Finish(target.Id, ActionVacuum, true, "模式 " + probed.AutoVacuumMode, before, after, sw, started);
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteConnection Open(string path, int timeoutSeconds)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout={timeoutSeconds}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout={timeoutSeconds * 1000};";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static long ReadScalar(SqliteDataReader reader)
    {
        if (!reader.Read()) return 0;
        return reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0));
    }

    private static long NextScalar(SqliteDataReader reader)
    {
        if (!reader.NextResult()) return 0;
        return ReadScalar(reader);
    }

    private static long FileBytes(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static void AddIfExists(List<SpaceReclaimTarget> list, string id, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { list.Add(Probe(id, path)); }
        catch (Exception) { /* 探测失败时本轮跳过该库，不影响其余目标 */ }
    }

    private static SpaceReclaimRun Finish(string id, string action, bool ok, string? reason, long before, long after, System.Diagnostics.Stopwatch sw, DateTimeOffset started) =>
        new(id, action, ok, reason, before, after, sw.ElapsedMilliseconds, started);

    private IReadOnlyList<SpaceReclaimRun> LoadRuns()
    {
        lock (_gate)
        {
            if (_lastRuns.Count > 0) return _lastRuns;
            try
            {
                if (File.Exists(LogPath))
                    _lastRuns = JsonSerializer.Deserialize<List<SpaceReclaimRun>>(File.ReadAllText(LogPath)) ?? [];
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "读取空间回收记录失败");
            }
            return _lastRuns;
        }
    }

    private void SaveRuns(IReadOnlyList<SpaceReclaimRun> runs)
    {
        lock (_gate)
        {
            _lastRuns = runs.ToArray();
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(LogPath, JsonSerializer.Serialize(_lastRuns));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "写入空间回收记录失败");
            }
        }
    }
}
