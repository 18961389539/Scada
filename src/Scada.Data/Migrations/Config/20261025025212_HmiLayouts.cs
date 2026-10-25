using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class HmiLayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HmiLayout",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmiLayout", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HmiWorkstation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LayoutId = table.Column<int>(type: "INTEGER", nullable: true),
                    AllowControl = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HmiWorkstation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HmiWorkstation_HmiLayout_LayoutId",
                        column: x => x.LayoutId,
                        principalTable: "HmiLayout",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HmiLayout_Name",
                table: "HmiLayout",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HmiWorkstation_LayoutId",
                table: "HmiWorkstation",
                column: "LayoutId");

            migrationBuilder.CreateIndex(
                name: "IX_HmiWorkstation_Name",
                table: "HmiWorkstation",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HmiWorkstation");

            migrationBuilder.DropTable(
                name: "HmiLayout");
        }
    }
}
