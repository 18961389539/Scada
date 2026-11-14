using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddGatewayCommandText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "GatewayCommand",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Text",
                table: "GatewayCommand");
        }
    }
}
