using Scada.Core.Enums;

namespace Scada.Core.Models;

public sealed record CollectorDiagnosticEvent(long Id, long Ts, string Level, string Kind, string Message, int? TagId = null);
public sealed record CollectorPollSample(long Ts, double DurationMs, bool Success, int Good, int Bad, string? Error);
public sealed record CollectorReadGroup(int Index, string Area, int? DbNumber, int? StartAddress, int Length, string Unit, IReadOnlyList<int> TagIds);
public sealed record CollectorTagDiagnostic(int TagId, string Name, string Address, string DataType, double? RawValue,
    double? Value, TagQuality? Quality, long? Ts, long? SourceTs, long? LastGoodTs, long? AgeMs, bool Stale, string? Error);
public sealed record CollectorQualityCounts(int Good, int Bad, int Uncertain, int CommFailure, int Unknown, int Stale);
public sealed record CollectorDiagnosticMetrics(long ConnectAttempts, long ConnectFailures, long PollAttempts, long PollSuccesses, long PollFailures,
    long DegradedPolls, int ConsecutiveFailures, long? LastAttemptTs, long? LastSuccessTs, long? LastFailureTs, long? NextRetryTs,
    string? LastFailure, long CommandsSucceeded, long CommandsFailed, long CommandsExpired, long CommandsRejected,
    long? LastCommandTs, double LastPollMs, double WindowAvgMs, double WindowMaxMs, double WindowP95Ms, int WindowSamples);
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
