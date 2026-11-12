using System.Globalization;
using Microsoft.Data.Sqlite;
using Scada.Core.Models;

namespace Scada.Engine.History;

/// <summary>
/// 历史主备复制按表的实际列传输。新增列只要两边库里都有，就会被复制，不再靠手写列清单。
/// 行的第 0 个元素仍是 rowid，供游标使用。没有 Columns 的旧报文按改造前的列解释。
/// </summary>
public static class SqliteHistoryReplica
{
    // 原始历史的"改写流"物理表（见 HistoryDbFactory）：记录被原地更新的 (TagId,Ts)。
    public const string RewriteTable = "HistoryValueRewrite";
    public static readonly string[] LegacyRaw = ["TagId", "Ts", "Value", "Quality"];
    public static readonly string[] LegacyAggregate = ["TagId", "Ts", "Avg", "Min", "Max", "Count"];
    // 与 SqliteHistoryBackend.BuildHistoryInsert 保持同一优先级：CommFailure > Bad > Uncertain > Good。
    private const string QualityPriorityWhere =
        " WHERE CASE excluded.Quality WHEN 2 THEN 3 WHEN 1 THEN 2 WHEN 3 THEN 1 ELSE 0 END" +
        " > CASE HistoryValue.Quality WHEN 2 THEN 3 WHEN 1 THEN 2 WHEN 3 THEN 1 ELSE 0 END";

    public static HistoryReplicaRows Read(SqliteConnection connection, string file, string table, long after, int limit)
    {
        var rewrite = table == RewriteTable;
        var baseTable = rewrite ? "HistoryValue" : table;
        var columns = Columns(connection, baseTable);
        using var cmd = connection.CreateCommand();
        if (rewrite)
        {
            // 改写流与普通行流共用列布局，但游标是改写序号；JOIN 取当前行状态，
            // 这样主机对已写点的质量/值修正也能送到备机。
            var list = string.Join(", ", columns.Select(c => "v." + Quote(c)));
            cmd.CommandText = $"SELECT r.Id, {list} FROM {Quote(RewriteTable)} r JOIN {Quote(baseTable)} v " +
                              $"ON v.TagId = r.TagId AND v.Ts = r.Ts WHERE r.Id > $after ORDER BY r.Id LIMIT $limit";
        }
        else
        {
            cmd.CommandText = $"SELECT rowid, {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)} WHERE rowid > $after ORDER BY rowid LIMIT $limit";
        }
        cmd.Parameters.AddWithValue("$after", after);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
        using var reader = cmd.ExecuteReader();
        var rows = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                {
                    double number => number.ToString("R", CultureInfo.InvariantCulture),
                    string text => text,
                    var value => Convert.ToString(value, CultureInfo.InvariantCulture)
                };
            rows.Add(row);
        }
        return new HistoryReplicaRows(file, table, rows, columns);
    }

    public static void Apply(SqliteConnection connection, string table, HistoryReplicaRows batch)
    {
        var target = table == RewriteTable ? "HistoryValue" : table;
        var local = Columns(connection, target);
        IReadOnlyList<string> source = batch.Columns is { Count: > 0 } columns ? columns : Legacy(target);
        foreach (var name in source)
        {
            if (!local.Contains(name)) throw new HistoryReplicaException($"历史列 {target}.{name} 在本机不存在，已停止复制", 409);
        }
        if (!source.Contains("TagId") || !source.Contains("Ts")) throw new HistoryReplicaException("历史复制缺少 TagId 或 Ts", 400);
        var types = ColumnTypes(connection, target);
        using var cmd = connection.CreateCommand();
        var parameters = source.Select((_, i) => "$c" + i).ToArray();
        // 与主机写入保持同一语义：更差的质量覆盖先到的点，更好的质量不能洗白坏点。
        var conflict = target == "HistoryValue"
            ? " ON CONFLICT(TagId, Ts) DO UPDATE SET Value = excluded.Value, Quality = excluded.Quality, Text = excluded.Text" + QualityPriorityWhere
            : "";
        var verb = target == "HistoryValue" ? "INSERT" : "INSERT OR REPLACE";
        cmd.CommandText = $"{verb} INTO {Quote(target)} ({string.Join(", ", source.Select(Quote))}) VALUES ({string.Join(", ", parameters)}){conflict}";
        for (var i = 0; i < source.Count; i++)
            cmd.Parameters.Add(parameters[i], ParameterType(types.GetValueOrDefault(source[i], "")));
        using var tx = connection.BeginTransaction();
        cmd.Transaction = tx;
        foreach (var row in batch.Rows)
        {
            if (row.Length != source.Count + 1) throw new HistoryReplicaException("历史复制数据格式无效", 400);
            for (var i = 0; i < source.Count; i++)
            {
                var raw = row[i + 1];
                var type = types.GetValueOrDefault(source[i], "");
                cmd.Parameters[i].Value = raw is null ? DBNull.Value
                    : IsInteger(type) ? long.Parse(raw, CultureInfo.InvariantCulture)
                    : IsText(type) ? raw
                    : double.Parse(raw, CultureInfo.InvariantCulture);
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

    private static Dictionary<string, string> ColumnTypes(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = cmd.ExecuteReader();
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) types[reader.GetString(1)] = reader.GetString(2);
        return types;
    }

    private static bool IsInteger(string type) => type.Contains("INT", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(string type) =>
        type.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
        || type.Contains("TEXT", StringComparison.OrdinalIgnoreCase)
        || type.Contains("CLOB", StringComparison.OrdinalIgnoreCase);

    private static SqliteType ParameterType(string type) =>
        IsInteger(type) ? SqliteType.Integer : IsText(type) ? SqliteType.Text : SqliteType.Real;

    public static bool Identifier(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static string Quote(string name)
    {
        if (!Identifier(name)) throw new HistoryReplicaException($"历史标识符无效：{name}", 400);
        return "\"" + name + "\"";
    }
}
