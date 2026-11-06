using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Collector.Iec104;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Server.External.Slave;

namespace Scada.Server.Tests;

/// <summary>
/// IEC 104 从站的协议级测试：用真实 TCP 跑一遍 STARTDT → 总召唤 → ACT_TERM，
/// 校验 ASDU 类型、IOA、品质位与短浮点小端编码。这些是最容易被现场主站挑出来的细节。
/// </summary>
public sealed class ExternalIec104ServerTests
{
    [Fact]
    public async Task Interrogation_returns_startdt_con_then_data_then_activation_termination()
    {
        var (map, options) = BuildSite();
        var (tcp, port) = await StartServerAsync(map, options);
        using var listenerHandle = tcp;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var stream = client.GetStream();

        await stream.WriteAsync(Iec104Codec.U(Iec104Types.U_StartDtAct), cts.Token);
        var start = await ReadApduAsync(stream, cts.Token);
        Assert.Equal(ApciKind.U, start.Kind);
        Assert.Equal(Iec104Types.U_StartDtCon, start.UFunction);

        await stream.WriteAsync(Iec104Codec.I(0, 0, new Asdu(Iec104Types.C_IC_NA, false, Iec104Types.CotAct,
            false, false, 0, 1, [new InfoObject(0, [20])])), cts.Token);

        var received = new List<Apdu>();
        while (true)
        {
            var apdu = await ReadApduAsync(stream, cts.Token);
            received.Add(apdu);
            if (apdu.Asdu is { TypeId: Iec104Types.C_IC_NA, Cause: Iec104Types.CotActTerm }) break;
            if (received.Count > 40) throw new TimeoutException("总召唤未在预期帧数内结束");
        }

        var asdus = received.Select(a => a.Asdu!).Where(a => a is not null).ToArray();
        Assert.Equal(Iec104Types.CotActCon, asdus[0].Cause);

        var single = asdus.Single(a => a.TypeId == Iec104Types.M_SP_NA);
        Assert.Equal(100, single.Objects[0].Ioa);
        Assert.Equal(1, single.Objects[0].Element[0] & 0x01);
        Assert.Equal(0x00, (byte)(single.Objects[0].Element[0] & 0x80));

        var measured = asdus.Single(a => a.TypeId == Iec104Types.M_ME_NC);
        Assert.Equal(200, measured.Objects[0].Ioa);
        // 104 短浮点是小端，和 Modbus 相反。
        Assert.Equal(42.5f, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(measured.Objects[0].Element));
        Assert.Equal(Iec104Types.CotInrogen, measured.Cause);
        Assert.Equal(Iec104Types.CotActTerm, asdus[^1].Cause);
    }

    [Fact]
    public async Task Bad_quality_is_reported_as_invalid_quality_bit()
    {
        var (map, options) = BuildSite(badQuality: true);
        var (tcp, port) = await StartServerAsync(map, options);
        using var listenerHandle = tcp;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Iec104Codec.U(Iec104Types.U_StartDtAct), cts.Token);
        _ = await ReadApduAsync(stream, cts.Token);

        await stream.WriteAsync(Iec104Codec.I(0, 0, new Asdu(Iec104Types.C_IC_NA, false, Iec104Types.CotAct,
            false, false, 0, 1, [new InfoObject(0, [20])])), cts.Token);

