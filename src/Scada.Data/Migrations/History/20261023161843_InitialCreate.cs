using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.History
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agg5Min",
                columns: table => new
                {
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Avg = table.Column<double>(type: "REAL", nullable: false),
                    Min = table.Column<double>(type: "REAL", nullable: false),
                    Max = table.Column<double>(type: "REAL", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agg5Min", x => new { x.TagId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "AggHour",
                columns: table => new
                {
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Avg = table.Column<double>(type: "REAL", nullable: false),
                    Min = table.Column<double>(type: "REAL", nullable: false),
                    Max = table.Column<double>(type: "REAL", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AggHour", x => new { x.TagId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "HistoryValue",
                columns: table => new
                {
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryValue", x => new { x.TagId, x.Ts });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agg5Min_Ts",
                table: "Agg5Min",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_AggHour_Ts",
                table: "AggHour",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryValue_Ts",
                table: "HistoryValue",
                column: "Ts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Agg5Min");

            migrationBuilder.DropTable(
                name: "AggHour");

            migrationBuilder.DropTable(
                name: "HistoryValue");
        }
    }
}
