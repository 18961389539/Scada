using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Options;

namespace Scada.Data.Infrastructure;

public interface IHistoryDbFactory
{
    string HistoryRoot { get; }
    ScadaSettings Settings { get; }
    HistoryDbContext CreateRaw(int year, int month);
    HistoryDbContext CreateAggregate(int year);
    Task<HistoryDbContext> CreateRawAsync(int year, int month, CancellationToken cancellationToken = default);
    Task<HistoryDbContext> CreateAggregateAsync(int year, CancellationToken cancellationToken = default);
    string GetRawPath(int year, int month);
    string GetAggregatePath(int year);
    void EnsureCreated(HistoryDbContext context);
    void ReleaseFile(string path) { }
}

public sealed class HistoryDbFactory : IHistoryDbFactory
{
    private readonly ScadaSettings _settings;
    private readonly ConcurrentDictionary<string, bool> _initialized = new(StringComparer.OrdinalIgnoreCase);
    // 每个历史文件一道闸：新月份首次被并发查询时，两个请求会同时跑 Migrate()，
    // SQLite 上表现为 database is locked 或 table already exists，且只在月初偶发。
    // 用信号量而不是 lock，是因为异步调用方需要 await 而不是阻塞线程池线程。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public HistoryDbFactory(IOptions<ScadaSettings> settings)
    {
        _settings = settings.Value;
        Directory.CreateDirectory(_settings.HistoryRoot);
    }

    public string HistoryRoot => _settings.HistoryRoot;
    public ScadaSettings Settings => _settings;

    public string GetRawPath(int year, int month) =>
        Path.Combine(_settings.HistoryRoot, $"raw_{year}{month:00}.db");

    public string GetAggregatePath(int year) =>
        Path.Combine(_settings.HistoryRoot, $"agg_{year}.db");

    public HistoryDbContext CreateRaw(int year, int month) => Create(GetRawPath(year, month));

    public HistoryDbContext CreateAggregate(int year) => Create(GetAggregatePath(year));

    public Task<HistoryDbContext> CreateRawAsync(int year, int month, CancellationToken cancellationToken = default) =>
        CreateAsync(GetRawPath(year, month), cancellationToken);

    public Task<HistoryDbContext> CreateAggregateAsync(int year, CancellationToken cancellationToken = default) =>
        CreateAsync(GetAggregatePath(year), cancellationToken);

    public void ReleaseFile(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        SqliteConnection.ClearPool(connection);
        _initialized.TryRemove(path, out _);
        // 闸对象此前只增不删：删除历史月库后（保留期清理）会一直留着。
        _gates.TryRemove(path, out _);
    }

