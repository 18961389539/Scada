using System.Text;
using System.Text.Json;
using Scada.Collector.Mqtt;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class SparkplugTests
{
    private static Tag T(int id, string metric, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = metric, DataType = TagDataType.Double, Writable = writable };

    private static SparkplugPayload P(ulong? seq, params SparkplugMetric[] metrics)
    {
        var p = new SparkplugPayload { Seq = seq, Timestamp = 1_700_000_000_000 };
        p.Metrics.AddRange(metrics); return p;
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> ok, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs; T last;
        do { last = await read(); if (ok(last)) return last; await Task.Delay(50); } while (Environment.TickCount64 < until);
        return last;
    }

    [Fact]
    public void PayloadRoundTripsAllScalarTypes()
    {
        var p = P(7,
            new SparkplugMetric { Name = "i8", Alias = 1, DataType = SparkplugDataType.Int8, Value = -5 },
            new SparkplugMetric { Name = "i32", DataType = SparkplugDataType.Int32, Value = -123456 },
            new SparkplugMetric { Name = "u32", DataType = SparkplugDataType.UInt32, Value = 4_000_000_000 },
            new SparkplugMetric { Name = "i64", DataType = SparkplugDataType.Int64, Value = -9_000_000_000 },
            new SparkplugMetric { Name = "f", DataType = SparkplugDataType.Float, Value = 1.5 },
            new SparkplugMetric { Name = "d", DataType = SparkplugDataType.Double, Value = Math.PI, Timestamp = 5 },
            new SparkplugMetric { Name = "b", DataType = SparkplugDataType.Boolean, Value = 1 },
            new SparkplugMetric { Name = "s", DataType = SparkplugDataType.String, StringValue = "x" },
            new SparkplugMetric { Name = "n", DataType = SparkplugDataType.Double, IsNull = true });
        var back = SparkplugPayload.Decode(p.Encode());
        Assert.Equal(7UL, back.Seq); Assert.Equal(1_700_000_000_000UL, back.Timestamp);
        Assert.Equal([-5, -123456, 4_000_000_000, -9_000_000_000, 1.5, Math.PI, 1], back.Metrics.Take(7).Select(m => m.Value!.Value));
        Assert.Equal(1UL, back.Metrics[0].Alias); Assert.Equal(5UL, back.Metrics[5].Timestamp);
        Assert.Equal("x", back.Metrics[7].StringValue); Assert.Null(back.Metrics[7].Value);
        Assert.True(back.Metrics[8].IsNull);
        Assert.Throws<FormatException>(() => SparkplugPayload.Decode(new byte[] { 0x12, 0x50, 0x01 }));
    }

    [Fact]
    public void DataWithoutDatatypeUsesBirthType()
    {
        // Metric { alias = 12, int_value = 0xFFFFFFFE }（省略 datatype）
        var bytes = new byte[] { 0x12, 0x08, 0x10, 0x0C, 0x50, 0xFE, 0xFF, 0xFF, 0xFF, 0x0F };
        var m = SparkplugPayload.Decode(bytes).Metrics.Single();
        Assert.Equal(SparkplugDataType.Unknown, m.DataType);
        Assert.Equal(-2, m.ValueAs(SparkplugDataType.Int16));
        Assert.Equal(4294967294, m.ValueAs(SparkplugDataType.UInt32));
    }

    [Fact]
    public async Task HostTracksBirthAliasesSeqDeathAndWritesThroughDcmd()
    {
        await using var broker = await BrokerFixture.StartAsync();
        await broker.SubscribeAsync("spBv1.0/#");
        const string g = "G1", n = "Edge1", d = "Pump1";
        string Topic(string type, bool dev = true) => $"spBv1.0/{g}/{type}/{n}" + (dev ? $"/{d}" : "");
        ulong seq = 0;
        double flow = 10, setpoint = 0;
        async Task Birth()
        {
            seq = 0;
            await broker.PublishAsync(Topic("NBIRTH", false), P(seq, new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, Value = 3 }, new SparkplugMetric { Name = "Node Control/Rebirth", DataType = SparkplugDataType.Boolean, Value = 0 }).Encode());
            await broker.PublishAsync(Topic("DBIRTH"), P(++seq,
                new SparkplugMetric { Name = "Flow", Alias = 10, DataType = SparkplugDataType.Float, Value = flow },
                new SparkplugMetric { Name = "Setpoint", Alias = 11, DataType = SparkplugDataType.Double, Value = setpoint }).Encode());
        }
        // 模拟边缘节点：响应 Rebirth 与 DCMD。
        broker.PeerMessage += async (topic, payload) =>
        {
            if (topic == Topic("NCMD", false) && SparkplugPayload.Decode(payload).Metrics.Any(m => m.Name == "Node Control/Rebirth")) await Birth();
            if (topic == Topic("DCMD"))
            {
                var m = SparkplugPayload.Decode(payload).Metrics.Single();
                Assert.Equal(11UL, m.Alias); setpoint = m.Value!.Value;
                await broker.PublishAsync(Topic("DDATA"), P(++seq % 256, new SparkplugMetric { Alias = 11, DataType = SparkplugDataType.Double, Value = setpoint }).Encode());
            }
        };
        var device = new Device { Id = 1, Name = "sp", Protocol = ProtocolType.SparkplugB, Host = "127.0.0.1", Port = broker.Port, TimeoutMs = 1500,
            ProtocolOptions = JsonSerializer.Serialize(new { groupId = g, edgeNodeId = n, deviceId = d, primaryHostId = "ScadaHost" }) };
        Assert.Null(ConfigurationValidator.Device(device));
        await using var driver = new SparkplugDriver(new PlainCredentials());
        await driver.ConnectAsync(device);
        // Primary Host STATE 上线（保留消息）
        Assert.Contains(broker.PeerInbox, x => x.Topic == "spBv1.0/STATE/ScadaHost" && Encoding.UTF8.GetString(x.Payload).Contains("\"online\":true"));
        var plan = new ReadPlan { Tags = [T(1, "Flow"), T(2, "Setpoint", true), T(3, "Missing")] };
        // 无出生证书时驱动发送 Rebirth 请求，模拟节点随即出生。
        var values = await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Quality == TagQuality.Good);
        Assert.Equal(10, values[0].Value); Assert.Equal(TagQuality.Bad, values[2].Quality);
        // DDATA 仅带别名
        await broker.PublishAsync(Topic("DDATA"), P(++seq, new SparkplugMetric { Alias = 10, DataType = SparkplugDataType.Float, Value = 12.5 }).Encode());
        values = await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Value == 12.5);
        Assert.Equal(12.5, values[0].Value);
        // 写入经 DCMD，并等待 DDATA 回报
        await driver.WriteAsync(device, plan.Tags[1], 42);
        Assert.Equal(42, setpoint);
        values = await driver.ReadAsync(device, plan); Assert.Equal(42, values[1].Value);
        // 序号缺口：值变为 Uncertain 并请求 Rebirth（模拟节点重新出生后恢复 Good）
        seq += 5;
        var gaps = driver.SeqGaps;
        await broker.PublishAsync(Topic("DDATA"), P(seq, new SparkplugMetric { Alias = 10, DataType = SparkplugDataType.Float, Value = 13 }).Encode());
        await Eventually(() => Task.FromResult(driver.SeqGaps), x => x > gaps);
        Assert.True(driver.SeqGaps > gaps);
        values = await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Quality == TagQuality.Good);
        Assert.Equal(TagQuality.Good, values[0].Quality);
        // DDEATH → CommFailure
        await broker.PublishAsync(Topic("DDEATH"), P(++seq).Encode());
        values = await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Quality == TagQuality.CommFailure);
        Assert.Contains("DDEATH", values[0].Error);
        await Assert.ThrowsAsync<IOException>(() => driver.WriteAsync(device, plan.Tags[1], 1));
    }

    [Fact]
    public async Task StaleNdeathWithOtherBdSeqIsIgnored()
    {
        await using var broker = await BrokerFixture.StartAsync();
        var device = new Device { Id = 1, Name = "sp", Protocol = ProtocolType.SparkplugB, Host = "127.0.0.1", Port = broker.Port, TimeoutMs = 1500,
            ProtocolOptions = JsonSerializer.Serialize(new { groupId = "G", edgeNodeId = "N", requestRebirth = false }) };
        await using var driver = new SparkplugDriver(new PlainCredentials());
        await driver.ConnectAsync(device);
        await broker.PublishAsync("spBv1.0/G/NBIRTH/N", P(0, new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, Value = 5 }, new SparkplugMetric { Name = "Temp", DataType = SparkplugDataType.Double, Value = 20 }).Encode());
        var plan = new ReadPlan { Tags = [T(1, "Temp")] };
        Assert.Equal(20, (await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Quality == TagQuality.Good))[0].Value);
        await broker.PublishAsync("spBv1.0/G/NDEATH/N", P(null, new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, Value = 4 }).Encode());
        await Task.Delay(200);
        Assert.Equal(TagQuality.Good, (await driver.ReadAsync(device, plan))[0].Quality);
        await broker.PublishAsync("spBv1.0/G/NDEATH/N", P(null, new SparkplugMetric { Name = "bdSeq", DataType = SparkplugDataType.Int64, Value = 5 }).Encode());
        Assert.Equal(TagQuality.CommFailure, (await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Quality == TagQuality.CommFailure))[0].Quality);
    }
}
