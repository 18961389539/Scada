using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddAreasRolesAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AreaId",
                table: "Tag",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details",
                table: "OperationLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetId",
                table: "OperationLog",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetType",
                table: "OperationLog",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AreaId",
                table: "Device",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "AccessRole",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Permissions = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRole", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Area",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Area", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserRoleAssignment",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoleAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignment_AccessRole_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AccessRole",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignment_Area_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Area",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignment_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Area",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[] { 1, "升级前的设备与未指定区域的设备", "默认区域" });

            migrationBuilder.CreateIndex(
                name: "IX_Tag_AreaId",
                table: "Tag",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_Device_AreaId",
                table: "Device",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRole_Name",
                table: "AccessRole",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Area_Name",
                table: "Area",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignment_AreaId",
                table: "UserRoleAssignment",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignment_RoleId",
                table: "UserRoleAssignment",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignment_UserId_RoleId_AreaId",
                table: "UserRoleAssignment",
                columns: new[] { "UserId", "RoleId", "AreaId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserRoleAssignment");

            migrationBuilder.DropTable(
                name: "AccessRole");

            migrationBuilder.DropTable(
                name: "Area");

            migrationBuilder.DropIndex(
                name: "IX_Tag_AreaId",
                table: "Tag");

            migrationBuilder.DropIndex(
                name: "IX_Device_AreaId",
                table: "Device");

            migrationBuilder.DropColumn(
                name: "AreaId",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "Details",
                table: "OperationLog");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "OperationLog");

            migrationBuilder.DropColumn(
                name: "TargetType",
                table: "OperationLog");

            migrationBuilder.DropColumn(
                name: "AreaId",
                table: "Device");
        }
    }
}
