using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChannelId",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Channel",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, defaultValue: "127.0.0.1"),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    SerialPort = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: "COM1"),
                    BaudRate = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 9600),
                    DataBits = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 8),
                    Parity = table.Column<int>(type: "INTEGER", nullable: false),
                    StopBits = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    TimeoutMs = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 3000),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channel", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Device_ChannelId",
                table: "Device",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Channel_Name",
                table: "Channel",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Device_Channel_ChannelId",
                table: "Device",
                column: "ChannelId",
                principalTable: "Channel",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Device_Channel_ChannelId",
                table: "Device");

            migrationBuilder.DropTable(
                name: "Channel");

            migrationBuilder.DropIndex(
                name: "IX_Device_ChannelId",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "Device");
        }
    }
}
