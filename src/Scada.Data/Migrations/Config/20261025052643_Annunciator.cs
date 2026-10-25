using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class Annunciator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnnunciatorPanel",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LatchMode = table.Column<int>(type: "INTEGER", nullable: false),
                    SilenceMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    FlashOnMs = table.Column<int>(type: "INTEGER", nullable: false),
                    FlashOffMs = table.Column<int>(type: "INTEGER", nullable: false),
                    CriticalFlashOnMs = table.Column<int>(type: "INTEGER", nullable: false),
                    CriticalFlashOffMs = table.Column<int>(type: "INTEGER", nullable: false),
                    BrowserSound = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnunciatorPanel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnnunciatorLevelMap",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PanelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Horn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Beacon = table.Column<bool>(type: "INTEGER", nullable: false),
                    TowerRed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TowerYellow = table.Column<bool>(type: "INTEGER", nullable: false),
                    TowerBlue = table.Column<bool>(type: "INTEGER", nullable: false),
                    TowerGreen = table.Column<bool>(type: "INTEGER", nullable: false),
                    Flash = table.Column<bool>(type: "INTEGER", nullable: false),
                    SoundKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnunciatorLevelMap", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnnunciatorLevelMap_AnnunciatorPanel_PanelId",
                        column: x => x.PanelId,
                        principalTable: "AnnunciatorPanel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnnunciatorOutput",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PanelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveValue = table.Column<double>(type: "REAL", nullable: false),
                    InactiveValue = table.Column<double>(type: "REAL", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnunciatorOutput", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnnunciatorOutput_AnnunciatorPanel_PanelId",
                        column: x => x.PanelId,
                        principalTable: "AnnunciatorPanel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnnunciatorOutput_Tag_TagId",
                        column: x => x.TagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnnunciatorSilence",
                columns: table => new
                {
                    PanelId = table.Column<int>(type: "INTEGER", nullable: false),
                    UntilTs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    ByUser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnunciatorSilence", x => x.PanelId);
                    table.ForeignKey(
                        name: "FK_AnnunciatorSilence_AnnunciatorPanel_PanelId",
                        column: x => x.PanelId,
                        principalTable: "AnnunciatorPanel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnnunciatorLevelMap_PanelId_Level",
                table: "AnnunciatorLevelMap",
                columns: new[] { "PanelId", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnnunciatorOutput_PanelId_Kind",
                table: "AnnunciatorOutput",
                columns: new[] { "PanelId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnnunciatorOutput_TagId",
                table: "AnnunciatorOutput",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_AnnunciatorPanel_Name",
                table: "AnnunciatorPanel",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnnunciatorLevelMap");

            migrationBuilder.DropTable(
                name: "AnnunciatorOutput");

            migrationBuilder.DropTable(
                name: "AnnunciatorSilence");

            migrationBuilder.DropTable(
                name: "AnnunciatorPanel");
        }
    }
}
