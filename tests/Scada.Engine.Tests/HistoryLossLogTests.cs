using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public class HistoryLossLogTests
{
    [Fact]
    public void RejectedWindowsSurviveReloadAndDoNotDuplicate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scada-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, HistoryLossLog.FileName);
        try
        {
            var log = new HistoryLossLog(path, () => 0);
            log.Record(HistoryGapKind.Rejected, 1_000, 2_000, 7, "bus full");
            log.Record(HistoryGapKind.Rejected, 1_500, 2_500, 7, "bus full again");
            log.Record(HistoryGapKind.SpoolDropped, 3_000, 4_000, null, "oldest dropped");
            log.Record(HistoryGapKind.Missing, 8_000, 9_000, 7, "not persisted");
            Assert.Equal(2, log.Snapshot().Count(x => x.Kind is HistoryGapKind.Rejected or HistoryGapKind.SpoolDropped));
            var reloaded = new HistoryLossLog(path, () => 0);
            var rejected = reloaded.Overlapping(0, 10_000, 7).Single(x => x.Kind == HistoryGapKind.Rejected);
            Assert.Equal(1_000, rejected.From);
            Assert.Equal(2_500, rejected.To);
            Assert.Contains(reloaded.Snapshot(), x => x.Kind == HistoryGapKind.SpoolDropped);
            Assert.DoesNotContain(reloaded.Snapshot(), x => x.Kind == HistoryGapKind.Missing);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void RetentionCutoffDropsExpiredWindows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scada-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, HistoryLossLog.FileName);
        try
        {
            var log = new HistoryLossLog(path, () => 0);
            log.Record(HistoryGapKind.Rejected, 10, 20);
            var trimmed = new HistoryLossLog(path, () => 50);
            Assert.Empty(trimmed.Snapshot());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void MergeFromFileUnionsOverlappingWindows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scada-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var primary = new HistoryLossLog(Path.Combine(dir, "primary.jsonl"), null);
            primary.Record(HistoryGapKind.Rejected, 10, 20, detail: "a");
            var peerPath = Path.Combine(dir, "peer.jsonl");
            var peer = new HistoryLossLog(peerPath, null);
            peer.Record(HistoryGapKind.Rejected, 15, 30, detail: "b");
            peer.Record(HistoryGapKind.SpoolDropped, 100, 120, detail: "spool");
            var merged = primary.MergeFromFile(peerPath);
            Assert.True(merged >= 2);
            var windows = primary.Snapshot();
            Assert.Contains(windows, x => x.Kind == HistoryGapKind.Rejected && x.From == 10 && x.To == 30);
            Assert.Contains(windows, x => x.Kind == HistoryGapKind.SpoolDropped);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
