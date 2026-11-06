using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Server.Services;

namespace Scada.Server.Tests;

// 网关规则直接决定"一个坏包是重试还是丢弃"以及"轮换密码后网关会不会拿到新配置"，
// 这些判断原来埋在 686 行的 GatewayCenterService 里，没有任何独立测试。
public sealed class GatewayRulesTests
{
    private const long Now = 1_700_000_000_000;
    private static readonly string Revision = new('A', 64);

    [Fact]
    public void TransientFailuresCoverSqliteContentionAndIoOnly()
    {
        Assert.True(GatewayRules.IsTransient(new SqliteException("database is locked", 5)));
        Assert.True(GatewayRules.IsTransient(new SqliteException("database table is locked", 6)));
        Assert.True(GatewayRules.IsTransient(new SqliteException("disk I/O error", 10)));
        Assert.True(GatewayRules.IsTransient(new IOException("磁盘不可用")));
        Assert.True(GatewayRules.IsTransient(new TimeoutException()));
        Assert.True(GatewayRules.IsTransient(new InvalidOperationException("包装", new IOException("内层"))));

        // 永久错误：语义问题重试多少次都不会好，累计到上限就该转死信。
        Assert.False(GatewayRules.IsTransient(new InvalidDataException("载荷校验失败")));
        Assert.False(GatewayRules.IsTransient(new JsonException("格式错误")));
        Assert.False(GatewayRules.IsTransient(new GatewayRequestException(400, "数据包无效")));
    }

    [Fact]
    public void RetryBackoffDoublesAndClampsToTheConfiguredCeiling()
    {
        var settings = new GatewaySettings { InboxRetryBaseMs = 1000, InboxRetryMaxMs = 30_000 };

        Assert.Equal(1000, GatewayRules.RetryDelayMs(settings, 1));
        Assert.Equal(2000, GatewayRules.RetryDelayMs(settings, 2));
        Assert.Equal(4000, GatewayRules.RetryDelayMs(settings, 3));
        // 次数很大时不溢出、不超过上限。
        Assert.Equal(30_000, GatewayRules.RetryDelayMs(settings, 200));
    }

    [Fact]
    public void DeadLetterPagingOrdersByTimeThenSequenceThenStreamText()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var row = new GatewayInboxBatch { GatewayId = 1, Sequence = 5, StreamId = second, DeadLetteredTs = 100 };

        Assert.True(GatewayRules.IsOlderLetter(row, 200, 1, first));    // 失败时间更早
        Assert.False(GatewayRules.IsOlderLetter(row, 100, 1, first));   // 同一时刻，序号更大
        Assert.True(GatewayRules.IsOlderLetter(row, 100, 6, first));    // 同一时刻，序号更小
        Assert.False(GatewayRules.IsOlderLetter(row, 100, 5, first));   // 同时刻同序号：数据流文本序更大
        Assert.True(GatewayRules.IsOlderLetter(row, 100, 5, Guid.Parse("00000000-0000-0000-0000-0000000000ff")));
    }

    [Fact]
    public void PacketValidationRejectsUntrustedShapes()
    {
        GatewayRules.ValidatePacket(Packet(Value(1, Now)), Now);

        AssertRejected(Packet() with { Values = [] });                                     // 空批
        AssertRejected(Packet(Value(1, Now), Value(1, Now)));                              // 重复测点
        AssertRejected(Packet(Value(0, Now)));                                             // 非法测点
        AssertRejected(Packet(Value(1, Now + 200_000)));                                   // 未来时标
        AssertRejected(Packet(Value(1, Now, value: double.NaN)));                          // 非有限值
        AssertRejected(Packet(Value(1, Now)) with { Sequence = 0 });                                    // 序号非法
        AssertRejected(Packet(Value(1, Now)) with { ConfigurationRevision = "short" });                 // 版本指纹非法
    }

    [Fact]
    public void ConfigurationRevisionChangesWhenProtectedPasswordRotates()
    {
        var device = new Device { Id = 1, Name = "PLC-1", OpcPasswordProtected = "cipher-v1" };
        var tags = Array.Empty<Tag>();

        var before = GatewayRules.ConfigurationMetadata([device], tags).Revision;
        var same = GatewayRules.ConfigurationMetadata([device], tags).Revision;
        device.OpcPasswordProtected = "cipher-v2";
        var rotated = GatewayRules.ConfigurationMetadata([device], tags).Revision;

        Assert.Equal(before, same);
        Assert.NotEqual(before, rotated);

        // 下发给网关的配置里不能出现受保护的密码字段。
        var configuration = GatewayRules.ConfigurationMetadata([device], tags);
        Assert.Null(configuration.Devices.Single().Device.OpcPasswordProtected);
    }

    [Fact]
    public void GatewayNameIsTrimmedAndBounded()
    {
        Assert.Equal("边缘网关", GatewayRules.Name("  边缘网关  "));
        Assert.Throws<GatewayRequestException>(() => GatewayRules.Name(""));
        Assert.Throws<GatewayRequestException>(() => GatewayRules.Name(null));
        Assert.Throws<GatewayRequestException>(() => GatewayRules.Name(new string('x', 101)));
    }

    private static void AssertRejected(GatewayTelemetry packet) =>
        Assert.Equal(400, Assert.Throws<GatewayRequestException>(() => GatewayRules.ValidatePacket(packet, Now)).StatusCode);

    private static TagValue Value(int tagId, long ts, TagQuality quality = TagQuality.Good, double value = 1) =>
        new(tagId, value, quality, ts);

    private static GatewayTelemetry Packet(params TagValue[] values) =>
        new(GatewayProtocol.Version, Guid.NewGuid(), 1, 1, Revision, Now, values);
}
