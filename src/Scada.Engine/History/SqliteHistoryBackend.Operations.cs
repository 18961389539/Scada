using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Models;
using Scada.Data;
using Scada.Data.Infrastructure;

namespace Scada.Engine.History;

public partial class SqliteHistoryBackend
{
    private string HistoryRoot => _factory.HistoryRoot;

    private static readonly Regex HistoryShard = new(@"^(raw_\d{6}|agg_\d{4})\.db$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static HistoryStoreUsage MeasureUsage(string historyRoot)
    {
        long raw = 0, aggregate = 0, journal = 0, other = 0;
        var rawFiles = 0;
        var aggregateFiles = 0;
        var complete = true;
        try
        {
            if (Directory.Exists(historyRoot))
            {
                foreach (var path in Directory.EnumerateFiles(historyRoot, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        var bytes = file.Length;
                        var relative = Path.GetRelativePath(historyRoot, path);
                        if (relative.StartsWith("_spool" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (file.Name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
                            journal += bytes;
                        else if (file.Name.StartsWith("raw_", StringComparison.OrdinalIgnoreCase) && file.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase))
                        {
                            raw += bytes;
                            rawFiles++;
                        }
                        else if (file.Name.StartsWith("agg_", StringComparison.OrdinalIgnoreCase) && file.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase))
                        {
                            aggregate += bytes;
                            aggregateFiles++;
                        }
                        else other += bytes;
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (IOException) { complete = false; }
                    catch (UnauthorizedAccessException) { complete = false; }
                }
            }
        }
        catch (IOException) { complete = false; }
        catch (UnauthorizedAccessException) { complete = false; }
        return new HistoryStoreUsage(raw, aggregate, journal, other, rawFiles, aggregateFiles, complete);
    }

    public HistoryStoreUsage MeasureUsage() => MeasureUsage(HistoryRoot);

    public async Task CleanupRetentionAsync(CancellationToken cancellationToken)
    {
        CleanupRawHistory();
        await CleanupAggregatesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> PackBackupAsync(string stagingHistoryDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stagingHistoryDirectory);
        if (!Directory.Exists(HistoryRoot)) return [];
        var packed = new List<string>();
        foreach (var source in Directory.EnumerateFiles(HistoryRoot, "*.db"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = Path.Combine(stagingHistoryDirectory, Path.GetFileName(source));
            if (!CopyDatabase(source, copy)) continue;
            packed.Add(copy);
        }
        return packed;
    }

    public IReadOnlyList<HistoryReplicaCursor> GetReplicaCursors()
    {
        var result = new List<HistoryReplicaCursor>();
        var root = Path.GetFullPath(HistoryRoot);
        if (!Directory.Exists(root)) return result;
        foreach (var path in Directory.EnumerateFiles(root, "*.db").OrderBy(x => x, StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            if (!HistoryShard.IsMatch(file)) continue;
            try
            {
                using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                connection.Open();
                foreach (var table in TablesOf(file))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT COALESCE(MAX(rowid), 0) FROM {table}";
                    try { result.Add(new HistoryReplicaCursor(file, table, Convert.ToInt64(cmd.ExecuteScalar()))); }
                    catch (SqliteException) { }
                }
            }
            catch (SqliteException ex) { _logger.LogWarning(ex, "读取历史库 {File} 复制位置失败", file); }
        }
        return result;
    }

    public HistoryReplicaRows ReadReplica(string file, string table, long after, int limit)
    {
        if (!HistoryShard.IsMatch(file) || !TablesOf(file).Contains(table))
            throw new HistoryReplicaException("历史文件或表名无效", 400);
        var path = Path.Combine(Path.GetFullPath(HistoryRoot), file);
        if (!File.Exists(path)) return new HistoryReplicaRows(file, table, []);
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return SqliteHistoryReplica.Read(connection, file, table, after, limit);
    }

    public void ApplyReplica(HistoryReplicaRows batch)
    {
        if (!HistoryShard.IsMatch(batch.File) || !TablesOf(batch.File).Contains(batch.Table))
            throw new HistoryReplicaException("历史文件或表名无效", 400);
        var name = Path.GetFileNameWithoutExtension(batch.File);
        if (name.StartsWith("raw_", StringComparison.Ordinal))
            _factory.CreateRaw(int.Parse(name[4..8], CultureInfo.InvariantCulture), int.Parse(name[8..10], CultureInfo.InvariantCulture)).Dispose();
        else
            _factory.CreateAggregate(int.Parse(name[4..8], CultureInfo.InvariantCulture)).Dispose();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Path.GetFullPath(HistoryRoot), batch.File)};Pooling=False;Default Timeout=30");
        connection.Open();
        SqliteHistoryReplica.Apply(connection, batch.Table, batch);
    }

    public async Task<IReadOnlyList<HistoryPendingWindow>> ListPendingWindowsAsync(
        bool raw, long latestStartInclusive, int batchSize, CancellationToken cancellationToken)
    {
        var result = new List<HistoryPendingWindow>();
        if (!Directory.Exists(HistoryRoot)) return result;
        var pattern = raw ? "raw_*.db" : "agg_*.db";
        var format = raw ? "yyyyMM" : "yyyy";
        foreach (var path in Directory.EnumerateFiles(HistoryRoot, pattern).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Path.GetFileName(path);
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length <= 4 || !DateTime.TryParseExact(name[4..], format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                continue;
            try
            {
                await using var db = raw
                    ? OpenRawShard(file)
                    : OpenAggregateShard(file);
                if (db is null) continue;
                await using (db)
                {
                    var windowMs = raw ? PendingAggregation.FiveMinuteMs : PendingAggregation.HourMs;
                    var pending = await db.PendingAggregations.AsNoTracking()
                        .Where(x => x.WindowMs == windowMs && x.Ts <= latestStartInclusive)
                        .OrderBy(x => x.Ts).Take(batchSize).ToListAsync(cancellationToken);
                    result.AddRange(pending.Select(x => new HistoryPendingWindow(file, raw, x.Ts, x.WindowMs, x.Revision)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "读取聚合待办 {File} 失败，未完成窗口保留待重试", file);
            }
        }
        return result;
    }

    public async Task AcknowledgePendingWindowAsync(HistoryPendingWindow window, CancellationToken cancellationToken)
    {
        await using var db = window.Raw ? OpenRawShard(window.Shard) : OpenAggregateShard(window.Shard);
        if (db is null) return;
        await using (db)
        {
            await db.PendingAggregations
                .Where(x => x.WindowMs == window.WindowMs && x.Ts == window.Ts && x.Revision == window.Revision)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    public async Task<bool> HasPendingRawWindowsAsync(long fromInclusive, long toExclusive, CancellationToken cancellationToken)
    {
        var end = Math.Max(fromInclusive, toExclusive - 1);
        foreach (var (year, month) in HistoryCalendar.MonthsOverlapping(fromInclusive, end, _zone))
        {
            if (!File.Exists(_factory.GetRawPath(year, month))) continue;
            await using var db = _factory.CreateRaw(year, month);
            if (await db.PendingAggregations.AnyAsync(
                x => x.WindowMs == PendingAggregation.FiveMinuteMs && x.Ts >= fromInclusive && x.Ts < toExclusive, cancellationToken))
                return true;
        }
        return false;
    }

    private void CleanupRawHistory()
    {
        if (!Directory.Exists(HistoryRoot)) return;
        var cutoff = DateTime.UtcNow.AddMonths(-Math.Max(_settings.HistoryRetentionMonths, 1));
        foreach (var file in Directory.EnumerateFiles(HistoryRoot, "raw_*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_');
            if (parts.Length != 2 || parts[1].Length != 6 ||
                !int.TryParse(parts[1][..4], out var year) ||
                !int.TryParse(parts[1][4..], out var month))
            {
                continue;
            }

            if (year < cutoff.Year || (year == cutoff.Year && month < cutoff.Month))
            {
                if (_settings.AggregationEnabled && Role.IsActive)
                {
                    using var db = _factory.CreateRaw(year, month);
                    if (db.PendingAggregations.Any(x => x.WindowMs == PendingAggregation.FiveMinuteMs))
                    {
                        _logger.LogWarning("原始历史库 {File} 尚有待聚合窗口，暂缓保留清理", file);
                        continue;
                    }
                }
                _factory.ReleaseFile(file);
                if (TryDelete(file))
                {
                    TryDelete(file + "-wal");
                    TryDelete(file + "-shm");
                }
            }
        }
    }

    private async Task CleanupAggregatesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(HistoryRoot)) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(_settings.Agg5MinRetentionDays, 1)).ToUnixTimeMilliseconds();
        cutoff -= cutoff % PendingAggregation.HourMs;
        var hourCutoff = _settings.AggHourRetentionDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-_settings.AggHourRetentionDays).ToUnixTimeMilliseconds() : (long?)null;
        foreach (var file in Directory.EnumerateFiles(HistoryRoot, "agg_*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var year))
            {
                continue;
            }

            try
            {
                await using var db = _factory.CreateAggregate(year);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM Agg5Min WHERE Ts < {0} AND
                    ({1} = 0 OR NOT EXISTS (
                        SELECT 1 FROM PendingAggregation p
                        WHERE p.WindowMs = 3600000 AND p.Ts = Agg5Min.Ts - (((Agg5Min.Ts - IFNULL((SELECT OffsetMs FROM AggregationOffset WHERE Id = 1), 0)) % 3600000 + 3600000) % 3600000)))
                    """,
                    new object[] { cutoff, _settings.AggregationEnabled && Role.IsActive ? 1 : 0 },
                    cancellationToken);
                if (hourCutoff is { } hour)
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM AggHour WHERE Ts < {0}", new object[] { hour }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "清理聚合数据 {File} 失败", file);
            }
        }
    }

    private HistoryDbContext? OpenRawShard(string file)
    {
        if (!HistoryShard.IsMatch(file) || !file.StartsWith("raw_", StringComparison.Ordinal)) return null;
        var name = Path.GetFileNameWithoutExtension(file);
        return _factory.CreateRaw(int.Parse(name[4..8], CultureInfo.InvariantCulture), int.Parse(name[8..10], CultureInfo.InvariantCulture));
    }

    private HistoryDbContext? OpenAggregateShard(string file)
    {
        if (!HistoryShard.IsMatch(file) || !file.StartsWith("agg_", StringComparison.Ordinal)) return null;
        var name = Path.GetFileNameWithoutExtension(file);
        return _factory.CreateAggregate(int.Parse(name[4..8], CultureInfo.InvariantCulture));
    }

    private static string[] TablesOf(string file) =>
        file.StartsWith("raw_", StringComparison.Ordinal) ? ["HistoryValue"] : ["Agg5Min", "AggHour"];

    private static bool CopyDatabase(string source, string target)
    {
        if (!File.Exists(source)) return false;
        using var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False");
        using var to = new SqliteConnection($"Data Source={target};Pooling=False");
        from.Open(); to.Open();
        from.BackupDatabase(to);
        return true;
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除文件 {File} 失败", path);
            return false;
        }
    }
}
