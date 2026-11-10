using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddOperationalData2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnnunciatorSilence",
                columns: table => new
                {
                    PanelId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UntilTs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ByUser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnunciatorSilence", x => x.PanelId);
                });

            migrationBuilder.CreateTable(
                name: "CalcJobSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodKey = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodStartTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalcJobSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalcJobStates",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalcJobStates", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "ControlLockouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlacedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlacedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PlacedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    RemovedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RemoveReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlLockouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ControlRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    Value = table.Column<double>(type: "REAL", nullable: true),
                    SequenceId = table.Column<int>(type: "INTEGER", nullable: true),
                    RecipeDownloadId = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RequestedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RequestedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DecidedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ResultMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ControlSequenceRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SequenceId = table.Column<int>(type: "INTEGER", nullable: false),
                    SequenceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlSequenceRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceControlStates",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChangedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceControlStates", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "DowntimeEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTs = table.Column<long>(type: "INTEGER", nullable: true),
                    ReasonId = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DowntimeEvent", x => x.Id);
                });

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
                });

            migrationBuilder.CreateTable(
                name: "OperationLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionCountCorrection",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: true),
                    PeriodFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodTo = table.Column<long>(type: "INTEGER", nullable: false),
                    GoodDelta = table.Column<double>(type: "REAL", nullable: false),
                    RejectDelta = table.Column<double>(type: "REAL", nullable: false),
                    ScrapDelta = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionCountCorrection", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspection",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: ""),
                    BatchNo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    Product = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    DefectCodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Inspector = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: ""),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspection", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipeDownloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecipeId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ApprovalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    EndedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    Written = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeDownloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    TemplateName = table.Column<string>(type: "TEXT", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodKey = table.Column<string>(type: "TEXT", nullable: false),
                    FromTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ToTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedBy = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    TemplateJson = table.Column<string>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    XlsxPath = table.Column<string>(type: "TEXT", nullable: false),
                    XlsxSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    PdfPath = table.Column<string>(type: "TEXT", nullable: false),
                    PdfSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    PurgedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    XlsxSignature = table.Column<string>(type: "TEXT", nullable: true),
                    PdfSignature = table.Column<string>(type: "TEXT", nullable: true),
                    SignerThumbprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SignerSubject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    SignedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    SignatureError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TraceabilityAttempt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    Reply = table.Column<int>(type: "INTEGER", nullable: false),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceabilityAttempt", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TraceabilityRecord",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CycleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Sn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceabilityRecord", x => x.Id);
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
                name: "ReportApprovals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<long>(type: "INTEGER", nullable: false),
                    Step = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", nullable: false),
                    SignerThumbprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportApprovals_ReportRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ReportRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalcJobSnapshots_JobId_Ts",
                table: "CalcJobSnapshots",
                columns: new[] { "JobId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_CalcJobStates_JobId",
                table: "CalcJobStates",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ControlLockouts_RemovedTs",
                table: "ControlLockouts",
                column: "RemovedTs");

            migrationBuilder.CreateIndex(
                name: "IX_ControlRequests_Status_ExpiresTs",
                table: "ControlRequests",
                columns: new[] { "Status", "ExpiresTs" });

            migrationBuilder.CreateIndex(
                name: "IX_ControlSequenceRuns_SequenceId_StartedTs",
                table: "ControlSequenceRuns",
                columns: new[] { "SequenceId", "StartedTs" });

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeEvent_AlarmEventId",
                table: "DowntimeEvent",
                column: "AlarmEventId");

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeEvent_LineId_EndTs",
                table: "DowntimeEvent",
                columns: new[] { "LineId", "EndTs" });

            migrationBuilder.CreateIndex(
                name: "IX_DowntimeEvent_LineId_StartTs",
                table: "DowntimeEvent",
                columns: new[] { "LineId", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayDeviceHealth_GatewayId",
                table: "GatewayDeviceHealth",
                column: "GatewayId");

            migrationBuilder.CreateIndex(
                name: "IX_GatewayStream_GatewayId",
                table: "GatewayStream",
                column: "GatewayId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLog_Ts",
                table: "OperationLog",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionCountCorrection_LineId_PeriodFrom",
                table: "ProductionCountCorrection",
                columns: new[] { "LineId", "PeriodFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_AreaId_Ts",
                table: "QualityInspection",
                columns: new[] { "AreaId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_BatchNo_Ts",
                table: "QualityInspection",
                columns: new[] { "BatchNo", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_DefectCodeId",
                table: "QualityInspection",
                column: "DefectCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_DeviceId",
                table: "QualityInspection",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_Result_Ts",
                table: "QualityInspection",
                columns: new[] { "Result", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_Sn_Ts",
                table: "QualityInspection",
                columns: new[] { "Sn", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspection_TagId",
                table: "QualityInspection",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeDownloads_RecipeId_CreatedTs",
                table: "RecipeDownloads",
                columns: new[] { "RecipeId", "CreatedTs" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeDownloads_Status",
                table: "RecipeDownloads",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ReportApprovals_RunId_Ts",
                table: "ReportApprovals",
                columns: new[] { "RunId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_GeneratedTs",
                table: "ReportRuns",
                column: "GeneratedTs");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRuns_TemplateId_PeriodKey_Version",
                table: "ReportRuns",
                columns: new[] { "TemplateId", "PeriodKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityAttempt_JobId",
                table: "TraceabilityAttempt",
                column: "JobId",
                unique: true,
                filter: "\"ClosedTs\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityRecord_AreaId_Outcome_Ts",
                table: "TraceabilityRecord",
                columns: new[] { "AreaId", "Outcome", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityRecord_AreaId_Ts",
                table: "TraceabilityRecord",
                columns: new[] { "AreaId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityRecord_CycleId",
                table: "TraceabilityRecord",
                column: "CycleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityRecord_JobId",
                table: "TraceabilityRecord",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityRecord_Sn_Ts",
                table: "TraceabilityRecord",
                columns: new[] { "Sn", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnnunciatorSilence");

            migrationBuilder.DropTable(
                name: "CalcJobSnapshots");

            migrationBuilder.DropTable(
                name: "CalcJobStates");

            migrationBuilder.DropTable(
                name: "ControlLockouts");

            migrationBuilder.DropTable(
                name: "ControlRequests");

            migrationBuilder.DropTable(
                name: "ControlSequenceRuns");

            migrationBuilder.DropTable(
                name: "DeviceControlStates");

            migrationBuilder.DropTable(
                name: "DowntimeEvent");

            migrationBuilder.DropTable(
                name: "GatewayArchiveCursor");

            migrationBuilder.DropTable(
                name: "GatewayDeviceHealth");

            migrationBuilder.DropTable(
                name: "OperationLog");

            migrationBuilder.DropTable(
                name: "ProductionCountCorrection");

            migrationBuilder.DropTable(
                name: "QualityInspection");

            migrationBuilder.DropTable(
                name: "RecipeDownloads");

            migrationBuilder.DropTable(
                name: "ReportApprovals");

            migrationBuilder.DropTable(
                name: "TraceabilityAttempt");

            migrationBuilder.DropTable(
                name: "TraceabilityRecord");

            migrationBuilder.DropTable(
                name: "GatewayStream");

            migrationBuilder.DropTable(
                name: "ReportRuns");
        }
    }
}
