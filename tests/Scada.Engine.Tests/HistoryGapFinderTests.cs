using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public class HistoryGapFinderTests
{
    [Fact]
    public void MarksBadQualityAndMissingHoles()
    {
        var series = new HistorySeries
        {
            TagId = 9,
            Points =
            [
                new HistoryPoint(1_000, 1, TagQuality.Good),
                new HistoryPoint(2_000, 1, TagQuality.Bad),
                new HistoryPoint(3_000, 1, TagQuality.Bad),
                new HistoryPoint(20_000, 2, TagQuality.Good)
            ]
        };
        var gaps = HistoryGapFinder.FromSeries([series], 0, 21_000, HistoryResolution.Raw);
        Assert.Contains(gaps, g => g.Kind == HistoryGapKind.BadQuality && g.TagId == 9 && g.From == 2_000);
        Assert.Contains(gaps, g => g.Kind == HistoryGapKind.Missing && g.From == 3_000 && g.To == 20_000);
    }

    [Fact]
    public void IncludesOverlappingLossWindows()
    {
        var series = new HistorySeries { TagId = 1, Points = [new HistoryPoint(5_000, 1, TagQuality.Good), new HistoryPoint(6_000, 1, TagQuality.Good)] };
        var loss = new[] { new HistoryGap(null, 4_000, 8_000, HistoryGapKind.Rejected, "bus") };
        var gaps = HistoryGapFinder.FromSeries([series], 0, 10_000, HistoryResolution.Raw, loss);
        Assert.Contains(gaps, g => g.Kind == HistoryGapKind.Rejected && g.From == 4_000 && g.To == 8_000);
    }

    [Fact]
    public void EmptySeriesIsAMissingGap()
    {
        var gaps = HistoryGapFinder.FromSeries([new HistorySeries { TagId = 4 }], 100, 200, HistoryResolution.Raw);
        var missing = Assert.Single(gaps);
        Assert.Equal(HistoryGapKind.Missing, missing.Kind);
        Assert.Equal(4, missing.TagId);
    }
}
