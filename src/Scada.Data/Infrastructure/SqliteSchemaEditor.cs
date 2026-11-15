using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Scada.Data.Infrastructure;

/// <summary>
/// SQLite 不能按 EF 的默认方式删除列、外键和检查约束。这里把这些操作改成
/// ALTER TABLE DROP COLUMN，做不到时按 sqlite_master 重建表。迁移因此可以真正往回走。
/// </summary>
public sealed class ScadaSqliteMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    IRelationalAnnotationProvider annotations)
    : SqliteMigrationsSqlGenerator(dependencies, annotations)
{
    protected override void Generate(DropColumnOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        try { base.Generate(operation, model, builder, terminate); }
        catch (NotSupportedException) { SqliteSchemaEditor.Emit(builder, new SchemaEdit("dropColumn", operation.Table, operation.Name)); }
    }

    protected override void Generate(DropForeignKeyOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        try { base.Generate(operation, model, builder, terminate); }
        catch (NotSupportedException) { SqliteSchemaEditor.Emit(builder, new SchemaEdit("dropConstraint", operation.Table, operation.Name)); }
    }

    protected override void Generate(DropCheckConstraintOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        try { base.Generate(operation, model, builder); }
        catch (NotSupportedException) { SqliteSchemaEditor.Emit(builder, new SchemaEdit("dropConstraint", operation.Table, operation.Name)); }
    }

    protected override void Generate(AddCheckConstraintOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        try { base.Generate(operation, model, builder); }
        catch (NotSupportedException) { SqliteSchemaEditor.Emit(builder, new SchemaEdit("addCheck", operation.Table, operation.Name, operation.Sql)); }
    }

    protected override void Generate(AlterColumnOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        try { base.Generate(operation, model, builder); }
        catch (NotSupportedException)
        {
            SqliteSchemaEditor.Emit(builder, new SchemaEdit("alterNull", operation.Table, operation.Name, Nullable: operation.IsNullable, DefaultSql: operation.DefaultValueSql ?? Literal(operation.DefaultValue)));
        }
    }

    private static string? Literal(object? value) => value switch
    {
        null => null,
        bool bit => bit ? "1" : "0",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => null
    };
}

public sealed record SchemaEdit(string Op, string Table, string? Name, string? Sql = null, bool? Nullable = null, string? DefaultSql = null);

public static partial class SqliteSchemaEditor
{
    public const string Marker = "scada_schema";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // 重建表必须先关外键，否则 DROP/RENAME 会顺着外键改写引用并级联删掉子表数据。
    // 而 foreign_keys 不能在事务里改，所以围栏必须以 suppressTransaction 单独下发，
    // 由 Migrator 在事务外执行（EF Core 自己的 SQLite 表重建也是这个做法）。
    private static readonly string[] RebuildOps = ["dropColumn", "dropConstraint", "addCheck", "alterNull"];

    public static void Emit(MigrationCommandListBuilder builder, SchemaEdit edit)
    {
        if (!RebuildOps.Contains(edit.Op))
        {
            EmitEdit(builder, edit);
            return;
        }
        EmitPragma(builder, "foreign_keys", "0");
        EmitEdit(builder, edit);
        EmitPragma(builder, "foreign_keys", "1");
    }

    private static void EmitEdit(MigrationCommandListBuilder builder, SchemaEdit edit)
    {
        var payload = JsonSerializer.Serialize(edit, Json).Replace("'", "''", StringComparison.Ordinal);
        builder.Append("SELECT '").Append(payload).Append("' AS ").Append(Marker).EndCommand();
    }

    private static void EmitPragma(MigrationCommandListBuilder builder, string name, string value)
    {
        var payload = JsonSerializer.Serialize(new SchemaEdit("pragma", "", name, value), Json);
        builder.Append("SELECT '").Append(payload).Append("' AS ").Append(Marker).EndCommand(suppressTransaction: true);
    }

