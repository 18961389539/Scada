using Microsoft.Data.Sqlite;

namespace Scada.Data.Tests;

/// <summary>每个用例一个临时目录；结束时清池再删，避免 Windows 上文件被占用。</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scada-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(string name)
    {
        var full = System.IO.Path.Combine(Root, name);
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return full;
    }

    public SqliteConnection Open(string name, bool foreignKeys = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path(name),
            Pooling = false
        }.ConnectionString);
        connection.Open();
        // Microsoft.Data.Sqlite 默认就打开外键，必须显式关掉才是 SQLite 的原始语义。
        Exec(connection, foreignKeys ? "PRAGMA foreign_keys=ON" : "PRAGMA foreign_keys=OFF");
        return connection;
    }

    public static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
