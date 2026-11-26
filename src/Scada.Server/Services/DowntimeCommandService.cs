using Microsoft.EntityFrameworkCore;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Oee;

namespace Scada.Server.Services;

/// <summary>
/// 停机确认的唯一落地点：REST 确认入口与 MCP confirm_downtime 共用确认、可选归类与 OEE 重建。
/// 授权与审计行形态由各入口负责。
/// </summary>
public enum DowntimeCommandError
{
    None,
    NotFound,
    LineMissing,
    ReasonMissing,
    ReasonRequired
}

public sealed class DowntimeCommandResult
{
    public bool Success { get; init; }
    public bool Unchanged { get; init; }
    public DowntimeCommandError Error { get; init; }
    public string? Message { get; init; }
    public DowntimeEvent? Event { get; init; }
    public ProductionLine? Line { get; init; }

    public static DowntimeCommandResult Failed(DowntimeCommandError error, string message, DowntimeEvent? row = null, ProductionLine? line = null) =>
        new() { Error = error, Message = message, Event = row, Line = line };

    public static DowntimeCommandResult Ok(DowntimeEvent row, ProductionLine? line, bool unchanged, string message) =>
        new() { Success = true, Unchanged = unchanged, Event = row, Line = line, Message = message };
}

public sealed class DowntimeCommandService
{
    public async Task<DowntimeCommandResult> ConfirmAsync(
        ConfigDbContext db, RuntimeDbContext runtime, OeeRollupService rollups, long downtimeId,
        int? reasonId, string? note, bool applyNote, string actorName,
        Action<DowntimeEvent, ProductionLine, bool, int?, string?, long> writeAudit,
        CancellationToken cancellationToken)
    {
        var row = await runtime.DowntimeEvents.SingleOrDefaultAsync(x => x.Id == downtimeId, cancellationToken);
        if (row is null) return DowntimeCommandResult.Failed(DowntimeCommandError.NotFound, "Downtime event does not exist.");
        var line = await db.ProductionLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.LineId, cancellationToken);
        if (line is null)
            return DowntimeCommandResult.Failed(DowntimeCommandError.LineMissing, "The production line of this downtime event no longer exists.", row);
        if (reasonId is int classify && !await db.DowntimeReasons.AnyAsync(x => x.Id == classify, cancellationToken))
            return DowntimeCommandResult.Failed(DowntimeCommandError.ReasonMissing, "Downtime reason does not exist.", row, line);

        var reasonChanged = reasonId is int target && target != row.ReasonId;
        var noteChanged = applyNote && note != row.Note;
        if (row.Confirmed && !reasonChanged && !noteChanged)
            return DowntimeCommandResult.Ok(row, line, unchanged: true, "Downtime event was already confirmed; nothing changed.");
        var finalReason = reasonId ?? row.ReasonId;
        if (finalReason is not int confirmedReason || confirmedReason <= 0)
            return DowntimeCommandResult.Failed(DowntimeCommandError.ReasonRequired, "确认停机必须填写停机原因", row, line);
        if (!await db.DowntimeReasons.AnyAsync(x => x.Id == confirmedReason, cancellationToken))
            return DowntimeCommandResult.Failed(DowntimeCommandError.ReasonMissing, "Downtime reason does not exist.", row, line);

        var now = ScadaTime.NowMs();
        var already = row.Confirmed;
        var previousReason = row.ReasonId;
        var previousNote = row.Note;
        row.Confirmed = true;
        if (reasonId is int classified) row.ReasonId = classified;
        if (applyNote) row.Note = note;
        row.UpdatedBy = actorName;
        row.UpdatedTs = now;
        writeAudit(row, line, already, previousReason, previousNote, now);
        await db.SaveChangesAsync(cancellationToken);
        await runtime.SaveChangesAsync(cancellationToken);
        await rollups.RequestRebuildAsync(row.StartTs, cancellationToken);
        return DowntimeCommandResult.Ok(row, line, unchanged: false, "Downtime event confirmed.");
    }
}
