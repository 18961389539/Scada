using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddKioskMonitorSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KioskMonitorSessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlaylistId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpiresTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EnteredTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KioskMonitorSessions", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KioskMonitorSessions_ExpiresTs",
                table: "KioskMonitorSessions",
                column: "ExpiresTs");

            migrationBuilder.CreateIndex(
                name: "IX_KioskMonitorSessions_UserId",
                table: "KioskMonitorSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KioskMonitorSessions");
        }
    }
}
