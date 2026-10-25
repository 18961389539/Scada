using System.Text.Json;
using Scada.Collector.OpcDa;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class OpcDaTests
{
    private sealed class FakeClient : IOpcDaClient
    {
        public readonly Dictionary<string, OpcDaSample> Items = new();
        public readonly List<(string Id, object Value)> Writes = [];
        public string? ConnectedTo; public int AddCalls; public bool ThrowOnRead, ThrowOnWrite; public int WriteCode;
        public bool Connected { get; private set; }
        public void Connect(string progId, string? node, int updateRateMs) { ConnectedTo = $"{progId}@{node}"; Connected = true; }
        public IReadOnlyList<int> AddItems(IReadOnlyList<string> ids) { AddCalls++; return ids.Select(i => Items.ContainsKey(i) ? 0 : unchecked((int)0xC0040007)).ToList(); }
        public IReadOnlyList<OpcDaSample> Read(IReadOnlyList<string> ids, bool fromDevice)
        {
            if (ThrowOnRead) throw new System.Runtime.InteropServices.COMException("RPC 服务器不可用", unchecked((int)0x800706BA));
            return ids.Select(i => Items[i]).ToList();
        }
        public int Write(string id, object value)
        {
            if (ThrowOnWrite) throw new System.Runtime.InteropServices.COMException("RPC 调用失败");
            if (WriteCode == 0) Writes.Add((id, value));
            return WriteCode;
        }
        public void Dispose() => Connected = false;
    }

    private static Tag T(int id, string item, TagDataType type, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = item, DataType = type, Writable = writable };
    private static Device D(object? options = null) => new()
    {
        Id = 1, Name = "da", Protocol = ProtocolType.OpcDa, TimeoutMs = 1000,
        ProtocolOptions = JsonSerializer.Serialize(options ?? new { progId = "Kepware.KEPServerEX.V6", node = "scada-host" })
    };

    [Fact]
    public async Task NonWindowsReportsClearMessage()
    {
        await using var d = new OpcDaDriver(() => new FakeClient(), () => false);
        var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => d.ConnectAsync(D()));
        Assert.Contains("DA→UA", ex.Message);
        Assert.Equal(OpcDaDriver.NotSupportedMessage, d.LastError);
        if (!OperatingSystem.IsWindows())
        {
            await using var real = new OpcDaDriver();
            Assert.Contains("Windows", (await Assert.ThrowsAsync<PlatformNotSupportedException>(() => real.ConnectAsync(D()))).Message);
        }
    }

    [Fact]
    public void Validation()
    {
        var device = new Device { Name = "d", Protocol = ProtocolType.OpcDa };
        Assert.Contains("ProgID", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"progId\":\"Matrikon.OPC.Simulation.1\",\"node\":\"bad host\"}"; Assert.Contains("节点", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"progId\":\"Matrikon.OPC.Simulation.1\"}"; Assert.Null(ConfigurationValidator.Device(device));
        Assert.Null(ConfigurationValidator.Tag(T(1, "Random.Real8", TagDataType.Double), device));
        Assert.Contains("空格", ConfigurationValidator.Tag(T(1, " Random.Real8", TagDataType.Double), device));
    }

    [Fact]
    public void QualityTimestampAndValueMapping()
    {
        Assert.Equal(TagQuality.Good, OpcDaDriver.MapQuality(0xC0).Quality);
        Assert.Equal(TagQuality.Uncertain, OpcDaDriver.MapQuality(0xD8).Quality); // Good + Local Override
        Assert.Equal(TagQuality.Uncertain, OpcDaDriver.MapQuality(0x44).Quality); // Last usable
        Assert.Contains("通信故障", OpcDaDriver.MapQuality(0x18).Detail);
        Assert.Equal(TagQuality.Bad, OpcDaDriver.MapQuality(0x00).Quality);
        var utc = new DateTime(2026, 10, 25, 1, 2, 3, DateTimeKind.Unspecified);
        var v = OpcDaDriver.Map(T(1, "x", TagDataType.Bool), new(true, 0xC0, utc, 0), true);
        Assert.Equal(1, v.Value); Assert.Equal(new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds(), v.SourceTs);
        Assert.Null(OpcDaDriver.Map(T(1, "x", TagDataType.Float), new(1.5f, 0xC0, DateTime.MinValue, 0), true).SourceTs);
        Assert.Equal(TagQuality.Bad, OpcDaDriver.Map(T(1, "x", TagDataType.Float), new("text", 0xC0, utc, 0), true).Quality);
        Assert.Equal(TagQuality.Bad, OpcDaDriver.Map(T(1, "x", TagDataType.Float), new(1, 0xC0, utc, unchecked((int)0xC0040007)), true).Quality);
        Assert.Equal(-1, OpcDaDriver.Map(T(1, "x", TagDataType.Int16), new((short)-1, 0xC0, utc, 0), true).Value);
        Assert.IsType<short>(OpcDaDriver.ToVariant(TagDataType.Int16, -3));
        Assert.IsType<bool>(OpcDaDriver.ToVariant(TagDataType.Bool, 1));
        Assert.Throws<InvalidOperationException>(() => OpcDaDriver.ToVariant(TagDataType.UInt16, -1));
        Assert.Throws<InvalidOperationException>(() => OpcDaDriver.ToVariant(TagDataType.Int32, 1.5));
    }

    [Fact]
    public async Task ReadsWritesAndDisconnectsWithFake()
    {
        var fake = new FakeClient();
        fake.Items["Ch.Dev.Temp"] = new(21.5, 0xC0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0);
        fake.Items["Ch.Dev.Run"] = new(false, 0x18, DateTime.UtcNow, 0);
        await using var d = new OpcDaDriver(() => fake, () => true);
        var device = D();
        await d.ConnectAsync(device);
        Assert.Equal("Kepware.KEPServerEX.V6@scada-host", fake.ConnectedTo);
        var plan = new ReadPlan { Tags = [T(1, "Ch.Dev.Temp", TagDataType.Float), T(2, "Ch.Dev.Run", TagDataType.Bool), T(3, "Ch.Dev.Missing", TagDataType.Float)] };
        var values = await d.ReadAsync(device, plan);
        Assert.Equal(21.5, values[0].Value); Assert.Equal(TagQuality.Good, values[0].Quality);
        Assert.Equal(TagQuality.Bad, values[1].Quality);
        Assert.Contains("0xC0040007", values[2].Error);
        await d.ReadAsync(device, plan);
        Assert.Equal(1, fake.AddCalls); // 已注册测点不重复添加
        await d.WriteAsync(device, T(1, "Ch.Dev.Temp", TagDataType.Float, true), 30);
        Assert.Equal(30f, fake.Writes.Single().Value);
        fake.WriteCode = unchecked((int)0xC004000B);
        Assert.Contains("未执行", (await Assert.ThrowsAsync<InvalidOperationException>(() => d.WriteAsync(device, T(1, "Ch.Dev.Temp", TagDataType.Float, true), 1))).Message);
        Assert.Contains("未执行", (await Assert.ThrowsAsync<InvalidOperationException>(() => d.WriteAsync(device, T(3, "Ch.Dev.Missing", TagDataType.Float, true), 1))).Message);
        fake.WriteCode = 0; fake.ThrowOnWrite = true;
        Assert.Contains("结果未知", (await Assert.ThrowsAsync<TimeoutException>(() => d.WriteAsync(device, T(1, "Ch.Dev.Temp", TagDataType.Float, true), 1))).Message);
        Assert.False(d.IsConnected);
        await d.ConnectAsync(device);
        fake.ThrowOnRead = true;
        await Assert.ThrowsAsync<IOException>(() => d.ReadAsync(device, plan));
        Assert.False(d.IsConnected);
    }
}