    public void EnsureCreated(HistoryDbContext context)
    {
        var path = context.Database.GetDbConnection().DataSource;
        if (_initialized.ContainsKey(path)) return;

        var gate = Gate(path);
        gate.Wait();
        try
        {
            if (_initialized.ContainsKey(path)) return;
            if (NeedsMigration(context))
            {
                context.Database.Migrate();
                AlignHourWindows(context, path);
            }
            else
            {
                // 查询打开的是已存在的旧月份文件：迁移链已最新就不跑 Migrate。
                // Migrate() 会拿写锁并建 WAL/SHM，只读挂载（viewer / 只读副本）上直接失败；
                // AlignHourWindows 内部有纯读快速路径，已对齐时一个字节都不写。
                AlignHourWindows(context, path);
            }
            _initialized.TryAdd(path, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureCreatedAsync(HistoryDbContext context, CancellationToken cancellationToken)
    {
        var path = context.Database.GetDbConnection().DataSource;
        if (_initialized.ContainsKey(path)) return;

        var gate = Gate(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized.ContainsKey(path)) return;
            if (NeedsMigration(context))
            {
                // 只有文件不存在或迁移链落后时才跑 DDL；异步执行避免查询线程被文件级 DDL 卡住。
                await context.Database.MigrateAsync(cancellationToken);
            }
            AlignHourWindows(context, path);
            _initialized.TryAdd(path, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim Gate(string path) => _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// 是否需要执行 DDL。对已存在且迁移链最新的文件返回 false，查询路径因此可以纯读打开。
    /// 这里只读 __EFMigrationsHistory，不建表、不拿写锁。
    /// </summary>
    private static bool NeedsMigration(HistoryDbContext context)
    {
        var path = context.Database.GetDbConnection().DataSource;
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return true;
        try
        {
            return context.Database.GetPendingMigrations().Any();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            // 历史表不存在或损坏：交给 Migrate 去建/修，别让查询路径误判成"已最新"。
            return true;
        }
    }

    // 未配置 ShiftTimeZone 时小时桶仍按 UTC 整点，已有历史文件的键不变。
    // 配置了时区后，桶起点用该时区的基准偏移。整点时差（含 UTC+8）与 UTC 整点重合；
    // 半点时区（如 UTC+5:30）新写入和回补窗口改到本地整点。夏令时若只加减整小时，分钟网格不变。
    private long HourOffsetMs()
    {
        var id = _settings.ShiftTimeZone;
        if (string.IsNullOrWhiteSpace(id)) return 0;
        try { return (long)TimeZoneInfo.FindSystemTimeZoneById(id).GetUtcOffset(DateTime.UtcNow).TotalMilliseconds; }
        catch (Exception) { return 0; }
    }

    private static long HourResidue(long offsetMs)
    {
        var mod = offsetMs % 3_600_000;
        return mod < 0 ? mod + 3_600_000 : mod;
    }

    private void AlignHourWindows(HistoryDbContext context, string path)
    {
        var offset = HourOffsetMs();
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) connection.Open();
        try
        {
            // 快速路径：表、触发器和时区都已就位时，一次查询就能确认无需写入。
            // 查询路径（只读挂载）走到这里必须零 DDL，否则只读库会直接抛 "attempt to write a readonly database"。
            if (AlreadyAligned(connection, path, offset)) return;
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS AggregationOffset (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    OffsetMs INTEGER NOT NULL);
                """);
            // 原始历史是 (TagId,Ts) 原地更新，rowid 游标看不到这些修正。用改写流记录被更新的行，
            // 主备复制时按改写序号把当前行状态重发一次（备机写入同样按质量优先级合并）。
            if (!Path.GetFileName(path).StartsWith("agg_", StringComparison.OrdinalIgnoreCase))
            {
                Execute(connection, """
                    CREATE TABLE IF NOT EXISTS HistoryValueRewrite (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        TagId INTEGER NOT NULL,
                        Ts INTEGER NOT NULL);
                    CREATE TRIGGER IF NOT EXISTS HistoryValue_CaptureRewrite AFTER UPDATE ON HistoryValue
                    BEGIN
                        INSERT INTO HistoryValueRewrite(TagId, Ts) VALUES (OLD.TagId, OLD.Ts);
                        DELETE FROM HistoryValueRewrite
                        WHERE Id < (SELECT MAX(Id) FROM HistoryValueRewrite) - 100000;
                    END;
                    """);
            }
            long previous = 0;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT OffsetMs FROM AggregationOffset WHERE Id = 1";
                var value = read.ExecuteScalar();
                if (value is not null and not DBNull) previous = Convert.ToInt64(value);
            }
            using (var write = connection.CreateCommand())
            {
                write.CommandText = "INSERT INTO AggregationOffset (Id, OffsetMs) VALUES (1, $offset) ON CONFLICT(Id) DO UPDATE SET OffsetMs = $offset";
                write.Parameters.Add(new SqliteParameter("$offset", offset));
                write.ExecuteNonQuery();
            }
            Execute(connection, "DROP TRIGGER IF EXISTS Agg5Min_QueueAggregation");
            Execute(connection, """
                CREATE TRIGGER Agg5Min_QueueAggregation AFTER INSERT ON Agg5Min
                BEGIN
                    INSERT INTO PendingAggregation (WindowMs, Ts, Revision)
                    VALUES (3600000,
                        NEW.Ts - (((NEW.Ts - (SELECT OffsetMs FROM AggregationOffset WHERE Id = 1)) % 3600000 + 3600000) % 3600000),
                        1)
                    ON CONFLICT(WindowMs, Ts) DO UPDATE SET Revision = Revision + 1;
                END;
                """);
            // 分钟网格变了才重写小时行。整点时差的键与原来的 UTC 整点相同，不碰已有文件。
            // 先丢掉对不齐的待办窗口，否则聚合任务还会把刚删掉的 UTC 小时行写回来。
            if (!Path.GetFileName(path).StartsWith("agg_", StringComparison.OrdinalIgnoreCase)
                || HourResidue(previous) == HourResidue(offset)) return;
            Execute(connection, """
                DELETE FROM PendingAggregation
                WHERE WindowMs = 3600000
                  AND (((Ts - $offset) % 3600000) + 3600000) % 3600000 != 0
                """, offset);
            Execute(connection, """
                DELETE FROM AggHour
                WHERE (((Ts - $offset) % 3600000) + 3600000) % 3600000 != 0
                """, offset);
            Execute(connection, """
                INSERT INTO PendingAggregation (WindowMs, Ts, Revision)
                SELECT 3600000,
                    Ts - (((Ts - $offset) % 3600000 + 3600000) % 3600000),
                    1
                FROM Agg5Min
                GROUP BY Ts - (((Ts - $offset) % 3600000 + 3600000) % 3600000)
                ON CONFLICT(WindowMs, Ts) DO UPDATE SET Revision = Revision + 1
                """, offset);
        }
        finally
        {
            if (openedHere) connection.Close();
        }
    }

    /// <summary>纯读检查：结构齐全且小时网格未变时，AlignHourWindows 可以整个跳过。</summary>
    private static bool AlreadyAligned(System.Data.Common.DbConnection connection, string path, long offset)
    {
        try
        {
            using var check = connection.CreateCommand();
            check.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AggregationOffset'),
                  (SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='Agg5Min_QueueAggregation'),
                  (SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='HistoryValue_CaptureRewrite'),
                  (SELECT OffsetMs FROM AggregationOffset WHERE Id = 1);
                """;
            using var reader = check.ExecuteReader();
            if (!reader.Read()) return false;
            var hasOffsetTable = reader.GetInt64(0) > 0;
            var hasAggTrigger = reader.GetInt64(1) > 0;
            var hasRewriteTrigger = reader.GetInt64(2) > 0;
            var previous = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);
            var isAggregate = Path.GetFileName(path).StartsWith("agg_", StringComparison.OrdinalIgnoreCase);
            return hasOffsetTable && hasAggTrigger && (isAggregate || hasRewriteTrigger)
                && HourResidue(previous) == HourResidue(offset);
        }
        catch (SqliteException)
        {
            // AggregationOffset 还不存在（新库）：走完整建表流程。
            return false;
        }
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql, long? offset = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (offset is long value) command.Parameters.Add(new SqliteParameter("$offset", value));
        command.ExecuteNonQuery();
    }

    private HistoryDbContext Create(string path)
    {
        var context = new HistoryDbContext(Options(path));
        EnsureCreated(context);
        return context;
    }

    private async Task<HistoryDbContext> CreateAsync(string path, CancellationToken cancellationToken)
    {
        var context = new HistoryDbContext(Options(path));
        await EnsureCreatedAsync(context, cancellationToken);
        return context;
    }

    private DbContextOptions<HistoryDbContext> Options(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new DbContextOptionsBuilder<HistoryDbContext>()
            .UseSqlite(ConnectionString(path))
            // 历史库是唯一按 NORMAL 落盘、且读多写多的库：缓存和 mmap 给到这里，配置/运行库不需要。
            .AddInterceptors(new SqlitePragmaInterceptor(cacheKb: 32_000, mmapBytes: 256L * 1024 * 1024))
            .Options;
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // WAL readers must not take shared-cache table locks that block the archive writer.
        Cache = SqliteCacheMode.Private,
        Pooling = true
    }.ToString();
}
