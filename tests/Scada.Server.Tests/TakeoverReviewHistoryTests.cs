using Microsoft.Data.Sqlite;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Engine.History;
using Scada.Server.Ha;

namespace Scada.Server.Tests;

public sealed class TakeoverReviewHistoryTests
{
    [Fact]
    public async Task SnapshotListsUnreplayedSpoolAndLossWindows()
    {
        await using var connection = TestRuntime.Open();
        var runtime = TestRuntime.Factory(connection);
        var root = Path.Combine(Path.GetTempPath(), "takeover-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var spool = new HistorySpool(root);
            await spool.AppendAsync(
            [
                new HistoryValue(1, 1_000, 1.5, TagQuality.Good),
                new HistoryValue(1, 2_000, 2.5, TagQuality.Good)
            ], default);
            var loss = new HistoryLossLog();
            loss.Record(HistoryGapKind.Rejected, 1_000, 3_000, detail: "test");

            var service = new TakeoverReviewService(runtime, new StandaloneNodeRole(), spool, loss);
            var review = await service.SnapshotAsync();
            Assert.NotNull(review.UnreplayedSpool);
            Assert.True(review.UnreplayedSpool!.ApproxCount >= 2);
            Assert.Equal(1_000, review.UnreplayedSpool.From);
            Assert.Equal(2_000, review.UnreplayedSpool.To);
            Assert.Equal(1, review.LossWindows);
            Assert.Equal(1_000, review.LossOldestFrom);
            Assert.Contains(review.Items, x => x.Kind == "history-spool");
            Assert.Contains(review.Items, x => x.Kind == "history-loss");
            Assert.Contains(service.HealthWarnings(), x => x.Code == "ha_takeover_review" && x.Message.Contains("暂存") && x.Message.Contains("缺口"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task SnapshotWithoutPendingHistoryIsEmpty()
    {
        await using var connection = TestRuntime.Open();
        var runtime = TestRuntime.Factory(connection);
        var review = await new TakeoverReviewService(runtime, new StandaloneNodeRole()).SnapshotAsync();
        Assert.Equal(0, review.UnknownCommands);
        Assert.Equal(0, review.LossWindows);
        Assert.True(review.UnreplayedSpool is null || review.UnreplayedSpool.ApproxCount == 0);
        Assert.Empty(review.Items);
        Assert.Empty(new TakeoverReviewService(runtime, new StandaloneNodeRole()).HealthWarnings());
    }
}
