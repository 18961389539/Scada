using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ConfigRevisionTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "NotificationRoute",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Channel",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "NotificationRoute");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Channel");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "AlarmRule");

            migrationBuilder.AlterColumn<long>(
                name: "Revision",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1L);
        }
    }
}
