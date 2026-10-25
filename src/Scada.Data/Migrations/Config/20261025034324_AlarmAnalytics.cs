using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AlarmAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlarmAnalyticsConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ThresholdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmAnalyticsConfig", x => x.Id);
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

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_EndTs_StartTs",
                table: "AlarmEvent",
                columns: new[] { "EndTs", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_RuleId_StartTs",
                table: "AlarmEvent",
                columns: new[] { "RuleId", "StartTs" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRollup_Granularity_BucketTs_Source_Level",
                table: "AlarmRollup",
                columns: new[] { "Granularity", "BucketTs", "Source", "Level" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmAnalyticsConfig");

            migrationBuilder.DropTable(
                name: "AlarmRollup");

            migrationBuilder.DropTable(
                name: "AlarmRollupState");

            migrationBuilder.DropIndex(
                name: "IX_AlarmEvent_EndTs_StartTs",
                table: "AlarmEvent");

            migrationBuilder.DropIndex(
                name: "IX_AlarmEvent_RuleId_StartTs",
                table: "AlarmEvent");
        }
    }
}
