using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public class HistoryGapSimulatedTests
{
    [Fact]
    public void SimulatedGoodPoints_AreReportedAsSimulatedGaps()
    {
        var series = new[]
        {
            new HistorySeries
            {
                TagId = 7,
                Points =
                [
                    new HistoryPoint(1_000, 1, TagQuality.Good),
                    new HistoryPoint(2_000, 2, TagQuality.Good, Simulated: true),
                    new HistoryPoint(3_000, 3, TagQuality.Good, Simulated: true),
                    new HistoryPoint(4_000, 4, TagQuality.Good)
                ]
            }
        };
        var gaps = HistoryGapFinder.FromSeries(series, 1_000, 4_000, HistoryResolution.Raw);
        Assert.Contains(gaps, g => g.Kind == HistoryGapKind.Simulated && g.TagId == 7 && g.From == 2_000 && g.To == 4_000);
        Assert.DoesNotContain(gaps, g => g.Kind == HistoryGapKind.BadQuality);
    }

    [Fact]
    public void SimulatedOnlyAggregateWindows_AreReportedAsSimulatedGaps()
    {
        var points = new[]
        {
            new AggregatePoint(7, 0, 0, 0, 0, 0, SimulatedCount: 3),
            new AggregatePoint(7, 3_600_000, 10, 10, 10, 5, SimulatedCount: 1)
        };
        var gaps = HistoryGapFinder.FromSimulatedAggregates(points, 0, 7_200_000, HistoryResolution.Hour);
        Assert.Contains(gaps, g => g.Kind == HistoryGapKind.Simulated && g.TagId == 7 && g.From == 0 && g.To == 3_600_000);
        Assert.Contains(gaps, g => g.Kind == HistoryGapKind.Simulated && g.TagId == 7 && g.From == 3_600_000 && g.To == 7_200_000);
    }
}
