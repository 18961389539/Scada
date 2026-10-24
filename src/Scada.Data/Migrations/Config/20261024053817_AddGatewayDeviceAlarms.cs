using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddGatewayDeviceAlarms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "AlarmEvent",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GatewayId",
                table: "AlarmEvent",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GatewayDeviceHealth",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedRevision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MonitoringSinceTs = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ObservedRevision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ObservedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPollTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Online = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OfflineSinceTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayDeviceHealth", x => x.DeviceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_DeviceId_GatewayId",
                table: "AlarmEvent",
                columns: new[] { "DeviceId", "GatewayId" },
                unique: true,
                filter: "\"GatewayId\" IS NOT NULL AND \"DeviceId\" IS NOT NULL AND \"RuleId\" IS NULL AND \"TagId\" IS NULL AND \"EndTs\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GatewayDeviceHealth_GatewayId",
                table: "GatewayDeviceHealth",
                column: "GatewayId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GatewayDeviceHealth");

            migrationBuilder.DropIndex(
                name: "IX_AlarmEvent_DeviceId_GatewayId",
                table: "AlarmEvent");

            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "AlarmEvent");

            migrationBuilder.DropColumn(
                name: "GatewayId",
                table: "AlarmEvent");
        }
    }
}
