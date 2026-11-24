using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Engine.Health;
using Scada.Engine.Maintenance;
using Scada.Server.Services;

namespace Scada.Server.Ha;

public sealed record TakeoverReviewItem(string Kind, string Id, string Name, string Message, long? Ts);

public sealed record HealthTakeoverReview(int UnknownCommands, int InterruptedSequences, int InterruptedRecipes, IReadOnlyList<TakeoverReviewItem> Items);

/// <summary>
/// 接管或重启后留下的结果未知 / 中断项，供健康页核对现场。不自动续跑顺控或配方。
/// </summary>
public sealed class TakeoverReviewService(IDbContextFactory<RuntimeDbContext> runtime, INodeRole role) : IHealthContributor
{
    public const int Limit = 50;

    public async Task<HealthTakeoverReview> SnapshotAsync(CancellationToken ct = default)
    {
        if (!role.IsActive) return new(0, 0, 0, []);
        await using var db = await runtime.CreateDbContextAsync(ct);
        var items = new List<TakeoverReviewItem>();

        var commands = await db.LocalControlCommands.AsNoTracking()
            .Where(x => x.State == "Unknown")
            .OrderByDescending(x => x.CreatedTs)
            .Take(Limit)
            .ToListAsync(ct);
        items.AddRange(commands.Select(x => new TakeoverReviewItem("command", x.Id.ToString(), $"设备 {x.DeviceId} 测点 {x.TagId}", x.ResultJson ?? LocalCommandLedger.UnknownMessage, x.CompletedTs ?? x.CreatedTs)));

        var sequences = await db.ControlSequenceRuns.AsNoTracking()
            .Where(x => (x.Status == ControlRunStatus.Failed || x.Status == ControlRunStatus.Aborted) && x.Message != null && (x.Message.Contains("结果未知") || x.Message.Contains("顺控中断")))
            .OrderByDescending(x => x.EndedTs ?? x.StartedTs)
            .Take(Limit)
            .ToListAsync(ct);
        items.AddRange(sequences.Select(x => new TakeoverReviewItem("sequence", x.Id.ToString(), x.SequenceName, x.Message ?? "", x.EndedTs ?? x.StartedTs)));

        var recipes = await db.RecipeDownloads.AsNoTracking()
            .Where(x => (x.Status == RecipeDownloadStatus.Failed || x.Status == RecipeDownloadStatus.PartiallyApplied || x.Status == RecipeDownloadStatus.Aborted)
                        && x.Message != null && (x.Message.Contains("结果未知") || x.Message.Contains("配方下发中断")))
            .OrderByDescending(x => x.EndedTs ?? x.StartedTs ?? x.CreatedTs)
            .Take(Limit)
            .ToListAsync(ct);
        items.AddRange(recipes.Select(x => new TakeoverReviewItem("recipe", x.Id.ToString(), x.RecipeName, x.Message ?? "", x.EndedTs ?? x.StartedTs ?? x.CreatedTs)));

        return new(commands.Count, sequences.Count, recipes.Count, items.OrderByDescending(x => x.Ts ?? 0).Take(Limit).ToList());
    }

    public IEnumerable<SystemWarning> HealthWarnings()
    {
        HealthTakeoverReview review;
        try { review = SnapshotAsync().GetAwaiter().GetResult(); }
        catch { yield break; }
        if (review.UnknownCommands + review.InterruptedSequences + review.InterruptedRecipes == 0) yield break;
        yield return new("ha_takeover_review",
            $"有 {review.UnknownCommands} 条本机控制命令结果未知、{review.InterruptedSequences} 条顺控中断、{review.InterruptedRecipes} 条配方下发中断，不会自动重发或续跑，请核对现场后再用新命令执行。");
    }
}
