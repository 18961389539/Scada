using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddReportTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "AlarmEventId",
                table: "NotificationOutbox",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "Recipients",
                table: "NotificationOutbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReportRunId",
                table: "NotificationOutbox",
                type: "INTEGER",
                nullable: true);

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
                    PdfSha256 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Period = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShiftStarts = table.Column<string>(type: "TEXT", nullable: false),
                    ColumnsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduleEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleDelayMin = table.Column<int>(type: "INTEGER", nullable: false),
                    EmailChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    EmailRecipients = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportTemplates_Area_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Area",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                name: "IX_ReportTemplates_AreaId",
                table: "ReportTemplates",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportTemplates_Name",
                table: "ReportTemplates",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportRuns");

            migrationBuilder.DropTable(
                name: "ReportTemplates");

            migrationBuilder.DropColumn(
                name: "Recipients",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "ReportRunId",
                table: "NotificationOutbox");

            migrationBuilder.AlterColumn<int>(
                name: "AlarmEventId",
                table: "NotificationOutbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
