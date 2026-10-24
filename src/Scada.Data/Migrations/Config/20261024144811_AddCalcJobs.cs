using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddCalcJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tag_Source",
                table: "Tag");

            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "Tag",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobOutput",
                table: "Tag",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CalcJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    IntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    InputTagId = table.Column<int>(type: "INTEGER", nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Factor = table.Column<double>(type: "REAL", nullable: false),
                    CounterMax = table.Column<double>(type: "REAL", nullable: true),
                    MaxDelta = table.Column<double>(type: "REAL", nullable: true),
                    BadQuality = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxGapSec = table.Column<int>(type: "INTEGER", nullable: false),
                    RunThreshold = table.Column<double>(type: "REAL", nullable: false),
                    MaintenanceHours = table.Column<double>(type: "REAL", nullable: true),
                    ResetPeriod = table.Column<int>(type: "INTEGER", nullable: false),
                    ShiftStarts = table.Column<string>(type: "TEXT", nullable: false),
                    TouJson = table.Column<string>(type: "TEXT", nullable: false),
                    Script = table.Column<string>(type: "TEXT", nullable: false),
                    ReadTags = table.Column<string>(type: "TEXT", nullable: false),
                    WriteTags = table.Column<string>(type: "TEXT", nullable: false),
                    RunAsUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RunAsUserName = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalcJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalcJobs_Area_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Area",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    table.ForeignKey(
                        name: "FK_CalcJobSnapshots_CalcJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "CalcJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalcJobStates",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalcJobStates", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_CalcJobStates_CalcJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "CalcJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tag_JobId_JobOutput",
                table: "Tag",
                columns: new[] { "JobId", "JobOutput" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tag_Source",
                table: "Tag",
                sql: "(Kind = 0 AND DeviceId IS NOT NULL) OR (Kind = 1 AND DeviceId IS NULL AND Writable = 0) OR (Kind = 2 AND DeviceId IS NULL AND Writable = 0 AND JobId IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CalcJobs_AreaId",
                table: "CalcJobs",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_CalcJobs_Name",
                table: "CalcJobs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalcJobSnapshots_JobId_Ts",
                table: "CalcJobSnapshots",
                columns: new[] { "JobId", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalcJobSnapshots");

            migrationBuilder.DropTable(
                name: "CalcJobStates");

            migrationBuilder.DropTable(
                name: "CalcJobs");

            migrationBuilder.DropIndex(
                name: "IX_Tag_JobId_JobOutput",
                table: "Tag");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tag_Source",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "JobOutput",
                table: "Tag");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tag_Source",
                table: "Tag",
                sql: "(Kind = 0 AND DeviceId IS NOT NULL) OR (Kind = 1 AND DeviceId IS NULL AND Writable = 0)");
        }
    }
}
