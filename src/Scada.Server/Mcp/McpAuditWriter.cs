using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Mcp;

// 每次 MCP 调用都要留一条操作审计。逐条同步 insert 会在只读轮询下放大成一堆单行事务，
// 所以这里做「合并批次 + 请求内等待」：调用方仍拿到"已落库"的保证（审计先于响应），
// 但同一瞬间的多次调用共用一个事务。
//
// 两条硬约束：
// 1) 审计永远不能改变工具结果——写入失败只记日志并计数，调用方照常返回；
// 2) 队列满时不阻塞请求：丢弃并告警，丢弃数通过 scada://metrics 暴露。
public sealed class McpAuditWriter : IAsyncDisposable
{
    private const int Capacity = 8192;
    private const int BatchSize = 256;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly ILogger<McpAuditWriter> _logger;
    private readonly Channel<Entry> _channel;
    private int _pump;
    private int _pending;
    private long _written, _failed, _dropped, _timedOut;

    public McpAuditWriter(IDbContextFactory<ConfigDbContext> factory, ILogger<McpAuditWriter> logger)
    {
        _factory = factory;
        _logger = logger;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // 用 Wait 而不是 DropWrite：TryWrite 会返回 false，丢弃因此可见、可计数。
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int Pending => Volatile.Read(ref _pending);
    public long Written => Interlocked.Read(ref _written);
    public long Failed => Interlocked.Read(ref _failed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long TimedOut => Interlocked.Read(ref _timedOut);

    public async Task RecordAsync(string targetType, string action, string targetId, string outcome)
    {
        var entry = new Entry(new OperationLog
        {
            Ts = ScadaTime.NowMs(),
            UserName = "mcp:lan",
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Result = outcome
        }, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(entry))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            _logger.LogWarning("MCP 审计队列已满（{Capacity}），丢弃一条 {TargetType} 记录：{Action}", Capacity, targetType, action);
            return;
        }
        StartPump();
        try
        {
            await entry.Done.Task.WaitAsync(FlushWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref _timedOut);
            _logger.LogWarning("MCP 审计写入超过 {Seconds} 秒仍未完成：{TargetType} {Action}",
                FlushWait.TotalSeconds, targetType, action);
        }
    }

    private void StartPump()
    {
        if (Interlocked.CompareExchange(ref _pump, 1, 0) != 0) return;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // 短暂等待让同一瞬间的调用聚成一批；并发高峰下 N 次调用只付一个事务。
                await Task.Delay(Debounce).ConfigureAwait(false);
                var batch = new List<Entry>(BatchSize);
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var entry)) batch.Add(entry);
                if (batch.Count > 0) await FlushAsync(batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP 审计后台写入循环异常终止，后续调用会重新启动它。");
        }
        finally
        {
            Volatile.Write(ref _pump, 0);
            if (_channel.Reader.TryPeek(out _)) StartPump();
        }
    }

    private async Task FlushAsync(List<Entry> batch)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
            foreach (var entry in batch) db.OperationLogs.Add(entry.Row);
            await db.SaveChangesAsync().ConfigureAwait(false);
            Interlocked.Add(ref _written, batch.Count);
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _failed, batch.Count);
            _logger.LogError(ex, "MCP 审计批量写入失败：{Count} 条；本次工具结果不受影响。", batch.Count);
        }
        finally
        {
            Interlocked.Add(ref _pending, -batch.Count);
            foreach (var entry in batch) entry.Done.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        for (var i = 0; i < 50 && Pending > 0; i++) await Task.Delay(100).ConfigureAwait(false);
        if (Pending > 0) _logger.LogWarning("进程退出时仍有 {Count} 条 MCP 审计未落库。", Pending);
    }

    private sealed record Entry(OperationLog Row, TaskCompletionSource Done);
}
