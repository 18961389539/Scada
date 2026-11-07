using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddSpcSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpcSample",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChartId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Mean = table.Column<double>(type: "REAL", nullable: false),
                    Range = table.Column<double>(type: "REAL", nullable: false),
                    SubgroupSize = table.Column<int>(type: "INTEGER", nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false, defaultValue: "[]"),
                    CenterLine = table.Column<double>(type: "REAL", nullable: true),
                    UpperControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    LowerControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    RangeCenter = table.Column<double>(type: "REAL", nullable: true),
                    RangeUpperControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    RangeLowerControlLimit = table.Column<double>(type: "REAL", nullable: true),
                    RuleCodes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, defaultValue: ""),
                    Cp = table.Column<double>(type: "REAL", nullable: true),
                    Cpk = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpcSample", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpcViolation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChartId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    SampleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Rule = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: true),
                    RecoveredTs = table.Column<long>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpcViolation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpcSample_ChartId_Revision_Ts",
                table: "SpcSample",
                columns: new[] { "ChartId", "Revision", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_SpcSample_ChartId_Ts",
                table: "SpcSample",
                columns: new[] { "ChartId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_SpcViolation_AlarmEventId",
                table: "SpcViolation",
                column: "AlarmEventId");

            migrationBuilder.CreateIndex(
                name: "IX_SpcViolation_ChartId_RecoveredTs",
                table: "SpcViolation",
                columns: new[] { "ChartId", "RecoveredTs" });

            migrationBuilder.CreateIndex(
                name: "IX_SpcViolation_ChartId_Ts",
                table: "SpcViolation",
                columns: new[] { "ChartId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_SpcViolation_SampleId",
                table: "SpcViolation",
                column: "SampleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpcSample");

            migrationBuilder.DropTable(
                name: "SpcViolation");
        }
    }
}
