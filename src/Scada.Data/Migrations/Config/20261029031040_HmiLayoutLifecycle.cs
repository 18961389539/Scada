using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class HmiLayoutLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublishedBy",
                table: "HmiLayout",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedJson",
                table: "HmiLayout",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedName",
                table: "HmiLayout",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublishedRevision",
                table: "HmiLayout",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PublishedTs",
                table: "HmiLayout",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HmiLayoutVersion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LayoutId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RestoredFromRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmiLayoutVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HmiLayoutVersion_HmiLayout_LayoutId",
                        column: x => x.LayoutId,
                        principalTable: "HmiLayout",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HmiLayoutVersion_LayoutId_Revision",
                table: "HmiLayoutVersion",
                columns: new[] { "LayoutId", "Revision" },
                unique: true);

            // 升级前保存的布局已经直接用于运行，保留其可运行状态并建立第一条发布历史。
            migrationBuilder.Sql("""
                UPDATE HmiLayout
                SET PublishedName = Name, PublishedJson = DocumentJson, PublishedRevision = Revision,
                    PublishedTs = UpdatedTs, PublishedBy = UpdatedBy;
                INSERT INTO HmiLayoutVersion (LayoutId, Revision, Name, DocumentJson, PublishedTs, PublishedBy, Note, RestoredFromRevision, Kind)
                SELECT Id, Revision, Name, DocumentJson, UpdatedTs, UpdatedBy, '升级前的运行布局', NULL, 'migration'
                FROM HmiLayout;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HmiLayoutVersion");

            migrationBuilder.DropColumn(
                name: "PublishedBy",
                table: "HmiLayout");

            migrationBuilder.DropColumn(
                name: "PublishedJson",
                table: "HmiLayout");

            migrationBuilder.DropColumn(
                name: "PublishedName",
                table: "HmiLayout");

            migrationBuilder.DropColumn(
                name: "PublishedRevision",
                table: "HmiLayout");

            migrationBuilder.DropColumn(
                name: "PublishedTs",
                table: "HmiLayout");
        }
    }
}
