using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddCalculatedTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "DeviceId",
                table: "Tag",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<bool>(
                name: "CalculationEnabled",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "CalculationIntervalMs",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddColumn<long>(
                name: "CalculationRevision",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "Expression",
                table: "Tag",
                type: "TEXT",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "InputMaxAgeMs",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15000);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TagDependency",
                columns: table => new
                {
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagDependency", x => new { x.TagId, x.InputTagId });
                    table.ForeignKey(
                        name: "FK_TagDependency_Tag_InputTagId",
                        column: x => x.InputTagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TagDependency_Tag_TagId",
                        column: x => x.TagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tag_Name",
                table: "Tag",
                column: "Name",
                unique: true,
                filter: "DeviceId IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tag_Source",
                table: "Tag",
                sql: "(Kind = 0 AND DeviceId IS NOT NULL) OR (Kind = 1 AND DeviceId IS NULL AND Writable = 0)");

            migrationBuilder.CreateIndex(
                name: "IX_TagDependency_InputTagId",
                table: "TagDependency",
                column: "InputTagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse a destructive downgrade while device-less calculated definitions exist.
            migrationBuilder.Sql("CREATE TEMP TABLE __CalculationRollbackGuard(Value INTEGER CONSTRAINT CannotDowngradeWithCalculatedTags CHECK(Value=0)); INSERT INTO __CalculationRollbackGuard SELECT COUNT(*) FROM Tag WHERE Kind=1; DROP TABLE __CalculationRollbackGuard;");

            migrationBuilder.DropTable(
                name: "TagDependency");

            migrationBuilder.DropIndex(
                name: "IX_Tag_Name",
                table: "Tag");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tag_Source",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "CalculationEnabled",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "CalculationIntervalMs",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "CalculationRevision",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "Expression",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "InputMaxAgeMs",
                table: "Tag");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Tag");

            migrationBuilder.AlterColumn<int>(
                name: "DeviceId",
                table: "Tag",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
