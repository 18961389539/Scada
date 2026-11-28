using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddKioskPlaylistsAndLayoutScreenRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScreenRevisionsJson",
                table: "HmiLayoutVersion",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KioskPlaylist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ScreenIdsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitorOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KioskPlaylist", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KioskPlaylist_Name",
                table: "KioskPlaylist",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KioskPlaylist");

            migrationBuilder.DropColumn(
                name: "ScreenRevisionsJson",
                table: "HmiLayoutVersion");
        }
    }
}
