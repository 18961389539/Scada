using Microsoft.Data.Sqlite;
using Scada.Core.Models;

namespace Scada.Engine.History;

/// <summary>
/// 历史主备复制按表的实际列传输。新增列只要两边库里都有，就会被复制，不再靠手写列清单。
/// 行的第 0 个元素仍是 rowid，供游标使用。没有 Columns 的旧报文按改造前的列解释。
/// </summary>
public static class SqliteHistoryReplica
{
    public static readonly string[] LegacyRaw = ["TagId", "Ts", "Value", "Quality"];
    public static readonly string[] LegacyAggregate = ["TagId", "Ts", "Avg", "Min", "Max", "Count"];

    public static HistoryReplicaRows Read(SqliteConnection connection, string file, string table, long after, int limit)
    {
        var columns = Columns(connection, table);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT rowid, {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)} WHERE rowid > $after ORDER BY rowid LIMIT $limit";
        cmd.Parameters.AddWithValue("$after", after);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
        using var reader = cmd.ExecuteReader();
        var rows = new List<double[]>();
        while (reader.Read())
        {
            var row = new double[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? double.NaN : reader.GetDouble(i);
            rows.Add(row);
        }
        return new HistoryReplicaRows(file, table, rows, columns);
    }

    public static void Apply(SqliteConnection connection, string table, HistoryReplicaRows batch)
    {
        var local = Columns(connection, table);
        IReadOnlyList<string> source = batch.Columns is { Count: > 0 } columns ? columns : Legacy(table);
        foreach (var name in source)
        {
            if (!local.Contains(name)) throw new HistoryReplicaException($"历史列 {table}.{name} 在本机不存在，已停止复制", 409);
        }
        if (!source.Contains("TagId") || !source.Contains("Ts")) throw new HistoryReplicaException("历史复制缺少 TagId 或 Ts", 400);
        var integer = IntegerColumns(connection, table);
        using var cmd = connection.CreateCommand();
        var parameters = source.Select((_, i) => "$c" + i).ToArray();
        var conflict = table == "HistoryValue" ? " ON CONFLICT(TagId, Ts) DO NOTHING" : "";
        var verb = table == "HistoryValue" ? "INSERT" : "INSERT OR REPLACE";
        cmd.CommandText = $"{verb} INTO {Quote(table)} ({string.Join(", ", source.Select(Quote))}) VALUES ({string.Join(", ", parameters)}){conflict}";
        for (var i = 0; i < source.Count; i++)
            cmd.Parameters.Add(parameters[i], integer.Contains(source[i]) ? SqliteType.Integer : SqliteType.Real);
        using var tx = connection.BeginTransaction();
        cmd.Transaction = tx;
        foreach (var row in batch.Rows)
        {
            if (row.Length != source.Count + 1) throw new HistoryReplicaException("历史复制数据格式无效", 400);
            for (var i = 0; i < source.Count; i++)
            {
                var value = row[i + 1];
                cmd.Parameters[i].Value = double.IsNaN(value)
                    ? DBNull.Value
                    : integer.Contains(source[i]) ? (long)value : value;
            }
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static IReadOnlyList<string> Legacy(string table) => table == "HistoryValue" ? LegacyRaw : LegacyAggregate;

    public static IReadOnlyList<string> Columns(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (!Identifier(name)) throw new HistoryReplicaException($"历史列名无效：{name}", 400);
            names.Add(name);
        }
        if (names.Count == 0) throw new HistoryReplicaException($"历史表 {table} 不存在", 400);
        return names;
    }

    private static HashSet<string> IntegerColumns(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var type = reader.GetString(2);
            if (type.Contains("INT", StringComparison.OrdinalIgnoreCase)) names.Add(reader.GetString(1));
        }
        return names;
    }

    public static bool Identifier(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static string Quote(string name)
    {
        if (!Identifier(name)) throw new HistoryReplicaException($"历史标识符无效：{name}", 400);
        return "\"" + name + "\"";
    }
}
