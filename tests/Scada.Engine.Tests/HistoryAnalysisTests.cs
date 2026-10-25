using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public class HistoryAnalysisTests
{
    private static HistorySeries Series(int tagId, params (long Ts, double Value, TagQuality Quality)[] points) =>
        new() { TagId = tagId, Points = points.Select(x => new HistoryPoint(x.Ts, x.Value, x.Quality)).ToList() };

    [Fact]
    public void TimeWeightedAverageIgnoresBadQualityAndMergesIntervals()
    {
        var series = Series(1, (0, 1, TagQuality.Good), (10_000, 3, TagQuality.Bad), (20_000, 5, TagQuality.Good), (30_000, 5, TagQuality.Good));
        var result = Assert.Single(HistoryAnalysis.Analyze([series], 0, 40_000));

        Assert.Equal(4, result.Samples);
        Assert.Equal(3, result.GoodSamples);
        Assert.Equal(30_000, result.GoodMs);
        Assert.Equal(75, result.Coverage);
        Assert.Equal(3.6667, result.TimeWeightedAvg);
        Assert.Equal(1, result.Min);
        Assert.Equal(5, result.Max);
        Assert.Equal(0.0306, result.IntegralHours, 4);   // 值·小时：110000 值·毫秒 / 3.6e6
        Assert.Equal(
            [new HistoryQualityInterval(TagQuality.Good, 0, 10_000), new(TagQuality.Bad, 10_000, 20_000), new(TagQuality.Good, 20_000, 40_000)],
            result.Quality);
        Assert.False(result.QualityTruncated);
    }

    [Fact]
    public void NoGoodSamplesLeavesStatisticsEmptyButKeepsQuality()
    {
        var series = Series(2, (0, 7, TagQuality.Bad), (10_000, 8, TagQuality.CommFailure));
        var result = Assert.Single(HistoryAnalysis.Analyze([series], 0, 20_000));

        Assert.Null(result.TimeWeightedAvg);
        Assert.Null(result.Min);
        Assert.Null(result.Max);
        Assert.Equal(0, result.IntegralHours);
        Assert.Equal(0, result.Coverage);
        Assert.Equal(2, result.Quality.Count);
    }

    [Fact]
    public void EmptySeriesStillReportsSpan()
    {
        var result = Assert.Single(HistoryAnalysis.Analyze([new HistorySeries { TagId = 3 }], 0, 60_000));
        Assert.Equal(0, result.Samples);
        Assert.Equal(60_000, result.SpanMs);
        Assert.Empty(result.Quality);
    }

    [Fact]
    public void UnevenSamplingUsesHoldDuration()
    {
        // 第 1 点保持 50 秒，第 2 点保持 10 秒：均值必须按持续时间加权，而不是按点数。
        var series = Series(4, (0, 0, TagQuality.Good), (50_000, 10, TagQuality.Good));
        var result = Assert.Single(HistoryAnalysis.Analyze([series], 0, 60_000));
        Assert.Equal(1.6667, result.TimeWeightedAvg);
        Assert.Equal(60_000, result.GoodMs);
    }

    [Fact]
    public void QualityIntervalsAreCapped()
    {
        var points = Enumerable.Range(0, 30).Select(i => ((long)i * 1000, 1d, i % 2 == 0 ? TagQuality.Good : TagQuality.Bad)).ToArray();
        var result = Assert.Single(HistoryAnalysis.Analyze([Series(5, points)], 0, 30_000, maxIntervals: 4));
        Assert.Equal(4, result.Quality.Count);
        Assert.True(result.QualityTruncated);
    }
}
