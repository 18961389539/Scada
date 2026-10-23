using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddOpcUaConnectionAndNodeAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NodeId",
                table: "Tag",
                type: "TEXT",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OpcEndpointUrl",
                table: "Device",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "opc.tcp://localhost:4840");

            migrationBuilder.AddColumn<string>(
                name: "OpcPasswordProtected",
                table: "Device",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpcSecurityMode",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "OpcSecurityPolicy",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OpcUserName",
                table: "Device",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "OpcEndpointUrl",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "OpcPasswordProtected",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "OpcSecurityMode",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "OpcSecurityPolicy",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "OpcUserName",
                table: "Device");
        }
    }
}
