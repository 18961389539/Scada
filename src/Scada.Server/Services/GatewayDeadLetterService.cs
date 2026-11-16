using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using static Scada.Server.Services.GatewayRules;

namespace Scada.Server.Services;

/// <summary>
/// 网关死信的只读查询。分页游标按「失败时间 → 序号 → 数据流文本」排序：
/// 同一时刻、同一序号的其它数据流会占满一窗，真正更早的序号需要再取一次。
/// </summary>
public sealed class GatewayDeadLetterService(
    IDbContextFactory<ConfigDbContext> factory,
    IDbContextFactory<RuntimeDbContext> runtimeFactory)
{
    public async Task<(IReadOnlyList<GatewayDeadLetterInfo> Items, bool HasMore)> ListAsync(int gatewayId, CancellationToken ct,
        long? beforeTs = null, long? beforeSequence = null, Guid? beforeStream = null, int? take = null)
    {
        const int maxPage = 200;
        var pageSize = Math.Clamp(take ?? maxPage, 1, maxPage);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Gateways.AnyAsync(x => x.Id == gatewayId, ct)) throw new GatewayRequestException(404, "网关不存在");
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var query = runtime.GatewayInbox.AsNoTracking().Where(x => x.GatewayId == gatewayId && x.DeadLetteredTs != null);
        List<GatewayInboxBatch> rows;
        if (beforeTs is long ts && beforeSequence is long seq && beforeStream is Guid stream)
        {
            rows = await query.Where(x => x.DeadLetteredTs < ts || (x.DeadLetteredTs == ts && x.Sequence < seq) || (x.DeadLetteredTs == ts && x.Sequence == seq))
                .OrderByDescending(x => x.DeadLetteredTs).ThenByDescending(x => x.Sequence).ThenByDescending(x => x.StreamId)
                .Take(pageSize + 32).ToListAsync(ct);
            var older = rows.Where(x => IsOlderLetter(x, ts, seq, stream)).ToList();
            if (older.Count <= pageSize && rows.Count == pageSize + 32)
            {
                var rest = await query.Where(x => x.DeadLetteredTs < ts || (x.DeadLetteredTs == ts && x.Sequence < seq))
                    .OrderByDescending(x => x.DeadLetteredTs).ThenByDescending(x => x.Sequence).ThenByDescending(x => x.StreamId)
                    .Take(pageSize + 1).ToListAsync(ct);
                older = older.Concat(rest).ToList();
            }
            rows = older;
        }
        else
        {
            rows = await query.OrderByDescending(x => x.DeadLetteredTs).ThenByDescending(x => x.Sequence).ThenByDescending(x => x.StreamId)
                .Take(pageSize + 1).ToListAsync(ct);
        }
        rows = rows.OrderByDescending(x => x.DeadLetteredTs).ThenByDescending(x => x.Sequence)
            .ThenByDescending(x => x.StreamId.ToString("N"), StringComparer.Ordinal).Take(pageSize + 1).ToList();
        var more = rows.Count > pageSize;
        if (more) rows = rows.Take(pageSize).ToList();
        return (rows.Select(x => Map(x, includePayload: false)).ToArray(), more);
    }

    public async Task<GatewayDeadLetterInfo?> GetAsync(int gatewayId, Guid streamId, long sequence, CancellationToken ct)
    {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var row = await runtime.GatewayInbox.AsNoTracking().FirstOrDefaultAsync(x => x.GatewayId == gatewayId && x.StreamId == streamId
            && x.Sequence == sequence && x.DeadLetteredTs != null, ct);
        return row is null ? null : Map(row, includePayload: true);
    }

    private static GatewayDeadLetterInfo Map(GatewayInboxBatch batch, bool includePayload) => new(
        batch.GatewayId, batch.StreamId, batch.Sequence, batch.DeviceId, batch.Revision, batch.ReceivedTs,
        batch.DeadLetteredTs ?? 0, batch.Attempts, batch.ProcessingError, batch.PayloadBytes, includePayload ? batch.PayloadJson : null);
}