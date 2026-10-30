using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Data;
using Scada.Server.Mcp;

namespace Scada.Server.Tests;

// 审计与幂等键的失败面：审计写不进去不能改变工具结果；同一 requestId 的并发请求只能真正执行一次。
public sealed class McpResilienceTests
{
    private sealed class BrokenFactory : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => throw new InvalidOperationException("audit store unavailable");
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit store unavailable");
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
            Task.Run(() => idempotency.RunAsync("t", requestId, Work)),
            Task.Run(() => idempotency.RunAsync("t", requestId, Work)));

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

        await idempotency.RunAsync("t", Guid.NewGuid(), Work);
        await idempotency.RunAsync("t", Guid.NewGuid(), Work);

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
            idempotency.RunAsync("t", Guid.Empty, () => { runs++; return Task.FromResult(1); }));
        Assert.Equal(McpErrorCodes.InvalidArgument, error.Code);
        Assert.Equal(0, runs);
    }
}
