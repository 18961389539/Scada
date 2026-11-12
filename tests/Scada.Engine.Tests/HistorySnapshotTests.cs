using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Data.Infrastructure;
using Scada.Engine.History;
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Engine.Tests;

public sealed class HistorySnapshotTests
{
    [Fact]
    public async Task LastBeforeReturnsLatestSamplePerTagAndWalksBackMonthShards()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(new HistoryDbFactory(Options.Create(new ScadaSettings { HistoryRoot = dir.Path })));
        var previousMonth = new DateTimeOffset(2026, 2, 28, 23, 59, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var currentMonth = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var later = currentMonth + 3_600_000;
        await store.WriteRawAsync([
            new HistoryValue(1, previousMonth, 10, TagQuality.Good),
            new HistoryValue(1, currentMonth, 20, TagQuality.Good),
            new HistoryValue(1, later, 30, TagQuality.Good),
            new HistoryValue(2, previousMonth, 5, TagQuality.CommFailure)
        ], default);

        var rows = await store.LastBeforeAsync([1, 2, 3], currentMonth + 1, default);

        Assert.Equal(2, rows.Count);
        var first = Assert.Single(rows, row => row.TagId == 1);
        Assert.Equal(20, first.Value);
        Assert.Equal(currentMonth, first.Ts);
        var second = Assert.Single(rows, row => row.TagId == 2);
        Assert.Equal(5, second.Value);
        Assert.Equal(TagQuality.CommFailure, second.Quality);
        Assert.DoesNotContain(rows, row => row.TagId == 3);
    }
}
