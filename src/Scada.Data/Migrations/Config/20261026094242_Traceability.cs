using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class Traceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TraceabilityJob",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggerTagId = table.Column<int>(type: "INTEGER", nullable: false),
                    SnSource = table.Column<int>(type: "INTEGER", nullable: false),
                    SnFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    SnTagIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ScanExpirySeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceabilityJob", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraceabilityJob_Tag_TriggerTagId",
                        column: x => x.TriggerTagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    table.ForeignKey(
                        name: "FK_TraceabilityAttempt_TraceabilityJob_JobId",
                        column: x => x.JobId,
                        principalTable: "TraceabilityJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceabilityRecord", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraceabilityRecord_TraceabilityJob_JobId",
                        column: x => x.JobId,
                        principalTable: "TraceabilityJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityAttempt_JobId",
                table: "TraceabilityAttempt",
                column: "JobId",
                unique: true,
                filter: "\"ClosedTs\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityJob_TriggerTagId",
                table: "TraceabilityJob",
                column: "TriggerTagId",
                unique: true);

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
                name: "TraceabilityAttempt");

            migrationBuilder.DropTable(
                name: "TraceabilityRecord");

            migrationBuilder.DropTable(
                name: "TraceabilityJob");
        }
    }
}
