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
    string GetRawPath(int year, int month);
    string GetAggregatePath(int year);
    void EnsureCreated(HistoryDbContext context);
    void ReleaseFile(string path) { }
}

public sealed class HistoryDbFactory : IHistoryDbFactory
{
    private readonly ScadaSettings _settings;
    private readonly ConcurrentDictionary<string, bool> _initialized = new(StringComparer.OrdinalIgnoreCase);
    // 每个历史文件一把锁：新月份首次被并发查询时，两个请求会同时跑 Migrate()，
    // SQLite 上表现为 database is locked 或 table already exists，且只在月初偶发。
    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.OrdinalIgnoreCase);

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

    public void ReleaseFile(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        SqliteConnection.ClearPool(connection);
        _initialized.TryRemove(path, out _);
    }

    public void EnsureCreated(HistoryDbContext context)
    {
        var path = context.Database.GetDbConnection().DataSource;
        if (_initialized.ContainsKey(path))
        {
            return;
        }

        // 锁内二次检查：迁移与业务查询都在调用方自己的上下文/线程上完成，
        // 不跨线程复用 DbContext（EF 明确不支持）。不同文件各用各的锁，互不阻塞。
        lock (_gates.GetOrAdd(path, _ => new object()))
        {
            if (_initialized.ContainsKey(path))
            {
                return;
            }

            context.Database.Migrate();
            AlignHourWindows(context, path);
            _initialized.TryAdd(path, true);
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

    private static void Execute(System.Data.Common.DbConnection connection, string sql, long? offset = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (offset is long value) command.Parameters.Add(new SqliteParameter("$offset", value));
        command.ExecuteNonQuery();
    }

    private HistoryDbContext Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var options = new DbContextOptionsBuilder<HistoryDbContext>()
            .UseSqlite(ConnectionString(path))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        var context = new HistoryDbContext(options);
        EnsureCreated(context);
        return context;
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
