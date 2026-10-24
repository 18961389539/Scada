using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    RouteId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseUntilTs = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    SentTs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationOutbox_AlarmEvent_AlarmEventId",
                        column: x => x.AlarmEventId,
                        principalTable: "AlarmEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_AlarmEventId",
                table: "NotificationOutbox",
                column: "AlarmEventId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_DedupKey",
                table: "NotificationOutbox",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptTs",
                table: "NotificationOutbox",
                columns: new[] { "Status", "NextAttemptTs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationOutbox");
        }
    }
}
