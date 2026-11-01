using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddAlarmDiscreteActiveWhen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveWhen",
                table: "AlarmRule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // 旧的 StateChange（Type=4）语义就是位翻转报警，回填为 Discrete 的 Change（2），避免列表与接口显示成 On。
            migrationBuilder.Sql("""
                UPDATE "AlarmRule" SET "ActiveWhen" = 2 WHERE "Type" = 4;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveWhen",
                table: "AlarmRule");
        }
    }
}
