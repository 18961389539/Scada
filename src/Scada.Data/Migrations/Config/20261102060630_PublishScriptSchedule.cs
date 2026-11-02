using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class PublishScriptSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PublishedAlarmMinLevel",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "PublishedAlarmOnAck",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PublishedAlarmOnRaise",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PublishedAlarmOnRecover",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PublishedChangeCooldownMs",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "PublishedChangeDeadband",
                table: "CalcJobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "PublishedIntervalMs",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PublishedTrigger",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AlarmMinLevel",
                table: "CalcJobPublications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AlarmOnAck",
                table: "CalcJobPublications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AlarmOnRaise",
                table: "CalcJobPublications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AlarmOnRecover",
                table: "CalcJobPublications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ChangeCooldownMs",
                table: "CalcJobPublications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "ChangeDeadband",
                table: "CalcJobPublications",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "IntervalMs",
                table: "CalcJobPublications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Trigger",
                table: "CalcJobPublications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // 已有脚本的触发设置就是当时正在运行的设置，升库后继续用它，直到下一次发布。
            migrationBuilder.Sql("""
                UPDATE "CalcJobs" SET
                    "PublishedTrigger" = "Trigger",
                    "PublishedIntervalMs" = "IntervalMs",
                    "PublishedChangeDeadband" = "ChangeDeadband",
                    "PublishedChangeCooldownMs" = "ChangeCooldownMs",
                    "PublishedAlarmMinLevel" = "AlarmMinLevel",
                    "PublishedAlarmOnRaise" = "AlarmOnRaise",
                    "PublishedAlarmOnAck" = "AlarmOnAck",
                    "PublishedAlarmOnRecover" = "AlarmOnRecover"
                WHERE "Kind" IN (3, 4);
                """);
            migrationBuilder.Sql("""
                UPDATE "CalcJobPublications" SET
                    "Trigger" = COALESCE((SELECT "Trigger" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 0),
                    "IntervalMs" = COALESCE((SELECT "IntervalMs" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 1000),
                    "ChangeDeadband" = COALESCE((SELECT "ChangeDeadband" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 0),
                    "ChangeCooldownMs" = COALESCE((SELECT "ChangeCooldownMs" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 1000),
                    "AlarmMinLevel" = COALESCE((SELECT "AlarmMinLevel" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 0),
                    "AlarmOnRaise" = COALESCE((SELECT "AlarmOnRaise" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 0),
                    "AlarmOnAck" = COALESCE((SELECT "AlarmOnAck" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 0),
                    "AlarmOnRecover" = COALESCE((SELECT "AlarmOnRecover" FROM "CalcJobs" j WHERE j."Id" = "CalcJobPublications"."JobId"), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishedAlarmMinLevel",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedAlarmOnAck",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedAlarmOnRaise",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedAlarmOnRecover",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedChangeCooldownMs",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedChangeDeadband",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedIntervalMs",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedTrigger",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "AlarmMinLevel",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "AlarmOnAck",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "AlarmOnRaise",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "AlarmOnRecover",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "ChangeCooldownMs",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "ChangeDeadband",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "IntervalMs",
                table: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "CalcJobPublications");
        }
    }
}
