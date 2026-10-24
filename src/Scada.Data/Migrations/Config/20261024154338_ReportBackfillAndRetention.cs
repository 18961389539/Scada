using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ReportBackfillAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ScheduleSinceTs",
                table: "ReportTemplates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PurgedTs",
                table: "ReportRuns",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleSinceTs",
                table: "ReportTemplates");

            migrationBuilder.DropColumn(
                name: "PurgedTs",
                table: "ReportRuns");
        }
    }
}
