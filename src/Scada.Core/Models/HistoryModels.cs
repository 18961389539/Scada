using Scada.Core.Enums;

namespace Scada.Core.Models;

// 字符串测点的历史点只带 Text，Value 保持 0；数值点 Text 为 null。
public sealed record HistoryValue(int TagId, long Ts, double Value, TagQuality Quality, string? Text = null);

public sealed record HistoryPoint(long Ts, double Value, TagQuality Quality, string? Text = null);

public sealed class HistorySeries
{
    public int TagId { get; init; }
    public List<HistoryPoint> Points { get; init; } = new();
}

public sealed record AggregateValue(int TagId, long Ts, double Avg, double Min, double Max, int Count, int BadCount = 0);

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

/// <summary>
/// 历史库后端占用情况（不含写入暂存 spool）。文件名分类只由具体后端解释。
/// </summary>
public sealed record HistoryStoreUsage(
    long RawBytes,
    long AggregateBytes,
    long JournalBytes,
    long OtherBytes,
    int RawFiles,
    int AggregateFiles,
    bool ScanComplete);

/// <summary>
/// 待聚合窗口。Shard 是后端不透明标识（SQLite 下为月库/年库文件名），调用方不要解析。
/// </summary>
public sealed record HistoryPendingWindow(string Shard, bool Raw, long Ts, long WindowMs, long Revision);

/// <summary>
/// 历史复制水位。属性名与主备协议 JSON 保持一致（File / Table / MaxRowId）。
/// </summary>
public sealed record HistoryReplicaCursor(string File, string Table, long MaxRowId);

/// <summary>
/// 历史复制增量批次。属性名与主备协议 JSON 保持一致。
/// 每行第 0 个元素仍是游标（SQLite 下为 rowid），其余为列值。
/// 列值统一用不变文化字符串承载，接收端按本机列类型解析（数值往返用 R 格式，文本列原样传输）。
/// </summary>
public sealed record HistoryReplicaRows(string File, string Table, IReadOnlyList<string?[]> Rows, IReadOnlyList<string>? Columns = null);

/// <summary>历史复制在后端内部失败时抛出，由主备层映射为协议错误。</summary>
public sealed class HistoryReplicaException(string message, int status = 409) : Exception(message)
{
    public int Status { get; } = status;
}
