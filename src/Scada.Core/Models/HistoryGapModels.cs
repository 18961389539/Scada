namespace Scada.Core.Models;

public enum HistoryGapKind
{
    BadQuality,
    Missing,
    Rejected,
    SpoolDropped,
    Simulated
}

/// <summary>
/// 趋势或分析上需要画出的缺口。TagId 为空表示整库/总线级事件（拒收、暂存丢弃）。
/// </summary>
public sealed record HistoryGap(int? TagId, long From, long To, HistoryGapKind Kind, string? Detail = null);
