using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class InitialRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlarmEvent",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: true),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Consequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseTimeSec = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    StartTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTs = table.Column<long>(type: "INTEGER", nullable: true),
                    AckUser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AckTs = table.Column<long>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MessageEn = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Shelved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Suppressed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SuppressReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Chattering = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstOut = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstUp = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmRollup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Granularity = table.Column<int>(type: "INTEGER", nullable: false),
                    BucketTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: true),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Annunciated = table.Column<int>(type: "INTEGER", nullable: false),
                    Shelved = table.Column<int>(type: "INTEGER", nullable: false),
                    Suppressed = table.Column<int>(type: "INTEGER", nullable: false),
                    Chattering = table.Column<int>(type: "INTEGER", nullable: false),
                    Fleeting = table.Column<int>(type: "INTEGER", nullable: false),
                    AckCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AckSumMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ClearCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ClearSumMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmRollup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmRollupState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    WatermarkTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunTs = table.Column<long>(type: "INTEGER", nullable: true),
                    RebuildFromTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmRollupState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatewayCommand",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresTs = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProtectedExecutionToken = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CompletedTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayCommand", x => x.Id);
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
                    ReceivedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    DeadLetteredTs = table.Column<long>(type: "INTEGER", nullable: true),
                    ProcessingError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayInbox", x => new { x.GatewayId, x.StreamId, x.Sequence });
                });

            migrationBuilder.CreateTable(
                name: "LocalControlCommand",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalControlCommand", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpIdempotencyRecord",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpIdempotencyRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OeeLineSample",
                columns: table => new
                {
                    LineId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    GoodCount = table.Column<double>(type: "REAL", nullable: true),
                    RejectCount = table.Column<double>(type: "REAL", nullable: true),
                    TotalCount = table.Column<double>(type: "REAL", nullable: true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeLineSample", x => x.LineId);
                });

            migrationBuilder.CreateTable(
                name: "OeeRollup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Granularity = table.Column<int>(type: "INTEGER", nullable: false),
                    BucketTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlannedMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RunMs = table.Column<long>(type: "INTEGER", nullable: false),
                    DownMs = table.Column<long>(type: "INTEGER", nullable: false),
                    PlannedDownMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FaultMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FaultCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SetupMs = table.Column<long>(type: "INTEGER", nullable: false),
                    GoodCount = table.Column<double>(type: "REAL", nullable: false),
                    RejectCount = table.Column<double>(type: "REAL", nullable: false),
                    ScrapCount = table.Column<double>(type: "REAL", nullable: false),
                    IdealCycleSec = table.Column<double>(type: "REAL", nullable: false),
                    TargetCount = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeRollup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OeeRollupState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WatermarkTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunTs = table.Column<long>(type: "INTEGER", nullable: true),
                    RebuildFromTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OeeRollupState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmActivity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmActivity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmActivity_AlarmEvent_AlarmEventId",
                        column: x => x.AlarmEventId,
                        principalTable: "AlarmEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlarmNotificationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    RouteId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmNotificationLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmNotificationLog_AlarmEvent_AlarmEventId",
                        column: x => x.AlarmEventId,
                        principalTable: "AlarmEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReportRunId = table.Column<long>(type: "INTEGER", nullable: true),
                    Recipients = table.Column<string>(type: "TEXT", nullable: true),
                    RouteId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseUntilTs = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    SentTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FinishedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    AcknowledgedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationOutbox_AlarmEvent_AlarmEventId",
                        column: x => x.AlarmEventId,
                        principalTable: "AlarmEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmActivity_AlarmEventId_Ts",
                table: "AlarmActivity",
                columns: new[] { "AlarmEventId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_Category_StartTs",
                table: "AlarmEvent",
                columns: new[] { "Category", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_DeviceId_GatewayId",
                table: "AlarmEvent",
                columns: new[] { "DeviceId", "GatewayId" },
                unique: true,
                filter: "\"GatewayId\" IS NOT NULL AND \"DeviceId\" IS NOT NULL AND \"RuleId\" IS NULL AND \"TagId\" IS NULL AND \"EndTs\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_EndTs",
                table: "AlarmEvent",
                column: "EndTs");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_EndTs_StartTs",
                table: "AlarmEvent",
                columns: new[] { "EndTs", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_RuleId",
                table: "AlarmEvent",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_RuleId_StartTs",
                table: "AlarmEvent",
                columns: new[] { "RuleId", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_StartTs",
                table: "AlarmEvent",
                column: "StartTs");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_TagId_StartTs",
                table: "AlarmEvent",
                columns: new[] { "TagId", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmNotificationLog_AlarmEventId_RouteId_Action",
                table: "AlarmNotificationLog",
                columns: new[] { "AlarmEventId", "RouteId", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRollup_Granularity_BucketTs_Source_Level",
                table: "AlarmRollup",
                columns: new[] { "Granularity", "BucketTs", "Source", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GatewayCommand_GatewayId_SessionId_State_ExpiresTs",
                table: "GatewayCommand",
                columns: new[] { "GatewayId", "SessionId", "State", "ExpiresTs" });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayInbox_GatewayId_DeadLetteredTs",
                table: "GatewayInbox",
                columns: new[] { "GatewayId", "DeadLetteredTs" });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayInbox_ReceivedTs",
                table: "GatewayInbox",
                column: "ReceivedTs");

            migrationBuilder.CreateIndex(
                name: "IX_LocalControlCommand_CreatedTs",
                table: "LocalControlCommand",
                column: "CreatedTs");

            migrationBuilder.CreateIndex(
                name: "IX_McpIdempotencyRecord_CreatedTs",
                table: "McpIdempotencyRecord",
                column: "CreatedTs");

            migrationBuilder.CreateIndex(
                name: "IX_McpIdempotencyRecord_Tool_RequestId",
                table: "McpIdempotencyRecord",
                columns: new[] { "Tool", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_AlarmEventId",
                table: "NotificationOutbox",
                column: "AlarmEventId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_DedupKey",
                table: "NotificationOutbox",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptTs",
                table: "NotificationOutbox",
                columns: new[] { "Status", "NextAttemptTs" });

            migrationBuilder.CreateIndex(
                name: "IX_OeeRollup_Granularity_BucketTs_LineId_ProductId",
                table: "OeeRollup",
                columns: new[] { "Granularity", "BucketTs", "LineId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OeeRollup_LineId_Granularity_BucketTs",
                table: "OeeRollup",
                columns: new[] { "LineId", "Granularity", "BucketTs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmActivity");

            migrationBuilder.DropTable(
                name: "AlarmNotificationLog");

            migrationBuilder.DropTable(
                name: "AlarmRollup");

            migrationBuilder.DropTable(
                name: "AlarmRollupState");

            migrationBuilder.DropTable(
                name: "GatewayCommand");

            migrationBuilder.DropTable(
                name: "GatewayInbox");

            migrationBuilder.DropTable(
                name: "LocalControlCommand");

            migrationBuilder.DropTable(
                name: "McpIdempotencyRecord");

            migrationBuilder.DropTable(
                name: "NotificationOutbox");

            migrationBuilder.DropTable(
                name: "OeeLineSample");

            migrationBuilder.DropTable(
                name: "OeeRollup");

            migrationBuilder.DropTable(
                name: "OeeRollupState");

            migrationBuilder.DropTable(
                name: "AlarmEvent");
        }
    }
}
