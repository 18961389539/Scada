using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddCalcJobTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AlarmMinLevel",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AlarmOnAck",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AlarmOnRaise",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AlarmOnRecover",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ChangeCooldownMs",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "ChangeDeadband",
                table: "CalcJobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "Trigger",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlarmMinLevel",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "AlarmOnAck",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "AlarmOnRaise",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "AlarmOnRecover",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "ChangeCooldownMs",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "ChangeDeadband",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "CalcJobs");
        }
    }
}
