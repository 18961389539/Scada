using ModelContextProtocol;
using Scada.Engine.History;

namespace Scada.Server.Mcp;

public sealed class McpQueryBudget : IDisposable
{
    private readonly SemaphoreSlim _slots = new(4);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(0, ct))
            throw new McpException("mcp_query_busy: Four analysis queries are already running. Retry later.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await query(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new McpException("mcp_query_timeout: Reduce the time range or number of objects.");
        }
        catch (HistoryQueryException ex) { throw new McpException($"{ex.Code}: {ex.Message}", ex); }
        finally { _slots.Release(); }
    }

    public void Dispose() => _slots.Dispose();
}
