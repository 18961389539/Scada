using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class FieldControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "ControlPoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequireConfirm = table.Column<bool>(type: "INTEGER", nullable: false),
                    DualApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovalTimeoutSec = table.Column<int>(type: "INTEGER", nullable: false),
                    PermitExpression = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    PermitMessage = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LimitMin = table.Column<double>(type: "REAL", nullable: true),
                    LimitMax = table.Column<double>(type: "REAL", nullable: true),
                    MaxStep = table.Column<double>(type: "REAL", nullable: true),
                    FeedbackExpression = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    FeedbackTimeoutMs = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ControlPoints_Tag_TagId",
                        column: x => x.TagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "ControlSequences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DualApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceControlStates",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChangedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceControlStates", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_DeviceControlStates_Device_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Device",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ControlLockouts_RemovedTs",
                table: "ControlLockouts",
                column: "RemovedTs");

            migrationBuilder.CreateIndex(
                name: "IX_ControlPoints_TagId",
                table: "ControlPoints",
                column: "TagId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ControlRequests_Status_ExpiresTs",
                table: "ControlRequests",
                columns: new[] { "Status", "ExpiresTs" });

            migrationBuilder.CreateIndex(
                name: "IX_ControlSequenceRuns_SequenceId_StartedTs",
                table: "ControlSequenceRuns",
                columns: new[] { "SequenceId", "StartedTs" });

            migrationBuilder.CreateIndex(
                name: "IX_ControlSequences_Name",
                table: "ControlSequences",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlLockouts");

            migrationBuilder.DropTable(
                name: "ControlPoints");

            migrationBuilder.DropTable(
                name: "ControlRequests");

            migrationBuilder.DropTable(
                name: "ControlSequenceRuns");

            migrationBuilder.DropTable(
                name: "ControlSequences");

            migrationBuilder.DropTable(
                name: "DeviceControlStates");
        }
    }
}
