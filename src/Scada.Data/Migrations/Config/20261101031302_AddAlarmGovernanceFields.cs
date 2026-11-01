using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddAlarmGovernanceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPriority",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Consequence",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ResponseTimeSec",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 600);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPriority",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "Consequence",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "ResponseTimeSec",
                table: "AlarmRule");
        }
    }
}
