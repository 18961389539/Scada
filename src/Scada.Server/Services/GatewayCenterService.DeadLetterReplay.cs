using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;
using HistoryValue = Scada.Core.Models.HistoryValue;

using static Scada.Server.Services.GatewayRules;

namespace Scada.Server.Services;

public sealed record GatewayDeadLetterPreview(
    int GatewayId,
    Guid StreamId,
    long Sequence,
    bool CanReplay,
    bool AlreadyReplayed,
    int WriteCount,
    int SkipCount,
    string? Reason,
    IReadOnlyList<string> Notes);

public sealed partial class GatewayCenterService
{
    public const string ReplayedPrefix = "已回放：";

    public Task<GatewayDeadLetterPreview> PreviewDeadLetterAsync(int gatewayId, Guid streamId, long sequence, CancellationToken ct) =>
        InspectDeadLetterAsync(gatewayId, streamId, sequence, apply: false, userId: 0, userName: "", ct);

    public Task<GatewayDeadLetterPreview> ReplayDeadLetterAsync(int gatewayId, Guid streamId, long sequence, int userId, string userName, CancellationToken ct) =>
        InspectDeadLetterAsync(gatewayId, streamId, sequence, apply: true, userId, userName, ct);

    private async Task<GatewayDeadLetterPreview> InspectDeadLetterAsync(
        int gatewayId, Guid streamId, long sequence, bool apply, int userId, string userName, CancellationToken ct)
    {
        if (apply) await _mutations.WaitAsync(ct);
        try
        {
            await using var runtime = await OpenRuntimeAsync(ct);
            var batch = await runtime.GatewayInbox.FirstOrDefaultAsync(
                x => x.GatewayId == gatewayId && x.StreamId == streamId && x.Sequence == sequence, ct);
            if (batch is null) throw new GatewayRequestException(404, "死信不存在");
            if (batch.DeadLetteredTs is null)
                throw new GatewayRequestException(409, "该批次不是死信，不能回放", "dead_letter_not_ready");

            if (IsAlreadyReplayed(batch.ProcessingError))
            {
                return new GatewayDeadLetterPreview(gatewayId, streamId, sequence, CanReplay: false, AlreadyReplayed: true,
                    0, 0, "该死信已经回放过，不会重复归档", ["幂等：按数据流与序号识别，已标记为已回放"]);
            }

            var planned = await PlanDeadLetterAsync(batch, ct);
            if (planned.Error is not null)
            {
                return new GatewayDeadLetterPreview(gatewayId, streamId, sequence, false, false, 0, 0, planned.Error, planned.Notes);
            }

            if (!apply)
            {
                return new GatewayDeadLetterPreview(gatewayId, streamId, sequence, true, false,
                    planned.History.Count, planned.SkipCount, null, planned.Notes);
            }

            if (planned.History.Count > 0)
                await _history.WriteGatewayHistoryAsync(planned.History, ct);

            await using var runtimeTx = await runtime.Database.BeginTransactionAsync(ct);
            batch = await runtime.GatewayInbox.FirstOrDefaultAsync(
                x => x.GatewayId == gatewayId && x.StreamId == streamId && x.Sequence == sequence, ct)
                ?? throw new GatewayRequestException(404, "死信不存在");
            if (IsAlreadyReplayed(batch.ProcessingError))
            {
                await runtimeTx.RollbackAsync(ct);
                return new GatewayDeadLetterPreview(gatewayId, streamId, sequence, false, true, 0, 0, "该死信已经回放过，不会重复归档", planned.Notes);
            }

            foreach (var cursor in planned.Cursors)
            {
                var row = await runtime.GatewayArchiveCursors.FindAsync([batch.GatewayId, batch.StreamId, cursor.TagId], ct);
                if (row is null)
                    runtime.GatewayArchiveCursors.Add(new GatewayArchiveCursor
                    {
                        GatewayId = batch.GatewayId, StreamId = batch.StreamId, TagId = cursor.TagId,
                        Ts = cursor.Ts, Value = cursor.Value, Quality = cursor.Quality, Text = cursor.Text
                    });
                else if (cursor.Ts >= row.Ts)
                {
                    row.Ts = cursor.Ts; row.Value = cursor.Value; row.Quality = cursor.Quality; row.Text = cursor.Text;
                }
            }

            var stamp = $"{ReplayedPrefix}{userName} {DateTimeOffset.FromUnixTimeMilliseconds(Now):yyyy-MM-dd HH:mm:ss} UTC，写入 {planned.History.Count} 条、跳过 {planned.SkipCount} 条";
            batch.ProcessingError = Truncate(stamp, 500);
            runtime.OperationLogs.Add(new OperationLog
            {
                UserId = userId, UserName = userName, Ts = Now,
                Action = $"回放网关 #{gatewayId} 死信：数据流 {streamId:N} 序号 {sequence}",
                Result = "成功", TargetType = "GatewayInbox", TargetId = $"{streamId:N}/{sequence}",
                Details = Truncate($"写入 {planned.History.Count} 条，跳过 {planned.SkipCount} 条（不回退 ProcessedThrough）", 1000)
            });
            await runtime.SaveChangesAsync(ct);
            await runtimeTx.CommitAsync(ct);
            return new GatewayDeadLetterPreview(gatewayId, streamId, sequence, false, true,
                planned.History.Count, planned.SkipCount, null, planned.Notes);
        }
        finally
        {
            if (apply) _mutations.Release();
        }
    }

