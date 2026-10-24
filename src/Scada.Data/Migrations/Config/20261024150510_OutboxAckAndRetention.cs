using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class OutboxAckAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedBy",
                table: "NotificationOutbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AcknowledgedTs",
                table: "NotificationOutbox",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FinishedTs",
                table: "NotificationOutbox",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgedBy",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "AcknowledgedTs",
                table: "NotificationOutbox");

            migrationBuilder.DropColumn(
                name: "FinishedTs",
                table: "NotificationOutbox");
        }
    }
}
