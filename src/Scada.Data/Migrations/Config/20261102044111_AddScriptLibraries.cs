using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddScriptLibraries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Libraries",
                table: "CalcJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublishedLibraries",
                table: "CalcJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublishedLibrarySource",
                table: "CalcJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Libraries",
                table: "CalcJobPublications",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LibrarySource",
                table: "CalcJobPublications",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ScriptLibraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedSource = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptLibraries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScriptLibraries_Name",
                table: "ScriptLibraries",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScriptLibraries");

            migrationBuilder.DropColumn(
                name: "Libraries",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedLibraries",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedLibrarySource",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "Libraries",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "LibrarySource",
                table: "CalcJobPublications");
        }
    }
}
