using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Models;

public sealed record TagValue(int TagId, double Value, TagQuality Quality, long Ts, long? SourceTs = null, string? Error = null);

public sealed record TagReadItem(int TagId, int Offset, TagDataType DataType, ByteOrder ByteOrder, int BitOffset = 0);

public sealed class ReadBlock
{
    public required TagArea Area { get; init; }
    public required int StartAddress { get; init; }
    public required int Length { get; init; }
    public int DbNumber { get; init; } = 1;
    public required IReadOnlyList<TagReadItem> Tags { get; init; }
}

public sealed class ReadPlan
{
    public int DeviceId { get; init; }
    public IReadOnlyList<ReadBlock> Blocks { get; init; } = Array.Empty<ReadBlock>();
    public IReadOnlyList<Tag> Tags { get; init; } = Array.Empty<Tag>();
}

public sealed record RawTagValue(int TagId, double Value, TagQuality Quality = TagQuality.Good, long? SourceTs = null, string? Error = null);

public sealed record CommandExecutionResult(bool Success, string Message, double? ReadBack = null, GatewayCommandOutcome? Outcome = null, Guid? CommandId = null);
public sealed record CollectorCommandContext(Guid? RequestId, int? UserId, string UserName);

public sealed record CollectorDeviceStatus(
    int DeviceId,
    string Name,
    bool Online,
    string? Error,
    long LastPollTs,
    double LastCycleMs,
    int TagCount,
    int ReconnectAttempts,
    long CommFailures,
    long? StatusTs = null);

public static class ScadaTime
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long ToMs(DateTime dateTime) =>
        new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime FromMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
}
