using Microsoft.Data.Sqlite;
using Scada.Data.Infrastructure;

namespace Scada.Data.Tests;

/// <summary>
/// SQLite 的表重建是"改名 → 建新表 → 拷数据 → 删旧表"，三件事最容易被写错：
/// 外键开着时 RENAME 会把子表 REFERENCES 改到旧表名、DROP 随即级联删空子表；
/// 拷贝时若不显式带 rowid，历史库的 HA 复制游标（WHERE rowid &gt; after）会静默跳过数据；
/// 拆列定义时若不认单引号，DEFAULT ',' 会被当成分隔符切坏 DDL。
/// 迁移脚本用"事务外 pragma 围栏"关外键，因此这里的用例也按同样姿势打开连接。
/// </summary>
public sealed class SqliteSchemaEditorTests
{
    private const string Ddl = """
        CREATE TABLE "Parent" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Parent" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL
        );
        CREATE TABLE "Child" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Child" PRIMARY KEY AUTOINCREMENT,
            "ParentId" INTEGER NOT NULL,
            CONSTRAINT "FK_Child_Parent_ParentId" FOREIGN KEY ("ParentId") REFERENCES "Parent" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX "IX_Child_ParentId" ON "Child" ("ParentId");
        INSERT INTO "Parent" ("Name") VALUES ('a'), ('b'), ('c');
        INSERT INTO "Child" ("ParentId") VALUES (1), (1), (2);
        """;

    [Fact]
    public void RebuildKeepsReferencingRowsWhenForeignKeysAreDisabled()
    {
        using var root = new TempRoot();
        using var connection = OpenWithoutForeignKeys(root, "schema.db");
        TempRoot.Exec(connection, Ddl);

        SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("addCheck", "Parent", "CK_Parent_Id", "\"Id\" > 0"));

        Assert.Equal(3L, TempRoot.Scalar(connection, "SELECT COUNT(*) FROM \"Child\""));
        Assert.Equal(3L, TempRoot.Scalar(connection, "SELECT COUNT(*) FROM \"Parent\""));
    }

    [Fact]
    public void RebuildRefusesToRunWhileForeignKeysAreEnabled()
    {
        using var root = new TempRoot();
        using var connection = root.Open("fk-on.db");
        TempRoot.Exec(connection, Ddl);

        // 宁可大声失败：静默执行会把 Child 的 3 行连同外键一起删掉。
        var error = Assert.Throws<InvalidOperationException>(
            () => SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("addCheck", "Parent", "CK_Parent_Id", "\"Id\" > 0")));
        Assert.Contains("foreign_keys", error.Message, StringComparison.Ordinal);
        Assert.Equal(3L, TempRoot.Scalar(connection, "SELECT COUNT(*) FROM \"Child\""));
    }

    [Fact]
    public void RebuildPreservesRowIdsAndSequence()
    {
        using var root = new TempRoot();
        using var connection = OpenWithoutForeignKeys(root, "rowid.db");
        TempRoot.Exec(connection, Ddl);
        var before = RowIds(connection);

        SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("addCheck", "Parent", "CK_Parent_Id", "\"Id\" > 0"));

        Assert.Equal(before, RowIds(connection));
        TempRoot.Exec(connection, "INSERT INTO \"Parent\" (\"Name\") VALUES ('d')");
        // 序列也要跟着走，否则新行会复用已被删掉的 rowid，备机永远看不到。
        Assert.Equal(before.Max() + 1, TempRoot.Scalar(connection, "SELECT MAX(rowid) FROM \"Parent\""));
    }

    [Fact]
    public void RebuildAppliesTheNewCheckConstraint()
    {
        using var root = new TempRoot();
        using var connection = OpenWithoutForeignKeys(root, "check.db");
        TempRoot.Exec(connection, Ddl);

        SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("addCheck", "Parent", "CK_Parent_Id", "\"Id\" > 0"));

        Assert.Throws<SqliteException>(() => TempRoot.Exec(connection, "INSERT INTO \"Parent\" (\"Id\", \"Name\") VALUES (-5, 'bad')"));
    }

    [Fact]
    public void RebuildDropsTheColumnAndItsIndexes()
    {
        using var root = new TempRoot();
        using var connection = OpenWithoutForeignKeys(root, "drop.db");
        TempRoot.Exec(connection, Ddl);
        TempRoot.Exec(connection, "ALTER TABLE \"Parent\" ADD COLUMN \"Extra\" TEXT");

        SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("dropColumn", "Parent", "Extra"));

        Assert.Equal(0L, TempRoot.Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('Parent') WHERE name='Extra'"));
        Assert.Equal(3L, TempRoot.Scalar(connection, "SELECT COUNT(*) FROM \"Child\""));
    }

    [Fact]
    public void ConstraintEditingSurvivesCommasInsideLiterals()
    {
        using var root = new TempRoot();
        using var connection = OpenWithoutForeignKeys(root, "literal.db");
        TempRoot.Exec(connection, """
            CREATE TABLE "T" (
                "Id" INTEGER NOT NULL PRIMARY KEY,
                "Sep" TEXT NOT NULL DEFAULT ',',
                "Kind" TEXT NOT NULL DEFAULT 'a,b'
            );
            INSERT INTO "T" ("Id") VALUES (1);
            """);

        SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("addCheck", "T", "CK_T_Kind", "\"Kind\" IN ('a,b','c')"));

        // 重建后默认值与数据都要在。
        Assert.Equal(1L, TempRoot.Scalar(connection, "SELECT COUNT(*) FROM \"T\""));
        Assert.Equal(1L, TempRoot.Scalar(connection, "SELECT COUNT(*) FROM \"T\" WHERE \"Sep\" = ',' AND \"Kind\" = 'a,b'"));
    }

    // 迁移脚本里的 pragma 围栏必须是"允许清单"内的操作，否则 payload 就成了任意 SQL 入口。
    [Fact]
    public void PragmaFenceOnlyAcceptsKnownPragmas()
    {
        using var root = new TempRoot();
        using var connection = OpenWithoutForeignKeys(root, "pragma.db");

        SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("pragma", "", "foreign_keys", "1"));
        Assert.Equal(1L, TempRoot.Scalar(connection, "PRAGMA foreign_keys"));

        Assert.Throws<InvalidOperationException>(
            () => SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("pragma", "", "journal_mode", "delete")));
        Assert.Throws<InvalidOperationException>(
            () => SqliteSchemaEditor.Apply(connection, null, new SchemaEdit("pragma", "", "foreign_keys", "1; DROP TABLE \"T\"")));
    }

    private static SqliteConnection OpenWithoutForeignKeys(TempRoot root, string name)
    {
        var connection = root.Open(name, foreignKeys: false);
        Assert.Equal(0L, TempRoot.Scalar(connection, "PRAGMA foreign_keys"));
        return connection;
    }

    private static long[] RowIds(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM \"Parent\" ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return [.. ids];
    }
}
