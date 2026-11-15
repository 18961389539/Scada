using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AuditOutboxAndObjectPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AreaId",
                table: "ProcessScreen",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartAddress",
                table: "ObjectInstance",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AuditOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptTs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessScreen_AreaId",
                table: "ProcessScreen",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditOutbox_CreatedTs",
                table: "AuditOutbox",
                column: "CreatedTs");

            migrationBuilder.CreateIndex(
                name: "IX_AuditOutbox_NextAttemptTs",
                table: "AuditOutbox",
                column: "NextAttemptTs");

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessScreen_Area_AreaId",
                table: "ProcessScreen",
                column: "AreaId",
                principalTable: "Area",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProcessScreen_Area_AreaId",
                table: "ProcessScreen");

            migrationBuilder.DropTable(
                name: "AuditOutbox");

            migrationBuilder.DropIndex(
                name: "IX_ProcessScreen_AreaId",
                table: "ProcessScreen");

            migrationBuilder.DropColumn(
                name: "AreaId",
                table: "ProcessScreen");

            migrationBuilder.DropColumn(
                name: "StartAddress",
                table: "ObjectInstance");
        }
    }
}
