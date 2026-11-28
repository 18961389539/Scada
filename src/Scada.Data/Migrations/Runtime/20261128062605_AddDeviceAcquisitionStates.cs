using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddDeviceAcquisitionStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceAcquisitionStates",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivePath = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ActiveUnhealthySince = table.Column<long>(type: "INTEGER", nullable: true),
                    PrimaryHealthySince = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAcquisitionStates", x => x.DeviceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAcquisitionStates_ActivePath",
                table: "DeviceAcquisitionStates",
                column: "ActivePath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceAcquisitionStates");
        }
    }
}
