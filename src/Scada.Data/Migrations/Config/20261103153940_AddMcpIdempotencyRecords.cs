using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddMcpIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpIdempotencyRecord",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedTs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpIdempotencyRecord", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpIdempotencyRecord_CreatedTs",
                table: "McpIdempotencyRecord",
                column: "CreatedTs");

            migrationBuilder.CreateIndex(
                name: "IX_McpIdempotencyRecord_Tool_RequestId",
                table: "McpIdempotencyRecord",
                columns: new[] { "Tool", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpIdempotencyRecord");
        }
    }
}
