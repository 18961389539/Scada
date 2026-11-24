using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Scada.Server.Ha;

/// <summary>
/// 运行库增量复制：在每张运行表上安装变更触发器，备机按 HaRuntimeChange.Id 游标拉取行，
/// 而不是每个同步周期对整个 runtime.db 做快照和 SHA。
/// 全库快照只用于强制切换/首次引导。
/// </summary>
public sealed record HaRuntimeChangeRow(long Id, string Table, string Key, bool Deleted, IReadOnlyDictionary<string, JsonElement>? Row);

public sealed record HaRuntimeChanges(long After, long Through, bool More, IReadOnlyList<HaRuntimeChangeRow> Changes, long Oldest = 0, bool Gap = false, long Pending = 0, IReadOnlyList<string>? PendingTables = null);

public static class HaRuntimeSync
{
    public const string ChangeTable = "HaRuntimeChange";
    public const string SyncStateTable = "HaSyncState";
    private const string Skip = ",OperationalImport,HaRuntimeChange,HaSyncState,__EFMigrationsHistory,sqlite_sequence,";

    public static void EnsureChangeFeed(SqliteConnection connection, bool rebuild = false)
    {
        // 旧版本的触发器没有 Applying 守卫；HaSyncState 之前不存在说明是旧结构，必须重建一次。
        var existed = TableExists(connection, SyncStateTable);
        Exec(connection, $"""
            CREATE TABLE IF NOT EXISTS "{ChangeTable}" (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                T TEXT NOT NULL,
                P TEXT NOT NULL,
                D INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS "{SyncStateTable}" (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Applying INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO "{SyncStateTable}"(Id, Applying) VALUES (1, 0);
            """);
        var rebuildTriggers = rebuild || !existed;
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
                if (rebuildTriggers) Exec(connection, $"DROP TRIGGER IF EXISTS \"{name}\"");
                var deleted = eventName == "DELETE" ? 1 : 0;
                Exec(connection, $"""
                    CREATE TRIGGER IF NOT EXISTS "{name}" AFTER {eventName} ON "{safe}"
                    WHEN (SELECT Applying FROM "{SyncStateTable}" WHERE Id = 1) = 0
                    BEGIN
                        INSERT INTO "{ChangeTable}"(T, P, D) VALUES ('{safe}', {expr}, {deleted});
                    END;
                    """);
            }
        }
    }

    /// <summary>
    /// 备机回放前调用：移除本机触发器。否则回放的 INSERT OR REPLACE 会再次写进自己的变更流，
    /// 备机日志无限增长，切换后两台机器互相回灌。
    /// </summary>
    public static void PrepareReplica(SqliteConnection connection)
    {
        Exec(connection, $"""
            CREATE TABLE IF NOT EXISTS "{SyncStateTable}" (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Applying INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO "{SyncStateTable}"(Id, Applying) VALUES (1, 0);
            """);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND (name LIKE 'ha_ai_%' OR name LIKE 'ha_au_%' OR name LIKE 'ha_ad_%')";
        var names = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        foreach (var name in names)
            Exec(connection, $"DROP TRIGGER IF EXISTS \"{name.Replace("\"", "", StringComparison.Ordinal)}\"");
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
        // 主机按水位裁剪旧窗；游标落在最早可用行之前说明中间有已删行，不能假装连续。
        var oldest = OldestId(connection);
        var gap = after > 0 && oldest > 0 && after + 1 < oldest;
        var pending = SummarizeCore(connection, through);
        return new HaRuntimeChanges(after, through, rows.Count == limit, changes, oldest, gap, pending.Count, pending.Tables);
    }

    public static (long Count, IReadOnlyList<string> Tables) Summarize(SqliteConnection connection, long after)
    {
        try { return SummarizeCore(connection, after); }
        catch (SqliteException)
        {
            EnsureChangeFeed(connection);
            return SummarizeCore(connection, after);
        }
    }

    private static (long Count, IReadOnlyList<string> Tables) SummarizeCore(SqliteConnection connection, long after)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), GROUP_CONCAT(DISTINCT T) FROM \"" + ChangeTable + "\" WHERE Id > $after";
        cmd.Parameters.AddWithValue("$after", after);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, []);
        var count = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        var raw = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var tables = string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (count, tables);
    }

    public static long Apply(SqliteConnection connection, HaRuntimeChanges batch)
    {
        if (batch.Changes.Count == 0) return batch.Through;
        Exec(connection, $"""
            CREATE TABLE IF NOT EXISTS "{SyncStateTable}" (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Applying INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO "{SyncStateTable}"(Id, Applying) VALUES (1, 0);
            """);
        using var tx = connection.BeginTransaction();
        // 即使触发器还在（例如从主机降级的节点），回放本身也不应产生新的变更行。
        SetApplying(connection, tx, 1);
        foreach (var change in batch.Changes)
        {
            // peer 传来的 Table / Key / 列名都不可信：回放端必须自己校验并参数化，
            // 不能把主机生成的 WHERE 片段当成"内部数据"直接拼进 SQL。
            var table = SafeTable(connection, change.Table);
            if (!TryParseKey(change.Key, out var keyTerms))
                throw new InvalidOperationException($"HA 回放主键谓词无效：{change.Key}");
            if (change.Deleted)
            {
                using var del = connection.CreateCommand();
                del.Transaction = tx;
                var where = WhereSql(keyTerms, del, "k");
                del.CommandText = $"DELETE FROM \"{table}\" WHERE {where}";
                del.ExecuteNonQuery();
                continue;
            }

            if (change.Row is null || change.Row.Count == 0) continue;
            var columns = change.Row.Keys.ToArray();
            if (columns.Length is 0 or > 256 || columns.Any(c => !IsIdentifier(c)))
                throw new InvalidOperationException($"HA 回放列名无效：{change.Table}");
            var names = string.Join(", ", columns.Select(c => "\"" + c + "\""));
            var values = string.Join(", ", columns.Select((_, i) => "$p" + i));
            using var upsert = connection.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = $"INSERT OR REPLACE INTO \"{table}\" ({names}) VALUES ({values})";
            for (var i = 0; i < columns.Length; i++)
                upsert.Parameters.AddWithValue("$p" + i, ToDb(change.Row[columns[i]]));
            upsert.ExecuteNonQuery();
        }

        SetApplying(connection, tx, 0);
        tx.Commit();
        return batch.Through;
    }

    private static void SetApplying(SqliteConnection connection, SqliteTransaction tx, int value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE \"{SyncStateTable}\" SET Applying = $value WHERE Id = 1";
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public static void Trim(SqliteConnection connection, long keepAfter)
    {
        if (keepAfter <= 0) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{ChangeTable}\" WHERE Id < $keep";
        cmd.Parameters.AddWithValue("$keep", keepAfter);
        cmd.ExecuteNonQuery();
    }

    private static long OldestId(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT IFNULL(MIN(Id), 0) FROM \"{ChangeTable}\"";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    // ── 回放输入的白名单 ──────────────────────────────────────────────────────
    // 主机生成的谓词形如 Id=2 或 Id='a''b' AND Slot=NULL（PredicateSql 用 quote()）。
    // 只接受这种严格语法，再拆成「列名 + 字面量」用参数重建 WHERE；任何 SQL 片段
    // （OR 1=1、注释、子查询、分号）都在这里被拒绝，整批回放回滚。
    private static readonly Regex KeyTerm = new(
        @"^([A-Za-z_][A-Za-z0-9_]*)=('(?:[^']|'')*'|X'(?:[0-9A-Fa-f]{2})*'|-?\d+(?:\.\d+)?|NULL)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsIdentifier(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 80
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string SafeTable(SqliteConnection connection, string? table)
    {
        // 变更流自身、同步状态表等不允许被回放；表还必须在本机 schema 里真实存在。
        if (!IsIdentifier(table) || Skip.Contains("," + table + ",", StringComparison.Ordinal) || !TableExists(connection, table!))
            throw new InvalidOperationException($"HA 回放目标表无效：{table}");
        return table!;
    }

    private static bool TryParseKey(string? key, out List<(string Column, object? Value)> terms)
    {
        terms = [];
        if (string.IsNullOrEmpty(key) || key.Length > 4096) return false;
        var position = 0;
        while (true)
        {
            var match = KeyTerm.Match(key[position..]);
            if (!match.Success) return false;
            var literal = match.Groups[2].Value;
            object? value;
            if (literal == "NULL") value = null;
            else if (literal[0] == '\'') value = literal[1..^1].Replace("''", "'", StringComparison.Ordinal);
            else if (literal[0] == 'X') value = Convert.FromHexString(literal[2..^1]);
            else if (literal.Contains('.', StringComparison.Ordinal)) value = double.Parse(literal, CultureInfo.InvariantCulture);
            else value = long.Parse(literal, CultureInfo.InvariantCulture);
            terms.Add((match.Groups[1].Value, value));
            position += match.Length;
            if (position == key.Length) return terms.Count is > 0 and <= 16;
            if (!key.AsSpan(position).StartsWith(" AND ", StringComparison.Ordinal)) return false;
            position += 5;
        }
    }

    private static string WhereSql(IReadOnlyList<(string Column, object? Value)> terms, SqliteCommand command, string prefix)
    {
        var parts = new List<string>(terms.Count);
        for (var i = 0; i < terms.Count; i++)
        {
            var (column, value) = terms[i];
            if (value is null) { parts.Add($"\"{column}\" IS NULL"); continue; }
            var parameter = "$" + prefix + i;
            parts.Add($"\"{column}\" = {parameter}");
            command.Parameters.AddWithValue(parameter, value);
        }
        return string.Join(" AND ", parts);
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadRow(SqliteConnection connection, string table, string key)
    {
        var safe = SafeTable(connection, table);
        if (!TryParseKey(key, out var terms))
            throw new InvalidOperationException($"HA 回放主键谓词无效：{key}");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{safe}\" WHERE {WhereSql(terms, cmd, "k")} LIMIT 1";
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
        // 表名来自本机 sqlite_master，但触发器名与 PRAGMA 里仍要拼接它：只接受标识符形态。
        if (!IsIdentifier(table)) return [];
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
        if (keys.Count == 0 || keys.Any(k => !IsIdentifier(k)))
            throw new InvalidOperationException("HA 变更触发器遇到非法主键列名");
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
