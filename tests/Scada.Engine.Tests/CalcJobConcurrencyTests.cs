using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.Calculations;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

// _jobs 的空转快速路径在 gate 外遍历；LoadAsync（由复位/手动执行/配置重载触发）在 gate 内增删同一个集合。
// 这是一条并发冒烟测试：让两侧真正并行并断言不抛异常、状态仍可读。
// 说明：该竞态窗口（遍历与字典增删相交）只有微秒级，压力测试无法稳定复现，
// 修复的正确性来自结构本身——gate 内发布不可变数组、gate 外只读数组，而不是靠这条测试兜底。
public sealed class CalcJobConcurrencyTests
{
    private sealed class NullWriter : ICalcTagWriter
    {
        public Task<string?> CheckIdentityAsync(CalcJob job, IReadOnlyCollection<int> writeTags, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<CalcWriteResult> WriteAsync(CalcJob job, int tagId, double value, Guid commandId, CancellationToken ct)
            => Task.FromResult(new CalcWriteResult(true));
    }

    [Fact]
    public async Task TickRemainsConsistentWhileJobOperationsReloadInParallel()
    {
        using var temp = new TempDir("calc-concurrency-");
        var factory = new CalcJobTests.FileFactory(Path.Combine(temp.Path, "config.db"));
        const int SeedJobs = 12;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            for (var id = 1; id <= SeedJobs; id++) db.CalcJobs.Add(Job(id));
            await db.SaveChangesAsync();
        }

        using var connection = TestRuntime.Open();
        var runtime = TestRuntime.Factory(connection);
        using var engine = new CalcJobEngine(factory, new CalcJobTests.Snapshot(), new RealtimeCache(), new RealtimeBus(),
            new NullWriter(), Options.Create(new ScadaSettings { ShiftTimeZone = "UTC", CalcStatePersistSec = 3600 }),
            NullLogger<CalcJobEngine>.Instance, runtime);

        // 先跑一拍把任务表加载进来（否则快速路径看到的是空快照，测不到并发点）。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await engine.TickAsync(now);
        Assert.Equal(SeedJobs, (await engine.StatusAsync()).Count);

        // 周期设得足够长：首拍之后每一拍都会扫完整个集合而不是在第一个到期任务处 break，
        // 让"遍历中"的窗口尽量长。
        var ticker = Task.Run(async () =>
        {
            for (var i = 0; i < 300; i++) await engine.TickAsync(now + i * 10);
        });

        var nextId = SeedJobs + 1;
        for (var i = 0; i < 20; i++)
        {
            // 每轮新增一个任务：只更新已有 job 不会改动 _jobs 的结构，必须增删才会与遍历相交。
            await using (var db = factory.CreateDbContext())
            {
                db.CalcJobs.Add(Job(nextId++));
                await db.SaveChangesAsync();
            }
            engine.RequestReload();
            await engine.ResetAsync(1, "并发测试");
        }

        await ticker.WaitAsync(TimeSpan.FromSeconds(120));
        await engine.WaitForScriptsAsync().WaitAsync(TimeSpan.FromSeconds(120));

        // 没有抛 Collection was modified；新任务已全部加载且状态可读。
        Assert.Equal(nextId - 1, (await engine.StatusAsync()).Count);
    }

    private static CalcJob Job(int id) => new()
    {
        Id = id,
        Name = $"job-{id}",
        Kind = CalcJobKind.Accumulator,
        Mode = AccumulatorMode.Pulse,
        Factor = 1,
        Enabled = true,
        IntervalMs = 3_600_000
    };
}
