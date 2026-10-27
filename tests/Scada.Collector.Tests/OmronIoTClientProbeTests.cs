using IoTClient.Clients.PLC;
using IoTClient.Enums;
using Xunit;

namespace Scada.Collector.Tests;

// 兼容性探针（不参与生产代码）：确认 IoTClient 的欧姆龙 FINS/TCP 帧与地址语法 "D100"
// 能与本项目既有的 FINS 模拟器互通（含节点握手）。通过则说明 TCP 路径可替换；
// FINS/UDP（自研支持、IoTClient 不支持）不在此覆盖范围。
public class OmronIoTClientProbeTests
{
    // 结论（2026-10-26 探测）：IoTClient 的欧姆龙客户端与本项目/PLC 的大端语义不兼容
    //   · ReadInt16("D100") 返回 0xD204（应为 0x04D2），整字按小端解释，且 EndianFormat 对 16 位无影响
    //   · Read("D100", 1, false, false) 只返回 1 字节，参数语义与预期不符
    //   · 源码/文档不可达，无法确认正确用法
    // 因此欧姆龙 FINS 继续使用自研驱动（已支持 TCP/UDP、连续块批量与结束码容错）。
    [Fact(Skip = "IoTClient 欧姆龙读 API 字节序/语义与本项目不兼容，欧姆龙保留自研驱动")]
    public async Task IoTClientReadsAndWritesWordsAgainstTheExistingFinsSimulator()
    {
        await using var plc = new FinsSimulator(tcp: true);
        plc.Memory[0x82][100] = 1234; // D 区（DM）

        // 注意：IoTClient 的 EndianFormat 语义与直觉相反——ABCD 实际按小端解释，BADC 才是"大端整字"。
        var client = new OmronFinsClient("127.0.0.1", plc.Port, 2000, EndianFormat.BADC);
        client.Open();

        // 驱动实现应走"原始字节读 + 自研 WordCodec 解码"，而不依赖 IoTClient 的类型读
        // （IoTClient 的 Omron ReadInt16 按小端解释，与本项目/PLC 大端语义不符；EndianFormat 不影响 16 位）。
        var raw = client.Read("D100", 1, false, false);
        Assert.True(raw.IsSucceed, $"IoTClient 读取失败：{raw.Err}");
        Assert.Equal(new byte[] { 0x04, 0xD2 }, raw.Value);

        var write = client.Write("D100", new byte[] { 0x10, 0xE7 }, false); // 4321 = 0x10E7
        Assert.True(write.IsSucceed, $"IoTClient 写入失败：{write.Err}");
        Assert.Equal(4321, plc.Memory[0x82][100]);

        client.Close();
    }
}