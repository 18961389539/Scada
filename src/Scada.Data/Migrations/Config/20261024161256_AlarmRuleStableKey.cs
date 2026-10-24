using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AlarmRuleStableKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "AlarmRule",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // 已有规则补稳定键（随机 32 位十六进制），之后才能建唯一索引。
            migrationBuilder.Sql("UPDATE AlarmRule SET Key = lower(hex(randomblob(16))) WHERE Key = '';");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRule_TagId_Key",
                table: "AlarmRule",
                columns: new[] { "TagId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlarmRule_TagId_Key",
                table: "AlarmRule");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "AlarmRule");
        }
    }
}
