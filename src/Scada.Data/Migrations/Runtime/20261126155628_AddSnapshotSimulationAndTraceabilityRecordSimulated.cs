using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddSnapshotSimulationAndTraceabilityRecordSimulated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Simulated",
                table: "TraceabilityRecord",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludesSimulation",
                table: "CalcJobSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Simulated",
                table: "TraceabilityRecord");

            migrationBuilder.DropColumn(
                name: "IncludesSimulation",
                table: "CalcJobSnapshots");
        }
    }
}
