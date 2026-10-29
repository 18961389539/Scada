using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.History
{
    /// <inheritdoc />
    public partial class DurableAggregation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingAggregation",
                columns: table => new
                {
                    WindowMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAggregation", x => new { x.WindowMs, x.Ts });
                });

            // Triggers cover local writes, spool replay, gateway backfill and HA replication.
            migrationBuilder.Sql("""
                CREATE TRIGGER HistoryValue_QueueAggregation AFTER INSERT ON HistoryValue
                BEGIN
                    INSERT INTO PendingAggregation (WindowMs, Ts, Revision)
                    VALUES (300000, NEW.Ts - ((NEW.Ts % 300000 + 300000) % 300000), 1)
                    ON CONFLICT(WindowMs, Ts) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER Agg5Min_QueueAggregation AFTER INSERT ON Agg5Min
                BEGIN
                    INSERT INTO PendingAggregation (WindowMs, Ts, Revision)
                    VALUES (3600000, NEW.Ts - ((NEW.Ts % 3600000 + 3600000) % 3600000), 1)
                    ON CONFLICT(WindowMs, Ts) DO UPDATE SET Revision = Revision + 1;
                END;
                INSERT INTO PendingAggregation (WindowMs, Ts, Revision)
                SELECT 300000, Ts - ((Ts % 300000 + 300000) % 300000), 1
                FROM HistoryValue GROUP BY Ts - ((Ts % 300000 + 300000) % 300000);
                """);

            // Older retention could have deleted only part of the earliest hour.
            // Raw backfill repairs that hour when its complete source month is still available.
            migrationBuilder.Sql("""
                INSERT INTO PendingAggregation (WindowMs, Ts, Revision)
                SELECT 3600000, Ts - ((Ts % 3600000 + 3600000) % 3600000), 1
                FROM Agg5Min
                WHERE Ts - ((Ts % 3600000 + 3600000) % 3600000) >=
                    (SELECT MIN(Ts) FROM Agg5Min)
                GROUP BY Ts - ((Ts % 3600000 + 3600000) % 3600000);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER HistoryValue_QueueAggregation; DROP TRIGGER Agg5Min_QueueAggregation;");
            migrationBuilder.DropTable(
                name: "PendingAggregation");
        }
    }
}
