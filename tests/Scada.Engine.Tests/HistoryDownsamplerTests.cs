using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public class HistoryDownsamplerTests
{
    private static HistorySeries Series(int tagId, params (long Ts, double Value)[] points) => new()
    {
        TagId = tagId,
        Points = points.Select(p => new HistoryPoint(p.Ts, p.Value, TagQuality.Good)).ToList()
    };

    private static async IAsyncEnumerable<HistoryValue> Stream(IEnumerable<HistoryValue> values)
    {
        foreach (var value in values) { await Task.Yield(); yield return value; }
    }

    [Fact]
    public void ApplyKeepsSeriesUntouchedWhenEveryPointFitsTheBudget()
    {
        var series = new[] { Series(1, (1, 10), (2, 20)), Series(2, (1, 30)) };

        var (result, downsampled) = HistoryDownsampler.Apply(series, maxPoints: 100);

        Assert.False(downsampled);
        Assert.Same(series, result);
    }

    [Fact]
    public void ApplyIgnoresNonPositiveBudget()
    {
        var series = new[] { Series(1, (1, 10), (2, 20)) };

        var (result, downsampled) = HistoryDownsampler.Apply(series, maxPoints: 0);

        Assert.False(downsampled);
        Assert.Same(series, result);
    }

    [Fact]
    public void ApplyKeepsFirstLastAndExtremesInsideEachBucket()
    {
        var points = Enumerable.Range(0, 200).Select(index => ((long)index, 50d + Math.Sin(index) * 10)).ToArray();
        points[37] = (37, -100);
        points[121] = (121, 200);
        var series = new[] { Series(1, points) };

        var (result, downsampled) = HistoryDownsampler.Apply(series, maxPoints: 10);

        Assert.True(downsampled);
        var kept = result.Single().Points;
        Assert.Equal(points[0].Item1, kept[0].Ts);
        Assert.Equal(points[^1].Item1, kept[^1].Ts);
        Assert.Contains(kept, point => point.Ts == 37 && point.Value == -100);
        Assert.Contains(kept, point => point.Ts == 121 && point.Value == 200);
        Assert.True(kept.Count <= 10);
    }

    [Fact]
    public void ApplyDegradesToFirstAndLastWhenTargetIsTwo()
    {
        var series = new[] { Series(1, (1, 1), (2, 2), (3, 3), (4, 4)) };

        var (result, downsampled) = HistoryDownsampler.Apply(series, maxPoints: 2);

        Assert.True(downsampled);
        Assert.Equal(new long[] { 1, 4 }, result.Single().Points.Select(point => point.Ts));
    }

    [Fact]
    public async Task ApplyStreamingFiltersTagsAndRangeWithoutDownsampling()
    {
        var values = new[]
        {
            new HistoryValue(1, 10, 1, TagQuality.Good),
            new HistoryValue(2, 10, 2, TagQuality.Good),
            new HistoryValue(1, 20, 3, TagQuality.Good),
            new HistoryValue(1, 5, 4, TagQuality.Good),
            new HistoryValue(1, 30, 5, TagQuality.Good)
        };

        var (series, downsampled) = await HistoryDownsampler.ApplyStreamingAsync(Stream(values), [1], 10, 20, maxPoints: 100);

        Assert.False(downsampled);
        var single = Assert.Single(series);
        Assert.Equal(1, single.TagId);
        Assert.Equal(new long[] { 10, 20 }, single.Points.Select(point => point.Ts));
    }

    [Fact]
    public async Task ApplyStreamingKeepsTextAndBadQualityPointsWhenDownsampled()
    {
        var values = new List<HistoryValue>();
        for (var index = 0; index < 40; index++)
            values.Add(new HistoryValue(1, index, index, index == 3 ? TagQuality.Bad : TagQuality.Good));
        values.Add(new HistoryValue(1, 40, 0, TagQuality.Good, "运行"));
        values.Add(new HistoryValue(1, 41, 0, TagQuality.Good, "停止"));

        var (series, downsampled) = await HistoryDownsampler.ApplyStreamingAsync(Stream(values), [1], 0, 41, maxPoints: 6);

        Assert.True(downsampled);
        var kept = series.Single().Points;
        // 每个时间桶只保留最后一个文本点：桶内更早的文本会被覆盖。
        Assert.Contains(kept, point => point.Text == "停止");
        Assert.Contains(kept, point => point.Quality == TagQuality.Bad);
        Assert.Equal(new long[] { 0, 41 }, new[] { kept[0].Ts, kept[^1].Ts });
        Assert.True(kept.Count <= 6);
    }

    [Fact]
    public async Task ApplyStreamingRejectsBudgetTooSmallForEveryTag()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await HistoryDownsampler.ApplyStreamingAsync(Stream([new HistoryValue(1, 1, 1, TagQuality.Good)]), [1, 2], 0, 10, maxPoints: 2));
    }

    [Fact]
    public async Task ApplyStreamingReturnsEmptyForMissingTagsOrInvalidRange()
    {
        var values = new[] { new HistoryValue(1, 10, 1, TagQuality.Good) };

        var (noTags, firstDownsampled) = await HistoryDownsampler.ApplyStreamingAsync(Stream(values), [], 0, 20, maxPoints: 100);
        var (invalidRange, secondDownsampled) = await HistoryDownsampler.ApplyStreamingAsync(Stream(values), [1], 20, 10, maxPoints: 100);

        Assert.Empty(noTags);
        Assert.False(firstDownsampled);
        Assert.Empty(invalidRange);
        Assert.False(secondDownsampled);
    }
}
