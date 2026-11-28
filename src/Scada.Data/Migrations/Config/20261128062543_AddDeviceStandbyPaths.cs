using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddDeviceStandbyPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailoverHoldMs",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10000);

            migrationBuilder.AddColumn<int>(
                name: "StandbyChannelId",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandbyGatewayId",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StandbyHost",
                table: "Device",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandbyPort",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SwitchbackHoldMs",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30000);

            migrationBuilder.CreateIndex(
                name: "IX_Device_StandbyChannelId",
                table: "Device",
                column: "StandbyChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Device_StandbyGatewayId",
                table: "Device",
                column: "StandbyGatewayId");

            migrationBuilder.AddForeignKey(
                name: "FK_Device_AcquisitionGateway_StandbyGatewayId",
                table: "Device",
                column: "StandbyGatewayId",
                principalTable: "AcquisitionGateway",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Device_AcquisitionGateway_StandbyGatewayId",
                table: "Device");

            migrationBuilder.DropIndex(
                name: "IX_Device_StandbyChannelId",
                table: "Device");

            migrationBuilder.DropIndex(
                name: "IX_Device_StandbyGatewayId",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "FailoverHoldMs",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "StandbyChannelId",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "StandbyGatewayId",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "StandbyHost",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "StandbyPort",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "SwitchbackHoldMs",
                table: "Device");
        }
    }
}
