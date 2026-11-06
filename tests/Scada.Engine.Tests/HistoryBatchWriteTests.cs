using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Data.Infrastructure;
using Scada.Engine.History;
// Scada.Data 与 Scada.Core.Models 都定义了同名的 HistoryValue，这里明确用领域模型那个。
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Engine.Tests;

// 历史写入从"每点一次 INSERT"改成分块多值 INSERT 后，落盘语义必须完全不变：
// 一个点都不能少，同一 (TagId, Ts) 仍然只保留最差质量，重放不能把坏点洗成好点。
public sealed class HistoryBatchWriteTests
{
    [Fact]
    public async Task BatchedInsertKeepsEveryPointAcrossChunkBoundary()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        // 450 个点跨过 200 行的分块边界，最后一块不足 200。
        var values = Enumerable.Range(0, 450)
            .Select(index => new HistoryValue(7, start + index * 1000L, index, TagQuality.Good))
            .ToArray();

        await store.WriteRawAsync(values, default);

        var series = Assert.Single(await store.QueryRawAsync([7], start, start + 450_000, default));
        Assert.Equal(450, series.Points.Count);
        Assert.Equal(0d, series.Points[0].Value);
        Assert.Equal(449d, series.Points[^1].Value);
    }

    [Fact]
    public async Task WorseQualityOverwritesBetterButNotTheOtherWayAround()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // 先好后坏：坏质量覆盖。
        await store.WriteRawAsync([new HistoryValue(1, start, 10, TagQuality.Good)], default);
        await store.WriteRawAsync([new HistoryValue(1, start, 11, TagQuality.CommFailure)], default);
        // 先坏后好：好质量不能把坏点洗白。
        await store.WriteRawAsync([new HistoryValue(2, start, 20, TagQuality.CommFailure)], default);
        await store.WriteRawAsync([new HistoryValue(2, start, 21, TagQuality.Good)], default);

        var series = await store.QueryRawAsync([1, 2], start, start, default);
        var first = Assert.Single(series, x => x.TagId == 1).Points;
        var second = Assert.Single(series, x => x.TagId == 2).Points;
        Assert.Equal(TagQuality.CommFailure, Assert.Single(first).Quality);
        Assert.Equal(11d, first[0].Value);
        Assert.Equal(TagQuality.CommFailure, Assert.Single(second).Quality);
        Assert.Equal(20d, second[0].Value);
    }

    private static HistoryStore NewStore(TempDir dir) =>
        new(new HistoryDbFactory(Options.Create(new ScadaSettings { HistoryRoot = dir.Path })));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scada-history-batch-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* 句柄释放失败不影响断言 */ }
        }
    }
}