    private static bool IsAlreadyReplayed(string? error) =>
        error is not null && error.StartsWith(ReplayedPrefix, StringComparison.Ordinal);

    private sealed record DeadLetterPlan(List<HistoryValue> History, List<PlannedCursor> Cursors, int SkipCount, string? Error, List<string> Notes);

    private async Task<DeadLetterPlan> PlanDeadLetterAsync(GatewayInboxBatch batch, CancellationToken ct)
    {
        var notes = new List<string>();
        if (TokenHash(batch.PayloadJson) != batch.PayloadHash)
            return new([], [], 0, "载荷校验失败，拒绝回放", ["PayloadHash 与内容不一致"]);

        GatewayTelemetry? packet;
        try
        {
            packet = JsonSerializer.Deserialize<GatewayTelemetry>(batch.PayloadJson, GatewayProtocol.Json);
        }
        catch (Exception ex)
        {
            return new([], [], 0, $"载荷无法解析：{ex.Message}", notes);
        }
        if (packet is null) return new([], [], 0, "载荷为空，拒绝回放", notes);

        await using var db = await OpenAsync(ct);
        var revision = await db.GatewayConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GatewayId == batch.GatewayId && x.Revision == batch.Revision, ct);
        if (revision is null)
            return new([], [], 0, $"找不到该批次记录的配置修订 {batch.Revision}，拒绝回放", notes);

        GatewayConfiguration metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<GatewayConfiguration>(revision.SnapshotJson, GatewayProtocol.Json)
                       ?? throw new InvalidDataException("配置快照为空");
        }
        catch (Exception ex)
        {
            return new([], [], 0, $"配置快照无法解析：{ex.Message}", notes);
        }

        await using var runtime = await OpenRuntimeAsync(ct);
        var tags = metadata.Tags.ToDictionary(x => x.Id);
        var stored = await runtime.GatewayArchiveCursors
            .Where(x => x.GatewayId == batch.GatewayId && x.StreamId == batch.StreamId)
            .ToDictionaryAsync(x => x.TagId, ct);
        var working = stored.ToDictionary(x => x.Key, x => new ArchiveState(x.Value.Ts, x.Value.Value, x.Value.Quality, x.Value.Text));
        var history = new List<HistoryValue>();
        var planned = new Dictionary<int, PlannedCursor>();
        var skipped = 0;
        foreach (var value in packet.Values)
        {
            if (!tags.TryGetValue(value.TagId, out var tag))
            {
                skipped++;
                notes.Add($"测点 #{value.TagId} 不在配置修订 {batch.Revision} 中，已跳过");
                continue;
            }
            working.TryGetValue(value.TagId, out var cursor);
            if (tag.ArchiveMode == ArchiveMode.None)
            {
                skipped++;
                continue;
            }
            if (value.Ts > Now + 1000)
            {
                skipped++;
                notes.Add($"测点 #{value.TagId} 时标在未来，已跳过");
                continue;
            }
            if (cursor is not null && value.Ts == cursor.Ts)
            {
                skipped++;
                continue;
            }
            if (cursor is not null && value.Ts > cursor.Ts && !ArchivePolicy.ShouldStore(tag, value, cursor, value.Ts))
            {
                skipped++;
                continue;
            }
            history.Add(new(value.TagId, value.Ts, value.Value, value.Quality, value.Text));
            if (cursor is null || value.Ts >= cursor.Ts)
            {
                working[value.TagId] = new ArchiveState(value.Ts, value.Value, value.Quality, value.Text);
                planned[value.TagId] = new PlannedCursor(value.TagId, value.Ts, value.Value, value.Quality, value.Text);
            }
        }

        notes.Add("回放按归档游标跳过已写入的同一时标，不回退 ProcessedThrough，也不重新进入收件箱。");
        return new(history, planned.Values.ToList(), skipped, null, notes);
    }
}
