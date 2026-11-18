using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddMomentaryLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MomentaryLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseValue = table.Column<double>(type: "REAL", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartDeadlineTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresTs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MomentaryLeases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MomentaryLeases_ExpiresTs",
                table: "MomentaryLeases",
                column: "ExpiresTs");

            migrationBuilder.CreateIndex(
                name: "IX_MomentaryLeases_TagId",
                table: "MomentaryLeases",
                column: "TagId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MomentaryLeases");
        }
    }
}
