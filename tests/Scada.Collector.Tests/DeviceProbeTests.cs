using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

// 设备在线时测试读取应复用采集连接，不能二次打开串口/连接。
public class DeviceProbeTests
{
    private sealed class StubCollector(CollectorProbeResult? result) : ICollectorControl
    {
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() =>
            [new CollectorDeviceStatus(7, "probe", true, null, 0, 0, 1, 0, 0)];

        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandExecutionResult(false, "stub"));

        public void RequestReload() { }

        public Task<CollectorProbeResult?> ProbeAsync(int deviceId, Tag tag, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static Device Device() => new()
    {
        Id = 7,
        Name = "probe",
        Protocol = ProtocolType.ModbusTcp,
        Host = "127.0.0.1",
        Port = 502,
        StationNumber = 1
    };

    private static DeviceProbeRequest Request() =>
        new(TagArea.HoldingRegister, 10, TagDataType.UInt16);

    [Fact]
    public async Task ReusesTheActiveCollectionConnection()
    {
        var collector = new StubCollector(new CollectorProbeResult(true, new RawTagValue(-1, 42.5)));

        // drivers/plans 传 null：复用路径不应创建独立连接。
        var result = await DeviceProbe.ReadAsync(Device(), Request(), null!, null!, CancellationToken.None, collector);

        Assert.True(result.Connected);
        Assert.Equal(42.5, result.Value);
        Assert.Equal(TagArea.HoldingRegister.ToString(), result.Area);
    }

    [Fact]
    public async Task ReportsProbeErrorInsteadOfOpeningASecondConnection()
    {
        var collector = new StubCollector(new CollectorProbeResult(true, null, "从站没有响应"));

        var result = await DeviceProbe.ReadAsync(Device(), Request(), null!, null!, CancellationToken.None, collector);

        Assert.False(result.Connected);
        Assert.Contains("从站没有响应", result.Message);
    }

    [Fact]
    public async Task PreservesQualityWhenReusedValueIsBad()
    {
        var collector = new StubCollector(new CollectorProbeResult(true, new RawTagValue(-1, 0, TagQuality.Bad, null, "设备返回异常码 02")));

        var result = await DeviceProbe.ReadAsync(Device(), Request(), null!, null!, CancellationToken.None, collector);

        Assert.True(result.Connected);
        Assert.Equal(TagQuality.Bad, result.Quality);
        Assert.Contains("异常码", result.Message);
    }
}