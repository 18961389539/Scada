using Scada.Collector.Iec61850;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class Iec61850Tests
{
    private sealed class Fake : IIec61850Client
    {
        public bool Connected { get; private set; }
        public List<(string, double)> Writes = [];
        public Task ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct) { Connected = true; return Task.CompletedTask; }
        public Task<IReadOnlyList<Iec61850Sample>> ReadAsync(IReadOnlyList<Iec61850Reference> refs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Iec61850Sample>>(refs.Select(r => r.Path switch
            {
                "TotW.mag.f" => new Iec61850Sample(12.5, 0, 1000, null),
                "Pos.stVal" => new Iec61850Sample(2, 3, 2000, null),
                _ => new Iec61850Sample(null, null, null, "object-non-existent"),
            }).ToList());
        public Task WriteAsync(Iec61850Reference r, double value, CancellationToken ct) { Writes.Add((r.MmsItem, value)); return Task.CompletedTask; }
        public void Dispose() => Connected = false;
    }

    private static Tag T(int id, string r, TagDataType type, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = r, DataType = type, Writable = writable };

    [Fact]
    public void ReferenceParsingAndValidation()
    {
        Assert.True(Iec61850Reference.TryParse("IED1LD0/MMXU1.MX.TotW.mag.f", out var r, out _));
        Assert.Equal("MMXU1$MX$TotW$mag$f", r.MmsItem); Assert.Equal("IED1LD0", r.LogicalDevice);
        Assert.False(Iec61850Reference.TryParse("IED1LD0/MMXU1.XX.TotW", out _, out _));
        Assert.False(Iec61850Reference.TryParse("MMXU1.MX.TotW", out _, out _));
        var device = new Device { Name = "i", Protocol = ProtocolType.Iec61850, Host = "h", Port = 102 };
        Assert.Null(ConfigurationValidator.Device(device));
        Assert.Contains("可写", ConfigurationValidator.Tag(T(1, "LD/XCBR1.ST.Pos.stVal", TagDataType.Int32, true), device));
        Assert.Contains("可写", ConfigurationValidator.Tag(T(1, "LD/CSWI1.CO.Pos", TagDataType.Bool), device));
        Assert.Null(ConfigurationValidator.Tag(T(1, "LD/CSWI1.CO.Pos", TagDataType.Bool, true), device));
    }

    [Fact]
    public async Task WithoutImplementationConnectExplainsLicensing()
    {
        await using var d = new Iec61850Driver();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => d.ConnectAsync(new Device { Host = "127.0.0.1", Port = 102 }));
        Assert.Contains("GPLv3", ex.Message);
    }

    [Fact]
    public async Task AdapterMappingWithFake()
    {
        Assert.Equal(TagQuality.Bad, Iec61850Driver.MapQuality(1).Quality);
        Assert.Equal(TagQuality.Uncertain, Iec61850Driver.MapQuality(3).Quality);
        Assert.Equal(TagQuality.Uncertain, Iec61850Driver.MapQuality(1 << 11).Quality);
        Assert.Equal(TagQuality.Good, Iec61850Driver.MapQuality(0).Quality);
        var fake = new Fake();
        await using var d = new Iec61850Driver(() => fake);
        var device = new Device { Host = "127.0.0.1", Port = 102 };
        await d.ConnectAsync(device);
        var v = await d.ReadAsync(device, new ReadPlan { Tags = [T(1, "LD/MMXU1.MX.TotW.mag.f", TagDataType.Float), T(2, "LD/XCBR1.ST.Pos.stVal", TagDataType.Int32), T(3, "LD/X1.ST.Nope", TagDataType.Float)] });
        Assert.Equal(12.5, v[0].Value); Assert.Equal(1000, v[0].SourceTs);
        Assert.Equal(TagQuality.Uncertain, v[1].Quality);
        Assert.Equal(TagQuality.Bad, v[2].Quality);
        await d.WriteAsync(device, T(4, "LD/CSWI1.CO.Pos", TagDataType.Bool, true), 1);
        Assert.Equal(("CSWI1$CO$Pos", 1.0), fake.Writes.Single());
    }
}
