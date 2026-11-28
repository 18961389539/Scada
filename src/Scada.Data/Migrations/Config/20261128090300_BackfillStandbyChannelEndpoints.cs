using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scada.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class BackfillStandbyChannelEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 升级前已配置 StandbyChannelId 但未落章备用端点的设备，按通道目录回填，避免继续沿用主路径串口或地址。
            migrationBuilder.Sql("""
UPDATE Device SET
    StandbySerialPort = (SELECT SerialPort FROM Channel WHERE Channel.Id = Device.StandbyChannelId),
    StandbyBaudRate = (SELECT BaudRate FROM Channel WHERE Channel.Id = Device.StandbyChannelId),
    StandbyDataBits = (SELECT DataBits FROM Channel WHERE Channel.Id = Device.StandbyChannelId),
    StandbyParity = (SELECT Parity FROM Channel WHERE Channel.Id = Device.StandbyChannelId),
    StandbyStopBits = (SELECT StopBits FROM Channel WHERE Channel.Id = Device.StandbyChannelId),
    StandbyTimeoutMs = (SELECT TimeoutMs FROM Channel WHERE Channel.Id = Device.StandbyChannelId)
WHERE StandbyChannelId IS NOT NULL
  AND (StandbySerialPort IS NULL OR StandbySerialPort = '')
  AND EXISTS (SELECT 1 FROM Channel WHERE Channel.Id = Device.StandbyChannelId AND Channel.Protocol IN (1, 12, 15, 16, 17));
""");
            migrationBuilder.Sql("""
UPDATE Device SET
    StandbyHost = (SELECT Host FROM Channel WHERE Channel.Id = Device.StandbyChannelId),
    StandbyPort = (SELECT Port FROM Channel WHERE Channel.Id = Device.StandbyChannelId),
    StandbyTimeoutMs = (SELECT TimeoutMs FROM Channel WHERE Channel.Id = Device.StandbyChannelId)
WHERE StandbyChannelId IS NOT NULL
  AND (StandbyHost IS NULL OR StandbyHost = '')
  AND StandbyPort IS NULL
  AND EXISTS (SELECT 1 FROM Channel WHERE Channel.Id = Device.StandbyChannelId AND Channel.Protocol NOT IN (1, 12, 15, 16, 17));
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
