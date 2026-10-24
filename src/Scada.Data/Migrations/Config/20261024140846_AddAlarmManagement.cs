using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddAlarmManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OffDelaySec",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "OutOfService",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OutOfServiceReason",
                table: "AlarmRule",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuppressMode",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SuppressTagId",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Chattering",
                table: "AlarmEvent",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Shelved",
                table: "AlarmEvent",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SuppressReason",
                table: "AlarmEvent",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Suppressed",
                table: "AlarmEvent",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AlarmActivity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmActivity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmActivity_AlarmEvent_AlarmEventId",
                        column: x => x.AlarmEventId,
                        principalTable: "AlarmEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlarmNotificationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlarmEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    RouteId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmNotificationLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmNotificationLog_AlarmEvent_AlarmEventId",
                        column: x => x.AlarmEventId,
                        principalTable: "AlarmEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlarmShelve",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UntilTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ClearedTs = table.Column<long>(type: "INTEGER", nullable: true),
                    ClearedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmShelve", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmShelve_AlarmRule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AlarmRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DutyShift",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    EndMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DutyShift", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationChannel",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    SecretProtected = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRoute",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: true),
                    MinLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    ShiftId = table.Column<int>(type: "INTEGER", nullable: true),
                    Recipients = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    OnRaise = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnRecover = table.Column<bool>(type: "INTEGER", nullable: false),
                    EscalateAfterMinutes = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRoute", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRoute_DutyShift_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "DutyShift",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationRoute_NotificationChannel_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "NotificationChannel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvent_RuleId",
                table: "AlarmEvent",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmActivity_AlarmEventId_Ts",
                table: "AlarmActivity",
                columns: new[] { "AlarmEventId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmNotificationLog_AlarmEventId_RouteId_Action",
                table: "AlarmNotificationLog",
                columns: new[] { "AlarmEventId", "RouteId", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmShelve_RuleId_ClearedTs",
                table: "AlarmShelve",
                columns: new[] { "RuleId", "ClearedTs" });

            migrationBuilder.CreateIndex(
                name: "IX_DutyShift_Name",
                table: "DutyShift",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannel_Name",
                table: "NotificationChannel",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRoute_AreaId",
                table: "NotificationRoute",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRoute_ChannelId",
                table: "NotificationRoute",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRoute_ShiftId",
                table: "NotificationRoute",
                column: "ShiftId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmActivity");

            migrationBuilder.DropTable(
                name: "AlarmNotificationLog");

            migrationBuilder.DropTable(
                name: "AlarmShelve");

            migrationBuilder.DropTable(
                name: "NotificationRoute");

            migrationBuilder.DropTable(
                name: "DutyShift");

            migrationBuilder.DropTable(
                name: "NotificationChannel");

            migrationBuilder.DropIndex(
                name: "IX_AlarmEvent_RuleId",
                table: "AlarmEvent");

            migrationBuilder.DropColumn(
                name: "OffDelaySec",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "OutOfService",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "OutOfServiceReason",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "SuppressMode",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "SuppressTagId",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "Chattering",
                table: "AlarmEvent");

            migrationBuilder.DropColumn(
                name: "Shelved",
                table: "AlarmEvent");

            migrationBuilder.DropColumn(
                name: "SuppressReason",
                table: "AlarmEvent");

            migrationBuilder.DropColumn(
                name: "Suppressed",
                table: "AlarmEvent");
        }
    }
}
