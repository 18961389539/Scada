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

public enum HistoryResolution
{
    Raw,
    FiveMinute,
    Hour
}
