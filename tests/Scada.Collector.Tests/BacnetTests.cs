using System.IO.BACnet;
using System.Text.Json;
using Scada.Collector.Bacnet;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;
using ProtocolType = Scada.Core.Enums.ProtocolType;
using T_ = System.IO.BACnet.BacnetObjectTypes;
using A_ = System.IO.BACnet.BacnetApplicationTags;

namespace Scada.Collector.Tests;

public class BacnetTests
{
    private static Tag T(int id, string address, TagDataType type, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = address, DataType = type, Writable = writable };

    private static Device D(int port, object? options = null) => new()
    {
        Id = 1, Name = "bac", Protocol = ProtocolType.BacnetIp, Host = "127.0.0.1", Port = port, TimeoutMs = 800,
        ProtocolOptions = JsonSerializer.Serialize(options ?? new { deviceInstance = 1234 })
    };

    private static ReadPlan Plan(params Tag[] tags) => new() { DeviceId = 1, Tags = tags };

    private static BacnetSimulator Sim()
    {
        var sim = new BacnetSimulator(1234);
        sim.Add(T_.OBJECT_ANALOG_INPUT, 1, 21.5f, A_.BACNET_APPLICATION_TAG_REAL);
        sim.Add(T_.OBJECT_ANALOG_VALUE, 2, 0f, A_.BACNET_APPLICATION_TAG_REAL);
        sim.Add(T_.OBJECT_BINARY_OUTPUT, 3, 0u, A_.BACNET_APPLICATION_TAG_ENUMERATED);
        sim.Add(T_.OBJECT_MULTI_STATE_VALUE, 4, 1u, A_.BACNET_APPLICATION_TAG_UNSIGNED_INT);
        sim.Add(T_.OBJECT_BINARY_INPUT, 5, 1u, A_.BACNET_APPLICATION_TAG_ENUMERATED);
        return sim;
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> ok, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs; T last;
        do { last = await read(); if (ok(last)) return last; await Task.Delay(30); } while (Environment.TickCount64 < until);
        return last;
    }

