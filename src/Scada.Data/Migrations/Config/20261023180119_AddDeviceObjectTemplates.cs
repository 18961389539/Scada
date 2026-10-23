using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddDeviceObjectTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObjectTemplate",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DraftJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    LatestVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectTemplate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ObjectInstance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    BaseAddress = table.Column<int>(type: "INTEGER", nullable: false),
                    ScreenId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    BaselineJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectInstance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjectInstance_Device_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Device",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ObjectInstance_ObjectTemplate_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ObjectTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ObjectInstance_ProcessScreen_ScreenId",
                        column: x => x.ScreenId,
                        principalTable: "ProcessScreen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObjectTemplateVersion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedTs = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectTemplateVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjectTemplateVersion_ObjectTemplate_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ObjectTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjectInstance_DeviceId_Name",
                table: "ObjectInstance",
                columns: new[] { "DeviceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObjectInstance_ScreenId",
                table: "ObjectInstance",
                column: "ScreenId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectInstance_TemplateId",
                table: "ObjectInstance",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectTemplate_Name",
                table: "ObjectTemplate",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObjectTemplateVersion_TemplateId_Version",
                table: "ObjectTemplateVersion",
                columns: new[] { "TemplateId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObjectInstance");

            migrationBuilder.DropTable(
                name: "ObjectTemplateVersion");

            migrationBuilder.DropTable(
                name: "ObjectTemplate");
        }
    }
}
