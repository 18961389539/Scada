using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class NotificationChannelAndAreaRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "NotificationChannel",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Area",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.UpdateData(
                table: "Area",
                keyColumn: "Id",
                keyValue: 1,
                column: "Revision",
                value: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "NotificationChannel");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Area");
        }
    }
}
