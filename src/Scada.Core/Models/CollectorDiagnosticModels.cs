using Scada.Core.Enums;

namespace Scada.Core.Models;

/// <summary>
/// 采集失败的归因类别。现场排障关心的不是"又失败了一次"，而是"哪一类问题在反复发生"：
/// 连接被拒、响应超时、协议异常码、认证失败、配置错误需要完全不同的处置。
/// 分类只依据异常类型和少量明确关键字，拿不准归 Unknown，宁可少归类也不误归类。
/// </summary>
public enum CollectorErrorKind { None, Timeout, Connection, Protocol, Authorization, Configuration, Io, Unknown }

public sealed record CollectorErrorCounts(long Timeout, long Connection, long Protocol, long Authorization, long Configuration, long Io, long Unknown)
{
    public static readonly CollectorErrorCounts Empty = new(0, 0, 0, 0, 0, 0, 0);
    public long Total => Timeout + Connection + Protocol + Authorization + Configuration + Io + Unknown;
}

public sealed record CollectorDiagnosticEvent(long Id, long Ts, string Level, string Kind, string Message, int? TagId = null);
public sealed record CollectorPollSample(long Ts, double DurationMs, bool Success, int Good, int Bad, string? Error, CollectorErrorKind ErrorKind = CollectorErrorKind.None);
public sealed record CollectorReadGroup(int Index, string Area, int? DbNumber, int? StartAddress, int Length, string Unit, IReadOnlyList<int> TagIds);
public sealed record CollectorTagDiagnostic(int TagId, string Name, string Address, string DataType, double? RawValue,
    double? Value, TagQuality? Quality, long? Ts, long? SourceTs, long? LastGoodTs, long? AgeMs, bool Stale, string? Error);
public sealed record CollectorQualityCounts(int Good, int Bad, int Uncertain, int CommFailure, int Unknown, int Stale);
public sealed record CollectorDiagnosticMetrics(long ConnectAttempts, long ConnectFailures, long PollAttempts, long PollSuccesses, long PollFailures,
    long DegradedPolls, int ConsecutiveFailures, long? LastAttemptTs, long? LastSuccessTs, long? LastFailureTs, long? NextRetryTs,
    string? LastFailure, long CommandsSucceeded, long CommandsFailed, long CommandsExpired, long CommandsRejected,
    long? LastCommandTs, double LastPollMs, double WindowAvgMs, double WindowMaxMs, double WindowP95Ms, int WindowSamples,
    CollectorErrorCounts? Errors = null, CollectorErrorKind LastErrorKind = CollectorErrorKind.None);
public sealed record CollectorDeviceDiagnostics(int DeviceId, string Name, string Protocol, string Connection, string State,
    string Operation, int? OperationTagId, string? CurrentError, bool Enabled, bool ConfigurationPending, int ConfigVersion,
    long CapturedTs, long StartedTs, int PollIntervalMs, int TimeoutMs, long StaleAfterMs, int PendingCommands,
    int PointCount, CollectorQualityCounts Quality, CollectorDiagnosticMetrics Metrics, IReadOnlyList<CollectorReadGroup> ReadGroups,
    IReadOnlyList<CollectorTagDiagnostic> Points, IReadOnlyList<CollectorDiagnosticEvent> Events, IReadOnlyList<CollectorPollSample> Samples, int? GatewayId = null, string? ConfigurationRevision = null);

public sealed record CollectorDiagnosticQuery(int Page = 1, int PageSize = 100, string? Search = null, TagQuality? Quality = null, bool StaleOnly = false);
public sealed record CollectorDiagnosticPage(CollectorDeviceDiagnostics Diagnostics, int Total, int Page, int PageSize);
public static class CollectorDiagnosticPaging
{
    public static void Validate(CollectorDiagnosticQuery query)
    {
        if (query.Page is < 1 or > 1_000_000 || query.PageSize is < 1 or > 200 || query.Search?.Length > 200 || query.Quality is not null && !Enum.IsDefined(query.Quality.Value))
            throw new ArgumentException("分页、搜索或质量参数无效（每页 1～200 条）");
    }
    public static CollectorDiagnosticPage Page(CollectorDeviceDiagnostics snapshot, CollectorDiagnosticQuery query)
    {
        Validate(query);
        var points = snapshot.Points.Where(x => (query.Quality is null || x.Quality == query.Quality) && (!query.StaleOnly || x.Stale)
            && (string.IsNullOrWhiteSpace(query.Search) || x.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || x.Address.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || x.TagId.ToString() == query.Search)).ToArray();
        return new(snapshot with { Points = points.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArray() }, points.Length, query.Page, query.PageSize);
    }
}
