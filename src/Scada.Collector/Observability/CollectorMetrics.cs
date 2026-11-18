
using System.Diagnostics.Metrics;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Observability;

/// <summary>
/// 采集侧指标。诊断 DTO 只服务人看的面板，趋势与告警需要可聚合的时间序列：
/// 这里把采集耗时、失败分类、命令结果和总线等待暴露成 Meter，未挂监听器时几乎没有开销。
/// </summary>
internal static class CollectorMetrics
{
    private static readonly Meter Meter = new("Scada.Collector", "1.0");
    private static readonly Counter<long> Polls = Meter.CreateCounter<long>("scada.collector.polls", "{poll}", "采集轮次");
    private static readonly Counter<long> DegradedPolls = Meter.CreateCounter<long>("scada.collector.polls.degraded", "{poll}", "连接正常但存在坏点的采集轮次");
    private static readonly Histogram<double> PollDuration = Meter.CreateHistogram<double>("scada.collector.poll.duration", "ms", "单档采集耗时（含驱动往返）");
    private static readonly Counter<long> PollFailures = Meter.CreateCounter<long>("scada.collector.poll.failures", "{poll}", "采集失败轮次（按错误类别）");
    private static readonly Histogram<double> BusWait = Meter.CreateHistogram<double>("scada.collector.bus.wait", "ms", "共享总线排队等待");
    private static readonly Counter<long> Commands = Meter.CreateCounter<long>("scada.collector.commands", "{command}", "写命令结果");

    public static void Polled(ProtocolType protocol, double durationMs, int badPoints)
    {
        var tag = new KeyValuePair<string, object?>("protocol", protocol.ToString());
        Polls.Add(1, tag);
        PollDuration.Record(durationMs, tag);
        if (badPoints > 0) DegradedPolls.Add(1, tag);
    }

    public static void PollFailed(ProtocolType protocol, CollectorErrorKind kind)
    {
        PollFailures.Add(1,
            new KeyValuePair<string, object?>("protocol", protocol.ToString()),
            new KeyValuePair<string, object?>("kind", kind.ToString()));
    }

    public static void BusQueued(ProtocolType protocol, double waitMs) =>
        BusWait.Record(waitMs, new KeyValuePair<string, object?>("protocol", protocol.ToString()));

    public static void Commanded(ProtocolType protocol, string outcome) =>
        Commands.Add(1,
            new KeyValuePair<string, object?>("protocol", protocol.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome));

}
