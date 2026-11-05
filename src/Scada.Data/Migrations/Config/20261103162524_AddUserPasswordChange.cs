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
            // 结构编辑器已能在 SQLite 上删列；空 Down 会在回滚后再升级时撞上 duplicate column。
            migrationBuilder.DropColumn(name: "PasswordChangedTs", table: "User");
            migrationBuilder.DropColumn(name: "MustChangePassword", table: "User");
        }
    }
}
