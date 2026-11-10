using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Scada.Data.Infrastructure;

/// <summary>
/// 配置库迁移把运行表搬走时，先按列名复制到旁边的 runtime.db，再让 EF 删除原表。
/// 复制必须可重入：config.db 与 runtime.db 都是 WAL，ATTACH 出来的多库事务在 WAL 下不保证原子提交，
/// 中途失败可能只提交了前几张表。因此逐表用 OR REPLACE 覆盖、复制后按行数校验，重试不会因主键冲突卡死。
/// 内存库没有可附加的文件，调用方要改用临时文件。
/// </summary>
public static class OperationalDataMover
{
    public const string Marker = "scada_move_operational";
    public const string MarkerV2 = "scada_move_operational_v2";

    public static readonly string[] Tables =
    [
        "AlarmEvent", "AlarmActivity", "AlarmRollup", "AlarmRollupState", "AlarmNotificationLog",
        "NotificationOutbox", "GatewayInbox", "LocalControlCommand", "GatewayCommand",
        "McpIdempotencyRecord", "OeeLineSample", "OeeRollup", "OeeRollupState"
    ];

    // 第二批：追溯/配方下发/报表运行/计算状态/停机与质检/控制运行/审计日志，以及网关收流游标与健康。
    // 必须使用独立标记：旧的 MoveOperationalData 迁移仍会在新装时执行，不能把尚未从配置库删除的表提前拷走。
    public static readonly string[] TablesV2 =
    [
        "TraceabilityRecord", "TraceabilityAttempt",
        "RecipeDownloads", "ReportRuns", "ReportApprovals",
        "CalcJobStates", "CalcJobSnapshots",
        "OperationLog", "QualityInspection",
        "DowntimeEvent", "ProductionCountCorrection",
        "ControlSequenceRuns", "ControlRequests", "ControlLockouts", "DeviceControlStates",
        "AnnunciatorSilence",
        "GatewayStream", "GatewayArchiveCursor", "GatewayDeviceHealth"
    ];

    public static string? SiblingPath(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource)) return null;
        if (dataSource is ":memory:" || dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.IsPathRooted(dataSource)) return null;
        var directory = Path.GetDirectoryName(dataSource);
        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "runtime.db");
    }

    public static bool TryApply(SqliteCommand command)
    {
        var wave = command.CommandText.Contains(MarkerV2, StringComparison.Ordinal) ? 2
            : command.CommandText.Contains(Marker, StringComparison.Ordinal) ? 1
            : 0;
        if (wave == 0) return false;
        var source = new SqliteConnectionStringBuilder(command.Connection!.ConnectionString).DataSource;
        var runtimePath = SiblingPath(source);
        if (runtimePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            EnsureRuntime(runtimePath);
            Copy((SqliteConnection)command.Connection, command.Transaction as SqliteTransaction, runtimePath,
                wave == 2 ? TablesV2 : Tables, wave);
        }
        command.CommandText = "SELECT 1";
        command.Parameters.Clear();
        return true;
    }

    public static void EnsureRuntime(string runtimePath)
    {
        // 配置迁移事务可能已经 ATTACH 了这个文件；再开写连接会 database is locked。表齐了就跳过。
        if (File.Exists(runtimePath) && HasOperationalTables(runtimePath)) return;
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = runtimePath, Pooling = false }.ToString())
            .Options;
        using var db = new RuntimeDbContext(options);
        db.Database.Migrate();
    }

    private static bool HasOperationalTables(string runtimePath)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = runtimePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AlarmEvent'";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch (SqliteException)
        {
            // 配置连接可能仍 ATTACH 着这个文件；打不开只读也当成表已就绪，避免再开写连接死锁。
            return true;
        }
    }

    private static void Copy(SqliteConnection connection, SqliteTransaction? transaction, string runtimePath, IReadOnlyList<string> tables, int wave)
    {
        // SQLite 事务里不能 DETACH；同一条配置连接再次跑到这里时，runtime 可能还挂着。
        if (!Attached(connection, transaction, "runtime"))
            Exec(connection, transaction, $"ATTACH DATABASE '{runtimePath.Replace("'", "''", StringComparison.Ordinal)}' AS runtime");
        Exec(connection, transaction, "CREATE TABLE IF NOT EXISTS runtime.OperationalImport (Id INTEGER PRIMARY KEY)");

        // 只有"已经搬过且源表确实空了"才跳过。只看标记不看源表的话，
        // 从旧版本回滚再升级时源表又有了数据，会直接走进 DropTable 把这段数据删掉。
        if (Count(connection, transaction, "SELECT COUNT(*) FROM runtime.OperationalImport WHERE Id = $n", name: wave.ToString()) > 0
            && !HasSourceRows(connection, transaction, tables))
            return;

        foreach (var table in tables)
        {
            if (Count(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", table) == 0) continue;
            var source = Columns(connection, transaction, $"PRAGMA table_info(\"{table}\")");
            var target = Columns(connection, transaction, $"PRAGMA runtime.table_info(\"{table}\")");
            if (target.Count == 0)
                throw new InvalidOperationException($"运行库缺少表 {table}（{runtimePath} 可能被占用或尚未完成迁移），已停止升级以免删表丢数据");
            var shared = source.Intersect(target).ToArray();
            if (shared.Length == 0) continue;
            var list = string.Join(", ", shared.Select(name => "\"" + name + "\""));
            // OR REPLACE：上一次搬迁可能只提交了前半部分表，重试时按主键覆盖已复制的行。
            Exec(connection, transaction, $"INSERT OR REPLACE INTO runtime.\"{table}\" ({list}) SELECT {list} FROM \"{table}\"");

            // 复制后立刻核对行数：列不匹配会静默少拷，等到 DropTable 之后就无法补救了。
            var sourceRows = Count(connection, transaction, $"SELECT COUNT(*) FROM \"{table}\"");
            var targetRows = Count(connection, transaction, $"SELECT COUNT(*) FROM runtime.\"{table}\"");
            if (targetRows < sourceRows)
                throw new InvalidOperationException($"运行表 {table} 搬迁不完整（源 {sourceRows} 行，运行库 {targetRows} 行），已停止升级");
        }
        Exec(connection, transaction, $"INSERT OR REPLACE INTO runtime.OperationalImport (Id) VALUES ({wave})");
    }

    // 源库里是否还有运行表数据：用于识别"标记已写但数据仍留在配置库"的回滚场景。
    private static bool HasSourceRows(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<string> tables)
    {
        foreach (var table in tables)
        {
            if (Count(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", table) == 0) continue;
            if (Count(connection, transaction, $"SELECT COUNT(*) FROM \"{table}\"") > 0) return true;
        }
        return false;
    }

    private static List<string> Columns(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    private static long Count(SqliteConnection connection, SqliteTransaction? transaction, string sql, string? name = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        if (name is not null) cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool Attached(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "PRAGMA database_list";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
