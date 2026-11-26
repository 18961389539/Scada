using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.History
{
    /// <inheritdoc />
    public partial class AddAggregateSimulatedCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SimulatedCount",
                table: "AggHour",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SimulatedCount",
                table: "Agg5Min",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SimulatedCount",
                table: "AggHour");

            migrationBuilder.DropColumn(
                name: "SimulatedCount",
                table: "Agg5Min");
        }
    }
}