        byte? qds = null;
        while (true)
        {
            var apdu = await ReadApduAsync(stream, cts.Token);
            if (apdu.Asdu is { TypeId: Iec104Types.M_ME_NC } m) qds = m.Objects[0].Element[4];
            if (apdu.Asdu is { TypeId: Iec104Types.C_IC_NA, Cause: Iec104Types.CotActTerm }) break;
        }
        Assert.NotNull(qds);
        // IV（bit7）必须置位：质量坏时不能让主站以为是有效值。
        Assert.Equal(0x80, qds!.Value & 0x80);
    }

    [Fact]
    public async Task Unknown_common_address_is_ignored()
    {
        var (map, options) = BuildSite();
        var (tcp, port) = await StartServerAsync(map, options);
        using var listenerHandle = tcp;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Iec104Codec.U(Iec104Types.U_StartDtAct), cts.Token);
        _ = await ReadApduAsync(stream, cts.Token);

        // 公共地址 9 不在配置里：从站不应给出任何总召唤响应。
        await stream.WriteAsync(Iec104Codec.I(0, 0, new Asdu(Iec104Types.C_IC_NA, false, Iec104Types.CotAct,
            false, false, 0, 9, [new InfoObject(0, [20])])), cts.Token);

        var read = stream.ReadAsync(new byte[64], cts.Token).AsTask();
        var completed = await Task.WhenAny(read, Task.Delay(1200, cts.Token));
        Assert.NotSame(read, completed);
    }

    private static (ExportPointMap Map, Iec104ServerOptions Options) BuildSite(bool badQuality = false)
    {
        var config = new FakeConfig();
        config.TagList.Add(new Tag
        {
            Id = 1, Name = "运行", Kind = TagKind.Physical, DataType = TagDataType.Bool,
            Area = TagArea.Coil, Address = 0, Writable = true
        });
        config.TagList.Add(new Tag
        {
            Id = 2, Name = "温度", Kind = TagKind.Physical, DataType = TagDataType.Float,
            Area = TagArea.HoldingRegister, Address = 0, Writable = true
        });

        var cache = new FakeCache();
        cache.Set(new TagValue(1, 1, TagQuality.Good, 1_700_000_000_000));
        cache.Set(new TagValue(2, 42.5, badQuality ? TagQuality.CommFailure : TagQuality.Good, 1_700_000_000_000));

        var options = new Iec104ServerOptions
        {
            UseTimeTag = false,
            CommonAddresses = [1],
            Points =
            [
                new Iec104ServerPoint { TagId = 1, Ioa = 100, Kind = Iec104PointKind.SinglePoint },
                new Iec104ServerPoint { TagId = 2, Ioa = 200, Kind = Iec104PointKind.MeasuredFloat }
            ]
        };

        var map = new ExportPointMap(config, cache);
        map.Ensure(new ModbusSlaveOptions(), options);
        return (map, options);
    }

    private static async Task<(TcpListener Listener, int Port)> StartServerAsync(ExportPointMap map, Iec104ServerOptions options)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var services = new ServiceCollection().BuildServiceProvider();
        var router = new ExportWriteRouter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExportWriteRouter>.Instance);

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var session = new Iec104ServerSession(client.GetStream(), options, map, router,
                    NullLogger.Instance, "127.0.0.1", 1);
                await session.RunAsync(cts.Token);
            }
            catch { }
            finally { try { listener.Stop(); } catch { } }
        });
        await Task.Yield();
        return (listener, port);
    }

    private static async Task<Apdu> ReadApduAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        await ReadExactAsync(stream, header, ct);
        var rest = new byte[header[1]];
        await ReadExactAsync(stream, rest, ct);
        var frame = new byte[2 + header[1]];
        header.CopyTo(frame, 0);
        rest.CopyTo(frame, 2);
        return Iec104Codec.Decode(frame);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private sealed class FakeConfig : IConfigSnapshot
    {
        public List<Tag> TagList { get; } = [];
        public IReadOnlyList<Tag> Tags => TagList;
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag)
        {
            tag = TagList.FirstOrDefault(t => t.Id == tagId)!;
            return tag is not null;
        }
        public bool TryGetDevice(int deviceId, out Device device)
        {
            device = null!;
            return false;
        }
    }

    private sealed class FakeCache : IRealtimeCache
    {
        private readonly ConcurrentDictionary<int, TagValue> _values = new();
        public void Set(TagValue value) => _values[value.TagId] = value;
        public void Remove(int tagId) => _values.TryRemove(tagId, out _);
        public TagValue? Get(int tagId) => _values.TryGetValue(tagId, out var v) ? v : null;
        public IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds) =>
            tagIds.Select(Get).Where(v => v is not null).Cast<TagValue>().ToArray();
        public IReadOnlyDictionary<int, TagValue> Snapshot() => _values;
    }
}
