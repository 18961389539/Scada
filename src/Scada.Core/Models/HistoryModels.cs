using Scada.Core.Enums;

namespace Scada.Core.Models;

public sealed record HistoryValue(int TagId, long Ts, double Value, TagQuality Quality);

public sealed record HistoryPoint(long Ts, double Value, TagQuality Quality);

public sealed class HistorySeries
{
    public int TagId { get; init; }
    public List<HistoryPoint> Points { get; init; } = new();
}

public sealed record AggregateValue(int TagId, long Ts, double Avg, double Min, double Max, int Count);

public sealed record AggregatePoint(int TagId, long Ts, double Avg, double Min, double Max, int Count);

// 质量区间：采样值保持到下一次采样，界面据此画出质量色带；Good 之外的质量不参与时间加权统计。
public sealed record HistoryQualityInterval(TagQuality Quality, long From, long To);

public sealed record HistoryTagAnalysis(
    int TagId,
    int Samples,
    int GoodSamples,
    long SpanMs,
    long GoodMs,
    double Coverage,
    double? TimeWeightedAvg,
    double? Min,
    double? Max,
    double IntegralHours,
    IReadOnlyList<HistoryQualityInterval> Quality,
    bool QualityTruncated);

public sealed record HistoryAnalysisResult(
    long From,
    long To,
    string Resolution,
    IReadOnlyList<HistoryTagAnalysis> Tags);

public enum HistoryResolution
{
    Raw,
    FiveMinute,
    Hour
}
