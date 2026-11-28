using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddMomentaryLeaseTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int?>(
                name: "TargetDeviceId",
                table: "MomentaryLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "TargetAddress",
                table: "MomentaryLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "TargetDbNumber",
                table: "MomentaryLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "TargetBitOffset",
                table: "MomentaryLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetNodeId",
                table: "MomentaryLeases",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "TargetDataType",
                table: "MomentaryLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double?>(
                name: "TargetScale",
                table: "MomentaryLeases",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double?>(
                name: "TargetOffset",
                table: "MomentaryLeases",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double?>(
                name: "TargetMinValue",
                table: "MomentaryLeases",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double?>(
                name: "TargetMaxValue",
                table: "MomentaryLeases",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetDeviceId",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetAddress",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetDbNumber",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetBitOffset",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetNodeId",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetDataType",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetScale",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetOffset",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetMinValue",
                table: "MomentaryLeases");

            migrationBuilder.DropColumn(
                name: "TargetMaxValue",
                table: "MomentaryLeases");
        }
    }
}
