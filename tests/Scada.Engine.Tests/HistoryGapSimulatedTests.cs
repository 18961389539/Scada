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
}
