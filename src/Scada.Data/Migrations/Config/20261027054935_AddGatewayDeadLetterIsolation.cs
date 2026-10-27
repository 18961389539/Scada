using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddGatewayDeadLetterIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "GatewayInbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "DeadLetteredTs",
                table: "GatewayInbox",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GatewayInbox_GatewayId_DeadLetteredTs",
                table: "GatewayInbox",
                columns: new[] { "GatewayId", "DeadLetteredTs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GatewayInbox_GatewayId_DeadLetteredTs",
                table: "GatewayInbox");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "GatewayInbox");

            migrationBuilder.DropColumn(
                name: "DeadLetteredTs",
                table: "GatewayInbox");
        }
    }
}
