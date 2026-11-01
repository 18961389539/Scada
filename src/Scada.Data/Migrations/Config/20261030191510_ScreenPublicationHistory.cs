using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ScreenPublicationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessScreenVersion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScreenId = table.Column<int>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_ProcessScreenVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessScreenVersion_ProcessScreen_ScreenId",
                        column: x => x.ScreenId,
                        principalTable: "ProcessScreen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessScreenVersion_ScreenId_Revision",
                table: "ProcessScreenVersion",
                columns: new[] { "ScreenId", "Revision" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO ProcessScreenVersion
                    (ScreenId, Revision, Name, DocumentJson, PublishedTs, PublishedBy, Note, Kind)
                SELECT Id, PublishedRevision, COALESCE(PublishedName, Name), PublishedJson, UpdatedTs, UpdatedBy,
                    '补录升级前的当前发布版本（时间与人员沿用画面最后修改记录）', 'migration'
                FROM ProcessScreen
                WHERE PublishedJson IS NOT NULL AND PublishedRevision IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessScreenVersion");
        }
    }
}
