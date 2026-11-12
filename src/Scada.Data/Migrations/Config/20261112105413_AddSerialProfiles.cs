using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddSerialProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SerialProfile",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Port = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BaudRate = table.Column<int>(type: "INTEGER", nullable: false),
                    DataBits = table.Column<int>(type: "INTEGER", nullable: false),
                    Parity = table.Column<int>(type: "INTEGER", nullable: false),
                    StopBits = table.Column<int>(type: "INTEGER", nullable: false),
                    Encoding = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    NewLine = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    ReadTimeoutMs = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxReplyChars = table.Column<int>(type: "INTEGER", nullable: false),
                    DtrEnable = table.Column<bool>(type: "INTEGER", nullable: false),
                    RtsEnable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SerialProfile", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SerialProfile_Name",
                table: "SerialProfile",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SerialProfile");
        }
    }
}
