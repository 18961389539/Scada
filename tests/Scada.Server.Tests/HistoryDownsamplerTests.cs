using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Server.Tests;

public sealed class HistoryDownsamplerTests
{
    [Theory]
    [InlineData("history_query_busy", 503)]
    [InlineData("history_query_timeout", 504)]
    [InlineData("history_query_read_limit", 422)]
    public async Task QueryRejectionUsesProblemDetailsAndRetryAfter(string code, int status)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new Scada.Server.Middleware.HistoryQueryExceptionHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Scada.Server.Middleware.HistoryQueryExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(context, new HistoryQueryException(code, "query limit"), default));
        Assert.Equal(status, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var json = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(status == 503 ? "5" : "", context.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task StreamingCapsDenseMultiTagInputAndKeepsEndpointsSpikesAndBadQuality()
    {
        static async IAsyncEnumerable<HistoryValue> Samples()
        {
            await Task.CompletedTask;
            for (var index = 0; index < 100_000; index++)
                foreach (var tagId in new[] { 1, 2 })
                    yield return new HistoryValue(tagId, index, index == 50000 ? 999 : 1,
                        index == 50001 ? TagQuality.Bad : TagQuality.Good);
        }
        var (series, downsampled) = await HistoryDownsampler.ApplyStreamingAsync(Samples(), [1, 2], 0, 99999, 1000);
        Assert.True(downsampled);
        Assert.Equal(2, series.Count);
        Assert.True(series.Sum(x => x.Points.Count) <= 1000);
        foreach (var item in series)
        {
            Assert.Equal(0, item.Points[0].Ts);
            Assert.Equal(99999, item.Points[^1].Ts);
            Assert.Contains(item.Points, point => point.Value == 999);
            Assert.Contains(item.Points, point => point.Quality == TagQuality.Bad);
        }
    }

    [Fact]
    public async Task StreamingLeavesSmallSeriesExactAndHonorsCancellation()
    {
        static async IAsyncEnumerable<HistoryValue> Samples()
        {
            await Task.CompletedTask;
            for (var index = 0; index < 10; index++)
                yield return new HistoryValue(1, index, index, TagQuality.Good);
        }
        var (series, downsampled) = await HistoryDownsampler.ApplyStreamingAsync(Samples(), [1], 0, 9, 100);
        Assert.False(downsampled);
        Assert.Equal(10, Assert.Single(series).Points.Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HistoryDownsampler.ApplyStreamingAsync(Samples(), [1], 0, 9, 100, cancellation.Token));
    }

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