    public static bool TryApply(SqliteCommand command)
    {
        if (command.CommandText.Contains(Marker, StringComparison.Ordinal) == false) return false;
        var match = Payload().Match(command.CommandText);
        if (!match.Success) return false;
        var edit = JsonSerializer.Deserialize<SchemaEdit>(match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal), Json)
            ?? throw new InvalidOperationException("无法解析 SQLite 结构操作");
        var connection = (SqliteConnection)command.Connection!;
        Apply(connection, command.Transaction, edit);
        command.CommandText = "SELECT 1";
        command.Parameters.Clear();
        return true;
    }

    public static void Apply(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, SchemaEdit edit)
    {
        if (edit.Op == "pragma")
        {
            ApplyPragma(connection, edit);
            return;
        }
        if (!HistoryReplicaIdentifier(edit.Table) || (edit.Name is not null && !HistoryReplicaIdentifier(edit.Name)))
            throw new InvalidOperationException($"SQLite 标识符无效：{edit.Table}.{edit.Name}");
        switch (edit.Op)
        {
            case "dropColumn":
                DropColumn(connection, transaction, edit.Table, edit.Name!);
                break;
            case "dropConstraint":
                Rebuild(connection, transaction, edit.Table, sql => RemovePart(sql, part => part.Contains($"CONSTRAINT \"{edit.Name}\"", StringComparison.Ordinal)), null);
                break;
            case "addCheck":
                Rebuild(connection, transaction, edit.Table, sql => InsertPart(sql, $"CONSTRAINT \"{edit.Name}\" CHECK ({edit.Sql})"), null);
                break;
            case "alterNull":
                if (edit.Nullable == false && edit.DefaultSql is not null)
                    Exec(connection, transaction, $"UPDATE \"{edit.Table}\" SET \"{edit.Name}\" = {edit.DefaultSql} WHERE \"{edit.Name}\" IS NULL");
                Rebuild(connection, transaction, edit.Table, sql => AlterNull(sql, edit.Name!, edit.Nullable == true), null);
                break;
            default:
                throw new InvalidOperationException($"未知的 SQLite 结构操作：{edit.Op}");
        }
    }

    private static void ApplyPragma(SqliteConnection connection, SchemaEdit edit)
    {
        // 只放行白名单：payload 来自迁移脚本，但仍然是一个可执行的 SQL 入口。
        if (edit.Name is not ("foreign_keys" or "legacy_alter_table"))
            throw new InvalidOperationException($"不允许的 pragma：{edit.Name}");
        if (edit.Sql is not ("0" or "1"))
            throw new InvalidOperationException($"pragma 取值无效：{edit.Name}={edit.Sql}");
        Exec(connection, null, $"PRAGMA {edit.Name}={edit.Sql}");
    }

    private static void DropColumn(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string table, string column)
    {
        // 失败的语句会使 SQLite 事务进入中止状态，必须先回到保存点再重建表。
        Exec(connection, transaction, "SAVEPOINT scada_schema");
        try
        {
            Exec(connection, transaction, $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\"");
            Exec(connection, transaction, "RELEASE scada_schema");
        }
        catch (SqliteException)
        {
            Exec(connection, transaction, "ROLLBACK TO scada_schema");
            Exec(connection, transaction, "RELEASE scada_schema");
            Rebuild(connection, transaction, table, sql => RemovePart(sql, part => IsColumn(part, column) || (part.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase) && part.Contains($"\"{column}\"", StringComparison.Ordinal))), column);
        }
    }

    private static void Rebuild(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string table, Func<string, string> edit, string? droppedColumn)
    {
        // 外键开着时，RENAME 会把所有子表的 REFERENCES 改到旧表名，随后的 DROP 立刻级联删空子表；
        // legacy_alter_table 挡不住这一条。宁可在这里大声失败，也不能静默删数据。
        if (ScalarInt(connection, transaction, "PRAGMA foreign_keys") != 0)
            throw new InvalidOperationException(
                $"重建表 {table} 前必须先关闭 foreign_keys（迁移脚本会以事务外 pragma 围栏下发），当前连接仍是开启状态");

        var create = Scalar(connection, transaction, "SELECT sql FROM sqlite_master WHERE type='table' AND name=$n", table)
            ?? throw new InvalidOperationException($"表 {table} 不存在");
        var indexes = List(connection, transaction, "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=$n AND sql IS NOT NULL", table);
        var triggers = List(connection, transaction, "SELECT sql FROM sqlite_master WHERE type='trigger' AND tbl_name=$n AND sql IS NOT NULL", table);
        var legacy = table + "__scada_old";
        // 三段式而不是"建临时表→拷→删原表→改名"：外键开启时删父表会执行隐式 DELETE 并触发级联，
        // 重建 Tag 这样的被引用表会把子表数据一并删掉。改成"原表改名→建新表→拷→删旧表"，
        // 子表的外键子句始终指向原表名，任何时刻都没有表被级联删除。
        // legacy_alter_table=ON 让 RENAME 只改表名、不改写其他表/触发器/视图里的引用（默认会改写）。
        var previousLegacy = ScalarInt(connection, transaction, "PRAGMA legacy_alter_table");
        Exec(connection, transaction, "PRAGMA legacy_alter_table=ON");
        try
        {
            Exec(connection, transaction, $"ALTER TABLE \"{table}\" RENAME TO \"{legacy}\"");
            Exec(connection, transaction, edit(create));
            var shared = Columns(connection, transaction, legacy).Intersect(Columns(connection, transaction, table)).ToArray();
            var list = string.Join(", ", shared.Select(name => "\"" + name + "\""));
            // 显式带上 rowid：HA 历史复制按 rowid 增量拉取（SqliteHistoryReplica），
            // 重建若重排 rowid，备机已推进的游标会静默跳过数据，且没有任何报错。
            Exec(connection, transaction, $"INSERT INTO \"{table}\" (rowid, {list}) SELECT rowid, {list} FROM \"{legacy}\"");
            Exec(connection, transaction, $"DROP TABLE \"{legacy}\"");
        }
        finally
        {
            Exec(connection, transaction, previousLegacy == 0 ? "PRAGMA legacy_alter_table=OFF" : "PRAGMA legacy_alter_table=ON");
        }
        foreach (var sql in indexes)
        {
            if (droppedColumn is not null && sql.Contains($"\"{droppedColumn}\"", StringComparison.Ordinal)) continue;
            Exec(connection, transaction, sql);
        }
        foreach (var sql in triggers) Exec(connection, transaction, sql);
    }

    private static string RemovePart(string create, Func<string, bool> remove)
    {
        var (prefix, parts, suffix) = Split(create);
        return prefix + string.Join(", ", parts.Where(part => !remove(part))) + suffix;
    }

    private static string InsertPart(string create, string part)
    {
        var (prefix, parts, suffix) = Split(create);
        return prefix + string.Join(", ", parts.Append(part)) + suffix;
    }

    private static string AlterNull(string create, string column, bool nullable)
    {
        var (prefix, parts, suffix) = Split(create);
        var next = parts.Select(part => IsColumn(part, column) ? SetNullable(part, nullable) : part);
        return prefix + string.Join(", ", next) + suffix;
    }

    private static bool IsColumn(string part, string column) =>
        part.StartsWith($"\"{column}\"", StringComparison.Ordinal);

    private static string SetNullable(string columnSql, bool nullable)
    {
        if (nullable)
            return columnSql.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase)
                ? NotNull().Replace(columnSql, "NULL")
                : columnSql;
        if (columnSql.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase)) return columnSql;
        var stripped = NullWord().Replace(columnSql, " ");
        var def = DefaultClause().Match(stripped);
        return def.Success
            ? stripped[..def.Index].TrimEnd() + " NOT NULL " + stripped[def.Index..]
            : stripped.TrimEnd() + " NOT NULL";
    }

    private static (string Prefix, List<string> Parts, string Suffix) Split(string create)
    {
        var open = create.IndexOf('(');
        var close = create.LastIndexOf(')');
        if (open < 0 || close < open) throw new InvalidOperationException("无法解析建表语句");
        return (create[..(open + 1)], SplitComma(create[(open + 1)..close]), create[close..]);
    }

    // 拆分列定义原始取自 CREATE TABLE 文本，必须按 SQL 词法走：
    // 只认双引号时，DEFAULT ',' 或 CHECK (x IN ('a','b')) 里的逗号会被当成分隔符，切出来的
    // DDL 仍能建表但约束/默认值会静默丢失。这里同时处理单引号、反引号、方括号、转义重复引号与行注释。
    private static List<string> SplitComma(string body)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    // SQL 用重复同一个引号字符来转义（如 'it''s'）。
                    if (i + 1 < body.Length && body[i + 1] == quote && quote != ']') { current.Append(body[++i]); continue; }
                    quote = '\0';
                }
                continue;
            }

            if (c is '"' or '\'' or '`') { quote = c; current.Append(c); continue; }
            if (c == '[') { quote = ']'; current.Append(c); continue; }
            if (c == '-' && i + 1 < body.Length && body[i + 1] == '-')
            {
                while (i < body.Length && body[i] != '\n') current.Append(body[i++]);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && c == ',')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString().Trim());
        return parts;
    }

    private static List<string> Columns(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction as SqliteTransaction;
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    private static void Exec(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction as SqliteTransaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string sql, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction as SqliteTransaction;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() as string;
    }

    private static long ScalarInt(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction as SqliteTransaction;
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static List<string> List(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string sql, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction as SqliteTransaction;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$n", name);
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) if (!reader.IsDBNull(0)) rows.Add(reader.GetString(0));
        return rows;
    }

    private static bool HistoryReplicaIdentifier(string name) =>
        name.Length is > 0 and <= 80 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    [GeneratedRegex(@"SELECT '((?:[^']|'')*)' AS scada_schema")]
    private static partial Regex Payload();

    [GeneratedRegex(@"\bNOT NULL\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotNull();

    [GeneratedRegex(@"\bNULL\b", RegexOptions.IgnoreCase)]
    private static partial Regex NullWord();

    [GeneratedRegex(@"\bDEFAULT\b", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultClause();
}

public sealed class SqliteSchemaCommandInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<int> NonQueryExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (command is SqliteCommand sqlite) Apply(sqlite);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command is SqliteCommand sqlite) Apply(sqlite);
        return new(result);
    }

    private static void Apply(SqliteCommand command)
    {
        if (!SqliteSchemaEditor.TryApply(command)) OperationalDataMover.TryApply(command);
    }
}
