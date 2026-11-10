using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Scada.Server.Ha;

/// <summary>
/// 运行库增量复制：在每张运行表上安装变更触发器，备机按 HaRuntimeChange.Id 游标拉取行，
/// 而不是每个同步周期对整个 runtime.db 做快照和 SHA。
/// 全库快照只用于强制切换/首次引导。
/// </summary>
public sealed record HaRuntimeChangeRow(long Id, string Table, string Key, bool Deleted, IReadOnlyDictionary<string, JsonElement>? Row);

public sealed record HaRuntimeChanges(long After, long Through, bool More, IReadOnlyList<HaRuntimeChangeRow> Changes);

public static class HaRuntimeSync
{
    public const string ChangeTable = "HaRuntimeChange";
    private const string Skip = ",OperationalImport,HaRuntimeChange,__EFMigrationsHistory,sqlite_sequence,";

    public static void EnsureChangeFeed(SqliteConnection connection, bool rebuild = false)
    {
        Exec(connection, $"""
            CREATE TABLE IF NOT EXISTS "{ChangeTable}" (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                T TEXT NOT NULL,
                P TEXT NOT NULL,
                D INTEGER NOT NULL DEFAULT 0
            );
            """);
        foreach (var table in UserTables(connection))
        {
            var keys = PrimaryKeys(connection, table);
            if (keys.Count == 0) continue;
            var predicate = PredicateSql("NEW", keys);
            var deletePredicate = PredicateSql("OLD", keys);
            var safe = table.Replace("\"", "", StringComparison.Ordinal);
            foreach (var (name, eventName, expr) in new[]
            {
                ($"ha_ai_{safe}", "INSERT", predicate),
                ($"ha_au_{safe}", "UPDATE", predicate),
                ($"ha_ad_{safe}", "DELETE", deletePredicate),
            })
            {
                if (rebuild) Exec(connection, $"DROP TRIGGER IF EXISTS \"{name}\"");
                var deleted = eventName == "DELETE" ? 1 : 0;
                Exec(connection, $"""
                    CREATE TRIGGER IF NOT EXISTS "{name}" AFTER {eventName} ON "{safe}"
                    BEGIN
                        INSERT INTO "{ChangeTable}"(T, P, D) VALUES ('{safe}', {expr}, {deleted});
                    END;
                    """);
            }
        }
    }

    public static long Watermark(SqliteConnection connection)
    {
        try { return ReadWatermark(connection); }
        catch (SqliteException)
        {
            EnsureChangeFeed(connection);
            return ReadWatermark(connection);
        }
    }

    private static long ReadWatermark(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT IFNULL(MAX(Id), 0) FROM \"{ChangeTable}\"";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static HaRuntimeChanges Read(SqliteConnection connection, long after, int limit)
    {
        try { return ReadChanges(connection, after, limit); }
        catch (SqliteException)
        {
            EnsureChangeFeed(connection);
            return ReadChanges(connection, after, limit);
        }
    }

    private static HaRuntimeChanges ReadChanges(SqliteConnection connection, long after, int limit)
    {
        if (limit < 1) limit = 1;
        if (limit > 10_000) limit = 10_000;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, T, P, D FROM "{ChangeTable}"
            WHERE Id > $after
            ORDER BY Id
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$after", after);
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<(long Id, string Table, string Key, bool Deleted)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
        }

        var changes = new List<HaRuntimeChangeRow>(rows.Count);
        foreach (var row in rows)
        {
            IReadOnlyDictionary<string, JsonElement>? data = null;
            if (!row.Deleted)
            {
                data = ReadRow(connection, row.Table, row.Key);
                if (data is null)
                    changes.Add(new HaRuntimeChangeRow(row.Id, row.Table, row.Key, true, null));
                else
                    changes.Add(new HaRuntimeChangeRow(row.Id, row.Table, row.Key, false, data));
            }
            else changes.Add(new HaRuntimeChangeRow(row.Id, row.Table, row.Key, true, null));
        }

        var through = rows.Count == 0 ? after : rows[^1].Id;
        return new HaRuntimeChanges(after, through, rows.Count == limit, changes);
    }

    public static long Apply(SqliteConnection connection, HaRuntimeChanges batch)
    {
        if (batch.Changes.Count == 0) return batch.Through;
        using var tx = connection.BeginTransaction();
        foreach (var change in batch.Changes)
        {
            if (change.Deleted)
            {
                using var del = connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM \"{change.Table}\" WHERE {change.Key}";
                del.ExecuteNonQuery();
                continue;
            }

            if (change.Row is null || change.Row.Count == 0) continue;
            var columns = change.Row.Keys.ToArray();
            var names = string.Join(", ", columns.Select(c => "\"" + c + "\""));
            var values = string.Join(", ", columns.Select((_, i) => "$p" + i));
            using var upsert = connection.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = $"INSERT OR REPLACE INTO \"{change.Table}\" ({names}) VALUES ({values})";
            for (var i = 0; i < columns.Length; i++)
                upsert.Parameters.AddWithValue("$p" + i, ToDb(change.Row[columns[i]]));
            upsert.ExecuteNonQuery();
        }

        tx.Commit();
        return batch.Through;
    }

    public static void Trim(SqliteConnection connection, long keepAfter)
    {
        if (keepAfter <= 0) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{ChangeTable}\" WHERE Id < $keep";
        cmd.Parameters.AddWithValue("$keep", keepAfter);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadRow(SqliteConnection connection, string table, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\" WHERE {key} LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (reader.IsDBNull(i)) { map[name] = JsonSerializer.SerializeToElement((object?)null); continue; }
            map[name] = reader.GetFieldType(i) switch
            {
                var t when t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte)
                    => JsonSerializer.SerializeToElement(reader.GetInt64(i)),
                var t when t == typeof(double) || t == typeof(float) || t == typeof(decimal)
                    => JsonSerializer.SerializeToElement(reader.GetDouble(i)),
                var t when t == typeof(bool) => JsonSerializer.SerializeToElement(reader.GetBoolean(i)),
                var t when t == typeof(byte[]) => JsonSerializer.SerializeToElement(Convert.ToBase64String((byte[])reader.GetValue(i))),
                _ => JsonSerializer.SerializeToElement(reader.GetValue(i)?.ToString()),
            };
        }
        return map;
    }

    private static object ToDb(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return DBNull.Value;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String => value.GetString() ?? (object)DBNull.Value,
            _ => value.GetRawText(),
        };
    }

    private static List<string> UserTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (Skip.Contains("," + name + ",", StringComparison.Ordinal)) continue;
            list.Add(name);
        }
        return list;
    }

    private static List<string> PrimaryKeys(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        var keys = new List<(int Seq, string Name)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(5) != 0) keys.Add((checked((int)reader.GetInt64(5)), reader.GetString(1)));
        }
        return keys.OrderBy(x => x.Seq).Select(x => x.Name).ToList();
    }

    private static string PredicateSql(string alias, IReadOnlyList<string> keys)
    {
        // 触发器里用 quote() 生成可直接用于 WHERE 的谓词，避免再解析主键。
        var parts = keys.Select(c => $"'{c}=' || quote({alias}.\"{c}\")");
        return string.Join(" || ' AND ' || ", parts);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
