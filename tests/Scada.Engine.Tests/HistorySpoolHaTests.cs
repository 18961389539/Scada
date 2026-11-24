using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public sealed class HistorySpoolHaTests
{
    [Fact]
    public async Task DescribePendingAndMergeFromDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "spool-ha-" + Guid.NewGuid().ToString("N"));
        try
        {
            var primary = Path.Combine(root, "primary");
            var peer = Path.Combine(root, "peer");
            Directory.CreateDirectory(primary);
            Directory.CreateDirectory(peer);
            var source = new HistorySpool(peer);
            await source.AppendAsync([new HistoryValue(1, 1_000, 1.5, TagQuality.Good), new HistoryValue(1, 2_000, 2.5, TagQuality.Good)], default);
            var pending = source.DescribePending();
            Assert.True(pending.ApproxCount >= 2);
            Assert.Equal(1_000, pending.From);
            Assert.Equal(2_000, pending.To);
            Assert.True(pending.Bytes > 0);

            var dest = new HistorySpool(primary);
            var imported = await dest.MergeFromDirectoryAsync(Path.Combine(peer, "_spool"), default);
            Assert.True(imported >= 1);
            var merged = dest.DescribePending();
            Assert.True(merged.ApproxCount >= 2);
            Assert.Equal(1_000, merged.From);
            Assert.Equal(2_000, merged.To);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
