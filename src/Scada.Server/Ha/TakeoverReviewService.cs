using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Engine.Health;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Server.Services;

namespace Scada.Server.Ha;

public sealed record TakeoverReviewItem(string Kind, string Id, string Name, string Message, long? Ts);

public sealed record HealthTakeoverReview(
    int UnknownCommands,
    int InterruptedSequences,
    int InterruptedRecipes,
    IReadOnlyList<TakeoverReviewItem> Items,
    HistorySpoolPending? UnreplayedSpool = null,
    int LossWindows = 0,
    long? LossOldestFrom = null);

/// <summary>
/// 接管或重启后留下的结果未知 / 中断项，供健康页核对现场。不自动续跑顺控或配方。
/// 未回放的历史暂存与缺口日志一并列出，便于决定要不要从备份补；不做持续双写。
/// </summary>
public sealed class TakeoverReviewService(
    IDbContextFactory<RuntimeDbContext> runtime,
    INodeRole role,
    HistorySpool? spool = null,
    HistoryLossLog? historyLoss = null) : IHealthContributor
{
    public const int Limit = 50;

    public async Task<HealthTakeoverReview> SnapshotAsync(CancellationToken ct = default)
    {
        var items = new List<TakeoverReviewItem>();
        var commands = 0;
        var sequences = 0;
        var recipes = 0;

        if (role.IsActive)
        {
            await using var db = await runtime.CreateDbContextAsync(ct);

            var commandRows = await db.LocalControlCommands.AsNoTracking()
                .Where(x => x.State == "Unknown")
                .OrderByDescending(x => x.CreatedTs)
                .Take(Limit)
                .ToListAsync(ct);
            commands = commandRows.Count;
            items.AddRange(commandRows.Select(x => new TakeoverReviewItem("command", x.Id.ToString(), $"设备 {x.DeviceId} 测点 {x.TagId}", x.ResultJson ?? LocalCommandLedger.UnknownMessage, x.CompletedTs ?? x.CreatedTs)));

            var sequenceRows = await db.ControlSequenceRuns.AsNoTracking()
                .Where(x => (x.Status == ControlRunStatus.Failed || x.Status == ControlRunStatus.Aborted) && x.Message != null && (x.Message.Contains("结果未知") || x.Message.Contains("顺控中断")))
                .OrderByDescending(x => x.EndedTs ?? x.StartedTs)
                .Take(Limit)
                .ToListAsync(ct);
            sequences = sequenceRows.Count;
            items.AddRange(sequenceRows.Select(x => new TakeoverReviewItem("sequence", x.Id.ToString(), x.SequenceName, x.Message ?? "", x.EndedTs ?? x.StartedTs)));

            var recipeRows = await db.RecipeDownloads.AsNoTracking()
                .Where(x => (x.Status == RecipeDownloadStatus.Failed || x.Status == RecipeDownloadStatus.PartiallyApplied || x.Status == RecipeDownloadStatus.Aborted)
                            && x.Message != null && (x.Message.Contains("结果未知") || x.Message.Contains("配方下发中断")))
                .OrderByDescending(x => x.EndedTs ?? x.StartedTs ?? x.CreatedTs)
                .Take(Limit)
                .ToListAsync(ct);
            recipes = recipeRows.Count;
            items.AddRange(recipeRows.Select(x => new TakeoverReviewItem("recipe", x.Id.ToString(), x.RecipeName, x.Message ?? "", x.EndedTs ?? x.StartedTs ?? x.CreatedTs)));
        }

        HistorySpoolPending? pending = null;
        var lossWindows = 0;
        long? lossOldest = null;
        if (spool is not null)
        {
            pending = spool.DescribePending();
            if (pending.ApproxCount > 0 || pending.Bytes > 0)
            {
                var window = pending.From is long from && pending.To is long to
                    ? $"时间窗 {from}–{to}"
                    : "时间窗未知";
                items.Add(new("history-spool", "pending", "未回放历史暂存",
                    $"约 {pending.ApproxCount} 条，{pending.Segments} 个分段，{pending.Bytes} 字节，{window}。最终同步超时与失败语义不变，不会持续双写。",
                    pending.From));
            }
        }

        if (historyLoss is not null)
        {
            var status = historyLoss.Status();
            lossWindows = status.Windows;
            lossOldest = status.OldestFrom;
            if (status.Windows > 0)
            {
                items.Add(new("history-loss", "log", "历史缺口日志",
                    $"{status.Windows} 个窗口" + (status.OldestFrom is long oldest ? $"，最早 {oldest}" : "") + "。" + status.Note,
                    status.OldestFrom));
            }
        }

        return new(commands, sequences, recipes, items.OrderByDescending(x => x.Ts ?? 0).Take(Limit).ToList(), pending, lossWindows, lossOldest);
    }

    public IEnumerable<SystemWarning> HealthWarnings()
    {
        HealthTakeoverReview review;
        try { review = SnapshotAsync().GetAwaiter().GetResult(); }
        catch { yield break; }
        var spoolPending = review.UnreplayedSpool is { } queued && (queued.ApproxCount > 0 || queued.Bytes > 0);
        if (review.UnknownCommands + review.InterruptedSequences + review.InterruptedRecipes == 0 && !spoolPending && review.LossWindows == 0)
            yield break;
        var parts = new List<string>
        {
            $"有 {review.UnknownCommands} 条本机控制命令结果未知、{review.InterruptedSequences} 条顺控中断、{review.InterruptedRecipes} 条配方下发中断"
        };
        if (spoolPending)
            parts.Add($"未回放历史暂存约 {review.UnreplayedSpool!.ApproxCount} 条");
        if (review.LossWindows > 0)
            parts.Add($"历史缺口 {review.LossWindows} 个窗口");
        yield return new("ha_takeover_review",
            string.Join("，", parts) + "，不会自动重发或续跑，请核对现场后再用新命令执行。");
    }
}
