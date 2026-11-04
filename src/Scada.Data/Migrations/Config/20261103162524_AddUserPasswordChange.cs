using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddUserPasswordChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "User",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PasswordChangedTs",
                table: "User",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite 不支持 DROP COLUMN，EF 会直接抛 NotSupportedException。
            // 降级时保留这两列：旧版本代码不读取它们，列上的默认值也不会影响登录。
            // 需要真正回退时走「迁移前备份」还原整库（见 DatabaseInitializer.BackupBeforeMigration）。
        }
    }
}
