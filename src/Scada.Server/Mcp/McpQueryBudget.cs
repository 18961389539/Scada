using Scada.Engine.History;

namespace Scada.Server.Mcp;

public sealed class McpQueryBudget : IDisposable
{
    public const int Slots = 4;
    public const int TimeoutSeconds = 30;

    private readonly SemaphoreSlim _slots = new(Slots);
    private long _rejected;
    private long _timedOut;

    public int Active => Slots - _slots.CurrentCount;
    public long Rejected => Interlocked.Read(ref _rejected);
    public long TimedOut => Interlocked.Read(ref _timedOut);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(0, ct))
        {
            Interlocked.Increment(ref _rejected);
            throw new McpToolException(McpErrorCodes.QueryBusy, "Four analysis queries are already running. Retry later.", true);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try { return await query(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            Interlocked.Increment(ref _timedOut);
            throw new McpToolException(McpErrorCodes.QueryTimeout, "Reduce the time range or number of objects.", true);
        }
        catch (HistoryQueryException ex) { throw McpErrors.Map(ex); }
        finally { _slots.Release(); }
    }

    public void Dispose() => _slots.Dispose();
}
