using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddCalcJobPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AlarmSnapshot",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HistoryWindowSec",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "PublishedAlarmSnapshot",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PublishedHistoryWindowSec",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PublishedReadTags",
                table: "CalcJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "PublishedRevision",
                table: "CalcJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PublishedScript",
                table: "CalcJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublishedWriteTags",
                table: "CalcJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CalcJobPublications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Script = table.Column<string>(type: "TEXT", nullable: false),
                    ReadTags = table.Column<string>(type: "TEXT", nullable: false),
                    WriteTags = table.Column<string>(type: "TEXT", nullable: false),
                    HistoryWindowSec = table.Column<int>(type: "INTEGER", nullable: false),
                    AlarmSnapshot = table.Column<bool>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalcJobPublications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalcJobPublications_CalcJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "CalcJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalcJobPublications_JobId",
                table: "CalcJobPublications",
                column: "JobId");

            // 升级前的脚本没有发布副本。回填后周期任务继续运行升级前正在执行的那一版。
            migrationBuilder.Sql("""
                UPDATE CalcJobs
                SET PublishedScript = Script,
                    PublishedReadTags = ReadTags,
                    PublishedWriteTags = WriteTags,
                    PublishedRevision = Revision
                WHERE Kind IN (3, 4);
                """);
            migrationBuilder.Sql("""
                INSERT INTO CalcJobPublications (JobId, Revision, Script, ReadTags, WriteTags, HistoryWindowSec, AlarmSnapshot, Ts, UserName)
                SELECT Id, Revision, Script, ReadTags, WriteTags, 0, 0, UpdatedTs, ''
                FROM CalcJobs
                WHERE Kind IN (3, 4);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalcJobPublications");

            migrationBuilder.DropColumn(
                name: "AlarmSnapshot",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "HistoryWindowSec",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedAlarmSnapshot",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedHistoryWindowSec",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedReadTags",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedRevision",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedScript",
                table: "CalcJobs");

            migrationBuilder.DropColumn(
                name: "PublishedWriteTags",
                table: "CalcJobs");
        }
    }
}