    [Fact]
    public void AddressAndDeviceValidation()
    {
        Assert.True(BacnetPointAddress.TryParse("av:10;pri=12", out var a, out _));
        Assert.Equal(BacnetObjectKind.AV, a.Kind); Assert.Equal(10u, a.Instance); Assert.Equal(12, a.Priority);
        Assert.False(BacnetPointAddress.TryParse("XX:1", out _, out _));
        Assert.False(BacnetPointAddress.TryParse("AV:1;pri=17", out _, out _));
        Assert.False(BacnetPointAddress.TryParse("AV:1;pri=6", out _, out _));
        Assert.False(BacnetPointAddress.TryParse("AV:4194303", out _, out _));
        var device = new Device { Name = "b", Protocol = ProtocolType.BacnetIp, Host = "h", Port = 47808 };
        Assert.Contains("实例号", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"deviceInstance\":1,\"writePriority\":6}"; Assert.Contains("优先级", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"deviceInstance\":1}"; Assert.Null(ConfigurationValidator.Device(device));
        Assert.Contains("Bool", ConfigurationValidator.Tag(T(1, "BI:1", TagDataType.Float), device));
        Assert.Contains("Bool", ConfigurationValidator.Tag(T(1, "AV:1", TagDataType.Bool), device));
        Assert.Contains("可写", ConfigurationValidator.Tag(T(1, "AI:1", TagDataType.Float, true), device));
        Assert.Contains("整数", ConfigurationValidator.Tag(T(1, "MSV:1", TagDataType.Float), device));
        Assert.Null(ConfigurationValidator.Tag(T(1, "MSV:1;pri=10", TagDataType.UInt16, true), device));
    }

    [Fact]
    public void StatusFlagsMapToQuality()
    {
        IList<BacnetValue> Pv(object v) => [new BacnetValue(A_.BACNET_APPLICATION_TAG_REAL, v)];
        IList<BacnetValue> F(string s) => [new BacnetValue(A_.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.Parse(s))];
        Assert.Equal(TagQuality.Good, BacnetDriver.Map(Pv(1f), F("1000")).Quality); // IN_ALARM 不影响品质
        Assert.Equal(TagQuality.Bad, BacnetDriver.Map(Pv(1f), F("0100")).Quality);
        Assert.Equal(TagQuality.Uncertain, BacnetDriver.Map(Pv(1f), F("0010")).Quality);
        Assert.Equal(TagQuality.Uncertain, BacnetDriver.Map(Pv(1f), F("0001")).Quality);
        Assert.Equal(TagQuality.Bad, BacnetDriver.Map([new BacnetValue(A_.BACNET_APPLICATION_TAG_NULL, null)], null).Quality);
        Assert.Throws<InvalidOperationException>(() => BacnetDriver.Encode(BacnetObjectKind.MSV, 0));
        Assert.Throws<InvalidOperationException>(() => BacnetDriver.Encode(BacnetObjectKind.MSV, 1.5));
        Assert.Equal(A_.BACNET_APPLICATION_TAG_ENUMERATED, BacnetDriver.Encode(BacnetObjectKind.BV, 5).Tag);
    }

    [Fact]
    public async Task WhoIsThenReadMultipleWithQualityAndErrors()
    {
        using var sim = Sim();
        sim.Objects[new BacnetObjectId(T_.OBJECT_ANALOG_VALUE, 2)].Flags = "0001";
        await using var d = new BacnetDriver();
        var device = D(sim.Port);
        await d.ConnectAsync(device);
        Assert.True(d.IsConnected); Assert.NotNull(d.PeerMaxApdu);
        var values = await d.ReadAsync(device, Plan(T(1, "AI:1", TagDataType.Float), T(2, "AV:2", TagDataType.Float), T(3, "BI:5", TagDataType.Bool),
            T(4, "MSV:4", TagDataType.UInt16), T(5, "AI:99", TagDataType.Float), T(6, "AI:1", TagDataType.Double)));
        Assert.Equal(21.5, values.Single(v => v.TagId == 1).Value);
        Assert.Equal(TagQuality.Good, values.Single(v => v.TagId == 1).Quality);
        Assert.Equal(TagQuality.Uncertain, values.Single(v => v.TagId == 2).Quality);
        Assert.Equal(1, values.Single(v => v.TagId == 3).Value);
        Assert.Equal(1, values.Single(v => v.TagId == 4).Value);
        Assert.Equal(TagQuality.Bad, values.Single(v => v.TagId == 5).Quality);
        Assert.Contains("UNKNOWN_OBJECT", values.Single(v => v.TagId == 5).Error);
        Assert.Equal(21.5, values.Single(v => v.TagId == 6).Value);
        Assert.Equal(1, sim.RpmRequests); // 重复对象合并、一次 RPM
    }

    [Fact]
    public async Task FallsBackToReadPropertyWhenRpmRejectedAndWithoutIAm()
    {
        using var sim = Sim();
        sim.RejectRpm = true; sim.AnswerWhoIs = false;
        await using var d = new BacnetDriver();
        var device = D(sim.Port);
        await d.ConnectAsync(device); // 无 I-Am 时读设备对象确认可达
        var values = await d.ReadAsync(device, Plan(T(1, "AI:1", TagDataType.Float), T(2, "BO:3", TagDataType.Bool)));
        Assert.Equal(21.5, values[0].Value); Assert.Equal(TagQuality.Good, values[1].Quality);
        Assert.False(d.ReadMultipleActive);
        await d.ReadAsync(device, Plan(T(1, "AI:1", TagDataType.Float)));
        Assert.Equal(1, sim.RpmRequests);
    }

    [Fact]
    public async Task WrongInstanceOrSilentDeviceFailsToConnect()
    {
        using var sim = Sim();
        await using var d = new BacnetDriver();
        var wrong = await Assert.ThrowsAsync<IOException>(() => d.ConnectAsync(D(sim.Port, new { deviceInstance = 99 })));
        Assert.Contains("99", wrong.Message);
        sim.Silent = true;
        var silent = await Assert.ThrowsAsync<IOException>(() => d.ConnectAsync(D(sim.Port)));
        Assert.Contains("无应答", silent.Message);
        Assert.False(d.IsConnected);
    }

    [Fact]
    public async Task WritesUsePriorityAndReportRejectionsAndUnknownOutcome()
    {
        using var sim = Sim();
        await using var d = new BacnetDriver();
        var device = D(sim.Port, new { deviceInstance = 1234, writePriority = 10 });
        await d.ConnectAsync(device);
        await d.WriteAsync(device, T(1, "AV:2", TagDataType.Float, true), 42.25);
        await d.WriteAsync(device, T(2, "BO:3;pri=1", TagDataType.Bool, true), 1);
        await d.WriteAsync(device, T(3, "MSV:4", TagDataType.UInt16, true), 3);
        var writes = sim.Writes.ToArray();
        Assert.Equal((byte)10, writes[0].Priority); Assert.Equal(42.25f, writes[0].Value);
        Assert.Equal((byte)1, writes[1].Priority); Assert.Equal(1u, writes[1].Value);
        Assert.Equal(3u, writes[2].Value);
        var values = await d.ReadAsync(device, Plan(T(1, "AV:2", TagDataType.Float), T(2, "BO:3", TagDataType.Bool), T(3, "MSV:4", TagDataType.UInt16)));
        Assert.Equal([42.25, 1, 3], values.Select(v => v.Value));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => d.WriteAsync(device, T(3, "MSV:4", TagDataType.UInt16, true), 9));
        Assert.Contains("未执行", rejected.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => d.WriteAsync(device, T(4, "AI:1", TagDataType.Float, true), 1));
        sim.Silent = true;
        var unknown = await Assert.ThrowsAsync<TimeoutException>(() => d.WriteAsync(device, T(1, "AV:2", TagDataType.Float, true), 1));
        Assert.Contains("结果未知", unknown.Message);
        Assert.Equal(3, sim.Writes.Count); // 未重发
    }

    [Fact]
    public async Task CovSubscriptionPushesValuesAndFallsBackToPolling()
    {
        using var sim = Sim();
        sim.Objects[new BacnetObjectId(T_.OBJECT_BINARY_INPUT, 5)].CovSupported = false;
        await using var d = new BacnetDriver();
        var device = D(sim.Port, new { deviceInstance = 1234, useCov = true, covLifetimeSec = 120 });
        await d.ConnectAsync(device);
        var plan = Plan(T(1, "AI:1", TagDataType.Float), T(2, "BI:5", TagDataType.Bool));
        await d.ReadAsync(device, plan);
        Assert.Equal(1, sim.Subscriptions);
        await Eventually(() => Task.FromResult(d.CovNotifications), n => n > 0);
        var rpm = sim.RpmRequests;
        sim.Set(T_.OBJECT_ANALOG_INPUT, 1, 30f, "0100");
        var v = await Eventually(() => d.ReadAsync(device, plan), r => r[0].Value == 30);
        Assert.Equal(TagQuality.Bad, v[0].Quality); // FAULT
        Assert.Equal(1, v[1].Value);
        sim.Set(T_.OBJECT_BINARY_INPUT, 5, 0u);
        v = await d.ReadAsync(device, plan);
        Assert.Equal(0, v[1].Value); // 不支持 COV 的对象继续轮询
        Assert.Equal(1, sim.Subscriptions); // 未到续订时间
        Assert.True(sim.RpmRequests > rpm);
    }

    [Fact]
    public async Task NoResponseDuringReadDisconnects()
    {
        using var sim = Sim();
        await using var d = new BacnetDriver();
        var device = D(sim.Port, new { deviceInstance = 1234, readRetries = 0 });
        await d.ConnectAsync(device);
        sim.Silent = true;
        await Assert.ThrowsAsync<IOException>(() => d.ReadAsync(device, Plan(T(1, "AI:1", TagDataType.Float))));
        Assert.False(d.IsConnected);
        sim.Silent = false;
        await d.ConnectAsync(device);
        Assert.Equal(21.5, (await d.ReadAsync(device, Plan(T(1, "AI:1", TagDataType.Float))))[0].Value);
    }
}
