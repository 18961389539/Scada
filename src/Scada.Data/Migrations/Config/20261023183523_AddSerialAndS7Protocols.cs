using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddSerialAndS7Protocols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BitOffset",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DbNumber",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "BaudRate",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 9600);

            migrationBuilder.AddColumn<int>(
                name: "Cpu",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "DataBits",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<int>(
                name: "Parity",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<short>(
                name: "Rack",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "SerialPort",
                table: "Device",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "COM1");

            migrationBuilder.AddColumn<short>(
                name: "Slot",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<int>(
                name: "StopBits",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BitOffset",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "DbNumber",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "BaudRate",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "Cpu",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "DataBits",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "Parity",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "Rack",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "SerialPort",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "StopBits",
                table: "Device");
        }
    }
}
