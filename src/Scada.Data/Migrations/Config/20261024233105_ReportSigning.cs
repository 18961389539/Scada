using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ReportSigning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PdfSignature",
                table: "ReportRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatureError",
                table: "ReportRuns",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SignedTs",
                table: "ReportRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignerSubject",
                table: "ReportRuns",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignerThumbprint",
                table: "ReportRuns",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XlsxSignature",
                table: "ReportRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReportApprovals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<long>(type: "INTEGER", nullable: false),
                    Step = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", nullable: false),
                    SignerThumbprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportApprovals_ReportRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ReportRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportApprovals_RunId_Ts",
                table: "ReportApprovals",
                columns: new[] { "RunId", "Ts" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportApprovals");

            migrationBuilder.DropColumn(
                name: "PdfSignature",
                table: "ReportRuns");

            migrationBuilder.DropColumn(
                name: "SignatureError",
                table: "ReportRuns");

            migrationBuilder.DropColumn(
                name: "SignedTs",
                table: "ReportRuns");

            migrationBuilder.DropColumn(
                name: "SignerSubject",
                table: "ReportRuns");

            migrationBuilder.DropColumn(
                name: "SignerThumbprint",
                table: "ReportRuns");

            migrationBuilder.DropColumn(
                name: "XlsxSignature",
                table: "ReportRuns");
        }
    }
}
