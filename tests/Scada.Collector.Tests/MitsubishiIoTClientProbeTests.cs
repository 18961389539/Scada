using IoTClient.Clients.PLC;
using IoTClient.Enums;
using Scada.Core.Protocols;
using Xunit;

namespace Scada.Collector.Tests;

// 兼容性探针（不参与生产代码）：确认 IoTClient 的三菱 3E(TCP) 帧与地址语法 "D100"
// 能与本项目既有的 3E 二进制模拟器互通。若该测试通过，说明"用 IoTClient 替换自研三菱驱动"在
// TCP + 二进制路径上可行；UDP 与 3E-ASCII（自研支持、IoTClient 不支持）不在此覆盖范围。
public class MitsubishiIoTClientProbeTests
{
    [Fact]
    public async Task IoTClientReadsAndWritesWordsAgainstTheExisting3ESimulator()
    {
        await using var plc = new McSimulator(McFormat.Binary, udp: false);
        plc.Words["D"][100] = 1234;

        var client = new MitsubishiClient(MitsubishiVersion.Qna_3E, "127.0.0.1", plc.Port, 2000);
        client.Open();

        var read = client.ReadInt16("D100");
        Assert.True(read.IsSucceed, $"IoTClient 读取失败：{read.Err}");
        Assert.Equal((short)1234, read.Value);

        var write = client.Write("D100", (short)4321);
        Assert.True(write.IsSucceed, $"IoTClient 写入失败：{write.Err}");
        Assert.Equal(4321, plc.Words["D"][100]);

        Assert.True(plc.Requests >= 2, "既有模拟器应至少收到一次读与一次写");
        client.Close();
    }

}