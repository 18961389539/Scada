using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddScreenDraftDirty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DraftDirty",
                table: "ProcessScreen",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // 旧库按原有草稿判定补齐标记：未发布、修订不一致或回退后草稿已与发布内容分离。
            migrationBuilder.Sql("""
                UPDATE "ProcessScreen" SET "DraftDirty" = 1
                WHERE "PublishedRevision" IS NULL
                   OR "Revision" != "PublishedRevision"
                   OR ("PublishedJson" IS NOT NULL AND ("Name" != "PublishedName" OR "DraftJson" != "PublishedJson")
                       AND EXISTS (SELECT 1 FROM "ProcessScreenVersion" v
                                   WHERE v."ScreenId" = "ProcessScreen"."Id"
                                     AND v."Revision" = "ProcessScreen"."PublishedRevision" AND v."Kind" = 'rollback'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftDirty",
                table: "ProcessScreen");
        }
    }
}
