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
    IReadOnlyList<CollectorTagDiagnostic> Points, IReadOnlyList<CollectorDiagnosticEvent> Events, IReadOnlyList<CollectorPollSample> Samples);
