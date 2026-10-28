using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddTraceabilityOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                table: "TraceabilityRecord",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityRecord_AreaId_Outcome_Ts",
                table: "TraceabilityRecord",
                columns: new[] { "AreaId", "Outcome", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TraceabilityRecord_AreaId_Outcome_Ts",
                table: "TraceabilityRecord");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "TraceabilityRecord");
        }
    }
}
