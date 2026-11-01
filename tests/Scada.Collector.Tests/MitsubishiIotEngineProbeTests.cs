using System.Diagnostics;
using IoTClient.Clients.PLC;
using IoTClient.Enums;
using Scada.Collector.Mitsubishi;
using Scada.Core.Protocols;
using Xunit;
using Xunit.Abstractions;

namespace Scada.Collector.Tests;

// Phase 0 探针：确认 IoTClient 在 TCP + 二进制 3E 下能否作为"纯传输层"使用
// （SendPackageSingle 收发原始 3E 帧），从而保留自研 McFrame 的结束码与批量语义。
public class MitsubishiIotEngineProbeTests(ITestOutputHelper output)
{
    private static string Hex(byte[]? data) => data is null ? "null" : string.Join(' ', data.Select(b => b.ToString("X2")));

    private static MitsubishiClient Open(McSimulator plc, int timeoutMs = 1500)
    {
        var client = new MitsubishiClient(MitsubishiVersion.Qna_3E, "127.0.0.1", plc.Port, timeoutMs);
        client.Open();
        return client;
    }

    [Fact]
    public async Task SendPackageSingleKeepsNativeFrameSemantics()
    {
        await using var plc = new McSimulator(McFormat.Binary, udp: false);
        plc.Words["D"][100] = 0x04D2; plc.Words["D"][101] = 0x8001; plc.Words["D"][102] = 0x1234;
        plc.Rejected.Add("R");

        var client = Open(plc);
        var o = new McDeviceOptions();

        var ok = client.SendPackageSingle(McFrame.Request(o, McFrame.CmdBatchRead, McFrame.SubWord, McAddress.Devices["D"], 100, 3));
        output.WriteLine($"OK read: ok={ok.IsSucceed} err={ok.Err} len={ok.Value?.Length} value={Hex(ok.Value)}");
        var data = McFrame.ResponseData(o, ok.Value!);
        var words = McFrame.Words(McFormat.Binary, data, 3);
        output.WriteLine($"decoded words={string.Join(',', words.Select(w => w.ToString("X4")))}");

        var rejected = client.SendPackageSingle(McFrame.Request(o, McFrame.CmdBatchRead, McFrame.SubWord, McAddress.Devices["R"], 0, 1));
        output.WriteLine($"Rejected: ok={rejected.IsSucceed} len={rejected.Value?.Length} value={Hex(rejected.Value)}");
        var ex = Assert.Throws<McEndCodeException>(() => McFrame.ResponseData(o, rejected.Value!).ToArray());
        output.WriteLine($"Rejected end code=0x{ex.Code:X4}");

        var longRead = client.SendPackageSingle(McFrame.Request(o, McFrame.CmdBatchRead, McFrame.SubWord, McAddress.Devices["D"], 0, 480));
        output.WriteLine($"Long 480 words: ok={longRead.IsSucceed} len={longRead.Value?.Length}");
        Assert.True(longRead.IsSucceed);
        Assert.Equal(9 + 2 + 960, longRead.Value!.Length);

        var write = client.SendPackageSingle(McFrame.Request(o, McFrame.CmdBatchWrite, McFrame.SubWord, McAddress.Devices["D"], 100, 1, new byte[] { 0xE7, 0x10 }));
        output.WriteLine($"Write: ok={write.IsSucceed} len={write.Value?.Length} value={Hex(write.Value)} stored={plc.Words["D"][100]:X4}");
        Assert.Equal(0x10E7, plc.Words["D"][100]);

        client.Close();
    }

    [Fact]
    public async Task SendPackageSingleTimeoutIsDetectable()
    {
        await using var plc = new McSimulator(McFormat.Binary, udp: false);
        var client = Open(plc, 1000);
        var o = new McDeviceOptions();
        plc.DropNext = 1;
        var started = Stopwatch.StartNew();
        var result = client.SendPackageSingle(McFrame.Request(o, McFrame.CmdBatchRead, McFrame.SubWord, McAddress.Devices["D"], 0, 1));
        output.WriteLine($"Drop: ok={result.IsSucceed} err={result.Err} code={result.ErrCode} len={result.Value?.Length} value={Hex(result.Value)} elapsed={started.ElapsedMilliseconds}ms connected={client.Connected} exception={result.Exception?.GetType().Name}:{result.Exception?.Message}");
        client.Close();
    }

    [Fact]
    public async Task AsciiFrameThroughSendPackageSingle()
    {
        await using var plc = new McSimulator(McFormat.Ascii, udp: false);
        plc.Words["D"][100] = 0x04D2;
        var client = Open(plc);
        var o = new McDeviceOptions { Format = McFormat.Ascii };
        var result = client.SendPackageSingle(McFrame.Request(o, McFrame.CmdBatchRead, McFrame.SubWord, McAddress.Devices["D"], 100, 1));
        output.WriteLine($"ASCII read: ok={result.IsSucceed} err={result.Err} len={result.Value?.Length} value={Hex(result.Value)}");
        Assert.False(result.IsSucceed, "IoTClient 无法收发 ASCII 3E 帧，ASCII 应走自研通道");
        client.Close();
    }
}
