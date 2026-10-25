using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Server.Tests;

public sealed class HistoryDownsamplerTests
{
    [Fact]
    public void CapsTotalPointsAndKeepsSeriesEndpoints()
    {
        var series = Enumerable.Range(1, 8).Select(tagId => new HistorySeries
        {
            TagId = tagId,
            Points = Enumerable.Range(0, 5000)
                .Select(i => new HistoryPoint(i * 1000L, i % 100, TagQuality.Good))
                .ToList()
        }).ToList();

        var (limited, downsampled) = HistoryDownsampler.Apply(series, 1000);

        Assert.True(downsampled);
        Assert.True(limited.Sum(x => x.Points.Count) <= 1000);
        foreach (var (original, kept) in series.Zip(limited))
        {
            Assert.Equal(original.Points[0].Ts, kept.Points[0].Ts);
            Assert.Equal(original.Points[^1].Ts, kept.Points[^1].Ts);
        }
    }

    [Fact]
    public void KeepsSpikesWhenDownsampling()
    {
        var points = Enumerable.Range(0, 10_000)
            .Select(i => new HistoryPoint(i * 1000L, 1.0, TagQuality.Good))
            .ToList();
        points[5000] = new HistoryPoint(points[5000].Ts, 999, TagQuality.Good);

        var (limited, downsampled) = HistoryDownsampler.Apply(
            [new HistorySeries { TagId = 1, Points = points }], 200);

        Assert.True(downsampled);
        Assert.True(limited[0].Points.Count <= 200);
        Assert.Contains(limited[0].Points, p => p.Value == 999);
    }

    [Fact]
    public void LeavesSeriesUnderBudgetUntouched()
    {
        var series = new HistorySeries
        {
            TagId = 1,
            Points = Enumerable.Range(0, 50)
                .Select(i => new HistoryPoint(i * 1000L, i, TagQuality.Good))
                .ToList()
        };

        var (limited, downsampled) = HistoryDownsampler.Apply([series], 100);

        Assert.False(downsampled);
        Assert.Same(series, limited[0]);
    }
}
