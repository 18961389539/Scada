using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddStandbyChannelEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StandbyBaudRate",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandbyDataBits",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandbyParity",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StandbySerialPort",
                table: "Device",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandbyStopBits",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandbyTimeoutMs",
                table: "Device",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "StandbyBaudRate", table: "Device");
            migrationBuilder.DropColumn(name: "StandbyDataBits", table: "Device");
            migrationBuilder.DropColumn(name: "StandbyParity", table: "Device");
            migrationBuilder.DropColumn(name: "StandbySerialPort", table: "Device");
            migrationBuilder.DropColumn(name: "StandbyStopBits", table: "Device");
            migrationBuilder.DropColumn(name: "StandbyTimeoutMs", table: "Device");
        }
    }
}
