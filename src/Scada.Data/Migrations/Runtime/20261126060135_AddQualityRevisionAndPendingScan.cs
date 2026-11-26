using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddQualityRevisionAndPendingScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AmendmentReason",
                table: "QualityInspection",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DeleteReason",
                table: "QualityInspection",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "QualityInspection",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeletedTs",
                table: "QualityInspection",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "ControlRequests",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QualityInspectionRevision",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InspectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspectionRevision", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TraceabilityPendingScan",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ExpiresTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceabilityPendingScan", x => x.JobId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionRevision_InspectionId_Ts",
                table: "QualityInspectionRevision",
                columns: new[] { "InspectionId", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityInspectionRevision");

            migrationBuilder.DropTable(
                name: "TraceabilityPendingScan");

            migrationBuilder.DropColumn(
                name: "AmendmentReason",
                table: "QualityInspection");

            migrationBuilder.DropColumn(
                name: "DeleteReason",
                table: "QualityInspection");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "QualityInspection");

            migrationBuilder.DropColumn(
                name: "DeletedTs",
                table: "QualityInspection");

            migrationBuilder.DropColumn(
                name: "Text",
                table: "ControlRequests");
        }
    }
}
