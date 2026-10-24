using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddGatewayCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GatewayCommand",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GatewayId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresTs = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProtectedExecutionToken = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CompletedTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayCommand", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GatewayCommand_AcquisitionGateway_GatewayId",
                        column: x => x.GatewayId,
                        principalTable: "AcquisitionGateway",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayCommand_GatewayId_SessionId_State_ExpiresTs",
                table: "GatewayCommand",
                columns: new[] { "GatewayId", "SessionId", "State", "ExpiresTs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GatewayCommand");
        }
    }
}
