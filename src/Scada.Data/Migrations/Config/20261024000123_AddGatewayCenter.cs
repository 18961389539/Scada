using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddGatewayCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DisabledSinceTs",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "GatewayId",
                table: "Device",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AcquisitionGateway",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActiveStreamId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LeaseUntilTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenTs = table.Column<long>(type: "INTEGER", nullable: false),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AppliedRevision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PendingBatches = table.Column<long>(type: "INTEGER", nullable: false),
                    PendingBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    OldestPendingTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcquisitionGateway", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatewayConfiguration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayConfiguration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GatewayConfiguration_AcquisitionGateway_GatewayId",
                        column: x => x.GatewayId,
                        principalTable: "AcquisitionGateway",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GatewayStream",
                columns: table => new
                {
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivedThrough = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProcessedThrough = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayStream", x => new { x.GatewayId, x.StreamId });
                    table.ForeignKey(
                        name: "FK_GatewayStream_AcquisitionGateway_GatewayId",
                        column: x => x.GatewayId,
                        principalTable: "AcquisitionGateway",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GatewayArchiveCursor",
                columns: table => new
                {
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayArchiveCursor", x => new { x.GatewayId, x.StreamId, x.TagId });
                    table.ForeignKey(
                        name: "FK_GatewayArchiveCursor_GatewayStream_GatewayId_StreamId",
                        columns: x => new { x.GatewayId, x.StreamId },
                        principalTable: "GatewayStream",
                        principalColumns: new[] { "GatewayId", "StreamId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GatewayInbox",
                columns: table => new
                {
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayloadBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceivedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayInbox", x => new { x.GatewayId, x.StreamId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_GatewayInbox_GatewayStream_GatewayId_StreamId",
                        columns: x => new { x.GatewayId, x.StreamId },
                        principalTable: "GatewayStream",
                        principalColumns: new[] { "GatewayId", "StreamId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Device_GatewayId",
                table: "Device",
                column: "GatewayId");

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionGateway_Name",
                table: "AcquisitionGateway",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GatewayConfiguration_GatewayId_Revision",
                table: "GatewayConfiguration",
                columns: new[] { "GatewayId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GatewayInbox_ReceivedTs",
                table: "GatewayInbox",
                column: "ReceivedTs");

            migrationBuilder.AddForeignKey(
                name: "FK_Device_AcquisitionGateway_GatewayId",
                table: "Device",
                column: "GatewayId",
                principalTable: "AcquisitionGateway",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Device_AcquisitionGateway_GatewayId",
                table: "Device");

            migrationBuilder.DropTable(
                name: "GatewayArchiveCursor");

            migrationBuilder.DropTable(
                name: "GatewayConfiguration");

            migrationBuilder.DropTable(
                name: "GatewayInbox");

            migrationBuilder.DropTable(
                name: "GatewayStream");

            migrationBuilder.DropTable(
                name: "AcquisitionGateway");

            migrationBuilder.DropIndex(
                name: "IX_Device_GatewayId",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "DisabledSinceTs",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "GatewayId",
                table: "Device");
        }
    }
}
