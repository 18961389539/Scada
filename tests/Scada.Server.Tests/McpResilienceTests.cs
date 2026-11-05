using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Mcp;

namespace Scada.Server.Tests;

// 审计与幂等键的失败面：审计写不进去不能改变工具结果；同一 requestId 的并发请求只能真正执行一次；
// 进程重启或换实例后，已完成的 requestId 必须能从数据库重放，而不是再执行一遍副作用。
public sealed class McpResilienceTests
{
    private sealed class BrokenFactory : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => throw new InvalidOperationException("audit store unavailable");
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit store unavailable");
    }

    // 模拟响应很慢的审计库：延迟后失败，避免测试结束时留下悬挂任务。
    private sealed class SlowFactory(int delayMs) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => throw new NotSupportedException();
        public async Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMs, cancellationToken);
            throw new InvalidOperationException("slow audit store");
        }
    }

    private sealed class BrokenRuntimeFactory : IDbContextFactory<RuntimeDbContext>
    {
        public RuntimeDbContext CreateDbContext() => throw new InvalidOperationException("idempotency store unavailable");
        public Task<RuntimeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("idempotency store unavailable");
    }

    // 独立运行库：每个用例一份，幂等记录不进配置库。
    private sealed class TempStore : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "mcp-idem-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly IDbContextFactory<RuntimeDbContext> _factory;

        public TempStore() => _factory = TestRuntime.Factory(_path);

        public IDbContextFactory<RuntimeDbContext> Factory => _factory;

        public async Task InsertAsync(string tool, Guid requestId, int status, long createdTs)
        {
            await using var db = await _factory.CreateDbContextAsync();
            db.McpIdempotencyRecords.Add(new McpIdempotencyRecord
            {
                Tool = tool, RequestId = requestId, Status = status, CreatedTs = createdTs
            });
            await db.SaveChangesAsync();
        }

        public async Task<bool> ExistsAsync(string tool, Guid requestId)
        {
            await using var db = await _factory.CreateDbContextAsync();
            return await db.McpIdempotencyRecords.AnyAsync(x => x.Tool == tool && x.RequestId == requestId);
        }

        public async Task<McpIdempotencyRecord?> FindAsync(string tool, Guid requestId)
        {
            await using var db = await _factory.CreateDbContextAsync();
            return await db.McpIdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Tool == tool && x.RequestId == requestId);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(_path); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }

    private static McpIdempotency Create(IDbContextFactory<RuntimeDbContext> factory, int waitMs = 5000, int pendingMinutes = 5) =>
        new(factory, NullLogger<McpIdempotency>.Instance,
            waitTimeout: TimeSpan.FromMilliseconds(waitMs),
            pendingTimeout: TimeSpan.FromMinutes(pendingMinutes));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = ScadaTime.NowMs() + timeoutMs;
        while (ScadaTime.NowMs() < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition was not met in time");
    }

    [Fact]
    public async Task Audit_Write_Failure_Is_Counted_And_Never_Thrown()
    {
        await using var writer = new McpAuditWriter(new BrokenFactory(), NullLogger<McpAuditWriter>.Instance);
        await writer.RecordAsync("McpTool", "MCP search_tags", "127.0.0.1", "成功");
        Assert.Equal(1, writer.Failed);
        Assert.Equal(0, writer.Written);
        Assert.Equal(0, writer.Pending);
        Assert.Equal(0, writer.Dropped);
        Assert.Equal(0, writer.TimedOut);
    }

    [Fact]
    public async Task Zero_Flush_Wait_Returns_Immediately_And_Counts_A_Timeout()
    {
        // 审计库迟迟不响应时，等待上限 0 必须让工具结果立刻返回，而不是把请求拖满默认的 5 秒。
        await using var writer = new McpAuditWriter(new SlowFactory(400), NullLogger<McpAuditWriter>.Instance,
            Options.Create(new McpOptions { AuditFlushTimeoutMs = 0 }));

        var started = ScadaTime.NowMs();
        await writer.RecordAsync("McpTool", "MCP search_tags", "127.0.0.1", "成功");
        var elapsed = ScadaTime.NowMs() - started;

        Assert.True(elapsed < 300, $"elapsed {elapsed}ms");
        Assert.Equal(1, writer.TimedOut);
    }

    [Fact]
    public async Task Concurrent_Calls_With_The_Same_Request_Id_Execute_Once()
    {
        var idempotency = new McpIdempotency();
        var runs = 0;
        var requestId = Guid.NewGuid();

        async Task<string> Work()
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(50);
            return "ok";
        }

        var results = await Task.WhenAll(
            Task.Run(() => idempotency.RunAsync("t", requestId, CancellationToken.None, Work)),
            Task.Run(() => idempotency.RunAsync("t", requestId, CancellationToken.None, Work)));

        Assert.All(results, x => Assert.Equal("ok", x));
        Assert.Equal(1, runs);
        Assert.Equal(1, idempotency.Hits);
        Assert.Equal(0, idempotency.Misses);
    }

    [Fact]
    public async Task Different_Request_Ids_Execute_Separately()
    {
        var idempotency = new McpIdempotency();
        var runs = 0;

        async Task<int> Work()
        {
            await Task.Yield();
            return Interlocked.Increment(ref runs);
        }

        await idempotency.RunAsync("t", Guid.NewGuid(), CancellationToken.None, Work);
        await idempotency.RunAsync("t", Guid.NewGuid(), CancellationToken.None, Work);

        Assert.Equal(2, runs);
        Assert.Equal(0, idempotency.Hits);
        Assert.Equal(0, idempotency.Misses);
    }

    [Fact]
    public async Task Empty_Request_Id_Is_An_Invalid_Argument()
    {
        var idempotency = new McpIdempotency();
        var runs = 0;
        var error = await Assert.ThrowsAsync<McpToolException>(() =>
            idempotency.RunAsync("t", Guid.Empty, CancellationToken.None, () => { runs++; return Task.FromResult(1); }));
        Assert.Equal(McpErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Stored_Result_Is_Replayed_After_Restart()
    {
        await using var store = new TempStore();
        var requestId = Guid.NewGuid();
        var runs = 0;

        var beforeRestart = Create(store.Factory);
        var first = await beforeRestart.RunAsync("create_area", requestId, CancellationToken.None, () =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult("area-7");
        });

        // 模拟重启：换一个实例，进程内槽为空，只剩数据库里的记录可用。
        var afterRestart = Create(store.Factory);
        var replay = await afterRestart.RunAsync("create_area", requestId, CancellationToken.None, () =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult("should not run");
        });

        Assert.Equal("area-7", first);
        Assert.Equal("area-7", replay);
        Assert.Equal(1, runs);
        Assert.Equal(1, afterRestart.ReplaysFromStore);
    }

    [Fact]
    public async Task Completed_Record_Is_Marked_With_The_First_Result()
    {
        await using var store = new TempStore();
        var requestId = Guid.NewGuid();
        var idempotency = Create(store.Factory);

        await idempotency.RunAsync("generate_report", requestId, CancellationToken.None, () => Task.FromResult(42));

        var record = await store.FindAsync("generate_report", requestId);
        Assert.NotNull(record);
        Assert.Equal(McpIdempotencyStates.Completed, record!.Status);
        Assert.Equal("42", record.ResultJson);
        Assert.NotNull(record.CompletedTs);
    }

    [Fact]
    public async Task Another_Instance_Sees_The_Reservation_And_Does_Not_Run_Twice()
    {
        await using var store = new TempStore();
        var requestId = Guid.NewGuid();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        var instanceA = Create(store.Factory, waitMs: 300);
        var instanceB = Create(store.Factory, waitMs: 300);

        var first = instanceA.RunAsync("create_device", requestId, CancellationToken.None, () =>
        {
            Interlocked.Increment(ref runs);
            return gate.Task;
        });

        // 等 A 真正写入占位，再让 B 用同一 key 发起调用。
        await WaitUntilAsync(async () => (await store.FindAsync("create_device", requestId))?.Status == McpIdempotencyStates.Pending);

        var error = await Assert.ThrowsAsync<McpToolException>(() =>
            instanceB.RunAsync("create_device", requestId, CancellationToken.None, () =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult("b");
            }));

        Assert.Equal(McpErrorCodes.IdempotencyInProgress, error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(1, runs);

        gate.SetResult("a");
        Assert.Equal("a", await first);
    }

    [Fact]
    public async Task Waiting_For_A_Slow_First_Call_Times_Out_As_In_Progress()
    {
        await using var store = new TempStore();
        var requestId = Guid.NewGuid();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idempotency = Create(store.Factory, waitMs: 300);

        var first = idempotency.RunAsync("create_tag", requestId, CancellationToken.None, () => gate.Task);
        var error = await Assert.ThrowsAsync<McpToolException>(() =>
            idempotency.RunAsync("create_tag", requestId, CancellationToken.None, () => Task.FromResult("second")));

        Assert.Equal(McpErrorCodes.IdempotencyInProgress, error.Code);
        gate.SetResult("first");
        Assert.Equal("first", await first);
    }

    [Fact]
    public async Task Expired_Pending_Reservation_Is_Taken_Over()
    {
        await using var store = new TempStore();
        var requestId = Guid.NewGuid();
        // 十分钟前留下的占位，等价于进程崩溃后再也没人完成它。
        await store.InsertAsync("create_tag", requestId, McpIdempotencyStates.Pending,
            ScadaTime.NowMs() - 10 * 60_000L);

        var idempotency = Create(store.Factory, pendingMinutes: 1);
        var runs = 0;
        var value = await idempotency.RunAsync("create_tag", requestId, CancellationToken.None, () =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult("recovered");
        });

        Assert.Equal("recovered", value);
        Assert.Equal(1, runs);
        Assert.Equal(McpIdempotencyStates.Completed, (await store.FindAsync("create_tag", requestId))!.Status);
    }

    [Fact]
    public async Task Failed_First_Call_Releases_The_Reservation_So_A_Retry_Runs()
    {
        await using var store = new TempStore();
        var requestId = Guid.NewGuid();
        var idempotency = Create(store.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            idempotency.RunAsync("create_alarm_rule", requestId, CancellationToken.None,
                () => Task.FromException<string>(new InvalidOperationException("boom"))));

        Assert.False(await store.ExistsAsync("create_alarm_rule", requestId));

        var value = await idempotency.RunAsync("create_alarm_rule", requestId, CancellationToken.None, () => Task.FromResult("ok"));
        Assert.Equal("ok", value);
    }

    [Fact]
    public async Task Unavailable_Store_Degrades_To_In_Process_Idempotency()
    {
        // 幂等表写不进去时，不能把已经成功的写操作变成失败。
        var idempotency = new McpIdempotency(new BrokenRuntimeFactory(), NullLogger<McpIdempotency>.Instance);
        var requestId = Guid.NewGuid();
        var runs = 0;

        var value = await idempotency.RunAsync("create_area", requestId, CancellationToken.None, () =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult("area-1");
        });

        Assert.Equal("area-1", value);
        Assert.Equal(1, runs);
    }
}
