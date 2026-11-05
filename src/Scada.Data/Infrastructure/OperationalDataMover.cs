using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Scada.Data.Infrastructure;

/// <summary>
/// 配置库迁移把运行表搬走时，先按列名复制到旁边的 runtime.db，再让 EF 删除原表。
/// 复制和删除在同一事务里，回滚会一起撤销。内存库没有可附加的文件，调用方要改用临时文件。
/// </summary>
public static class OperationalDataMover
{
    public const string Marker = "scada_move_operational";

    public static readonly string[] Tables =
    [
        "AlarmEvent", "AlarmActivity", "AlarmRollup", "AlarmRollupState", "AlarmNotificationLog",
        "NotificationOutbox", "GatewayInbox", "LocalControlCommand", "GatewayCommand",
        "McpIdempotencyRecord", "OeeLineSample", "OeeRollup", "OeeRollupState"
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
        if (!command.CommandText.Contains(Marker, StringComparison.Ordinal)) return false;
        var source = new SqliteConnectionStringBuilder(command.Connection!.ConnectionString).DataSource;
        var runtimePath = SiblingPath(source);
        if (runtimePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            EnsureRuntime(runtimePath);
            Copy((SqliteConnection)command.Connection, command.Transaction as SqliteTransaction, runtimePath);
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

    private static void Copy(SqliteConnection connection, SqliteTransaction? transaction, string runtimePath)
    {
        // SQLite 事务里不能 DETACH；同一条配置连接再次跑到这里时，runtime 可能还挂着。
        if (!Attached(connection, transaction, "runtime"))
            Exec(connection, transaction, $"ATTACH DATABASE '{runtimePath.Replace("'", "''", StringComparison.Ordinal)}' AS runtime");
        Exec(connection, transaction, "CREATE TABLE IF NOT EXISTS runtime.OperationalImport (Id INTEGER PRIMARY KEY)");
        if (Count(connection, transaction, "SELECT COUNT(*) FROM runtime.OperationalImport") > 0) return;
        foreach (var table in Tables)
        {
            if (Count(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", table) == 0) continue;
            var source = Columns(connection, transaction, $"PRAGMA table_info(\"{table}\")");
            var target = Columns(connection, transaction, $"PRAGMA runtime.table_info(\"{table}\")");
            if (target.Count == 0) throw new InvalidOperationException($"运行库缺少表 {table}");
            var shared = source.Intersect(target).ToArray();
            if (shared.Length == 0) continue;
            var list = string.Join(", ", shared.Select(name => "\"" + name + "\""));
            Exec(connection, transaction, $"INSERT INTO runtime.\"{table}\" ({list}) SELECT {list} FROM \"{table}\"");
        }
        Exec(connection, transaction, "INSERT INTO runtime.OperationalImport (Id) VALUES (1)");
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
