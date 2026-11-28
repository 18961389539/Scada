using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddProductionBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductionBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipeId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RecipeVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SequenceId = table.Column<int>(type: "INTEGER", nullable: true),
                    SequenceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RecipeDownloadId = table.Column<int>(type: "INTEGER", nullable: true),
                    ControlSequenceRunId = table.Column<int>(type: "INTEGER", nullable: true),
                    SequenceRequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionBatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionBatches_BatchNumber",
                table: "ProductionBatches",
                column: "BatchNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionBatches_StartedTs",
                table: "ProductionBatches",
                column: "StartedTs");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionBatches_Status",
                table: "ProductionBatches",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductionBatches");
        }
    }
}
