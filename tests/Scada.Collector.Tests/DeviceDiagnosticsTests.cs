using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

// 诊断快照是运维唯一的聚合入口：分类计数必须单调累加，成功轮询不能清掉历史错误。
public sealed class DeviceDiagnosticsTests
{
    private static Device NewDevice() => new()
    {
        Id = 7, Name = "泵站", Protocol = ProtocolType.ModbusTcp, Host = "10.0.0.7", Port = 502,
        PollIntervalMs = 1000, TimeoutMs = 1000, Enabled = true
    };

    private static Tag NewTag() => new()
    {
        Id = 11, DeviceId = 7, Name = "温度", Kind = TagKind.Physical, DataType = TagDataType.Float, Address = 0
    };

    private static DeviceDiagnostics NewDiagnostics()
    {
        var tag = NewTag();
        return new DeviceDiagnostics(NewDevice(), [tag], new ReadPlan { DeviceId = 7, Tags = [tag] }, 3);
    }

    [Fact]
    public void FailuresAreCountedByKind()
    {
        var diagnostics = NewDiagnostics();
        diagnostics.ConnectionFailed("连接 10.0.0.7:502 超时", CollectorErrorKind.Timeout, 1000);
        diagnostics.ConnectionFailed("设备尚未连接", CollectorErrorKind.Connection, 1000);
        diagnostics.PollFailed(12, "MC 响应异常码 2", CollectorErrorKind.Protocol, 1000);

        var metrics = diagnostics.Snapshot(0, false).Metrics;
        Assert.Equal(1, metrics.Errors!.Timeout);
        Assert.Equal(1, metrics.Errors.Connection);
        Assert.Equal(1, metrics.Errors.Protocol);
        Assert.Equal(3, metrics.Errors.Total);
        Assert.Equal(CollectorErrorKind.Protocol, metrics.LastErrorKind);
        Assert.Equal(2, metrics.ConnectFailures);
        Assert.Equal(1, metrics.PollFailures);
    }

    [Fact]
    public void FailedSampleCarriesErrorKind()
    {
        var diagnostics = NewDiagnostics();
        diagnostics.PollFailed(12.5, "等待 PLC 响应超时", CollectorErrorKind.Timeout, 1000);

        var sample = Assert.Single(diagnostics.Snapshot(0, false).Samples);
        Assert.False(sample.Success);
        Assert.Equal(CollectorErrorKind.Timeout, sample.ErrorKind);
        Assert.Equal(12.5, sample.DurationMs);
    }

    [Fact]
    public void SuccessfulPollKeepsErrorHistory()
    {
        var diagnostics = NewDiagnostics();
        diagnostics.PollFailed(9, "读取超时", CollectorErrorKind.Timeout, 1000);
        diagnostics.Polled(4, null);

        var metrics = diagnostics.Snapshot(0, false).Metrics;
        Assert.Equal(1, metrics.Errors!.Total);
        Assert.Equal(CollectorErrorKind.Timeout, metrics.LastErrorKind);
        Assert.Equal(1, metrics.PollSuccesses);
        Assert.Equal(0, metrics.ConsecutiveFailures);
    }

    [Fact]
    public void NoFailureMeansEmptyCounters()
    {
        var diagnostics = NewDiagnostics();
        diagnostics.Polled(4, null);

        var metrics = diagnostics.Snapshot(0, false).Metrics;
        Assert.Equal(0, metrics.Errors!.Total);
        Assert.Equal(CollectorErrorKind.None, metrics.LastErrorKind);
    }
}
