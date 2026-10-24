using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using Scada.Core.Entities;
using Scada.Core.Models;

namespace Scada.Collector.Opc;

public sealed record OpcBrowseRequest(string NodeId = "i=85", int Limit = 100, string? Cursor = null);
public sealed record OpcBrowseResponse(IReadOnlyList<OpcNodeInfo> Nodes, string? Cursor);

public sealed class OpcBrowseRegistry(OpcUaRuntime runtime) : IAsyncDisposable
{
    private sealed class Cursor(int owner, int deviceId, string revision, string nodeId, OpcCatalogSession session)
    {
        public int Owner = owner, DeviceId = deviceId; public string Revision = revision, NodeId = nodeId;
        public OpcCatalogSession Session = session; public ByteString Continuation; public DateTime Expires = DateTime.UtcNow.AddMinutes(5);
        public SemaphoreSlim Gate = new(1, 1);
    }
    private readonly ConcurrentDictionary<string, Cursor> _cursors = new();
    private readonly SemaphoreSlim _creation = new(1, 1);
    private bool _disposed;
    public async Task<OpcBrowseResponse> BrowseAsync(int owner, Device device, string revision, OpcBrowseRequest request, CancellationToken ct)
    {
        if (request.Limit is < 1 or > 200 || !OpcNodeAddress.IsValid(request.NodeId)) throw new ArgumentException("浏览节点或分页大小无效（1～200）");
        var key = request.Cursor; var created = key is null; Cursor cursor;
        if (key is null)
        {
            await _creation.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_cursors.Count >= 64 || _cursors.Values.Count(x => x.Owner == owner) >= 8) throw new InvalidOperationException("浏览会话已达上限，请关闭旧浏览或等待过期");
                var session = new OpcCatalogSession(runtime, await runtime.ConnectAsync(device, ct));
                key = Guid.NewGuid().ToString("N"); cursor = new(owner, device.Id, revision, request.NodeId, session); _cursors[key] = cursor;
            }
            finally { _creation.Release(); }
        }
        else if (!_cursors.TryGetValue(key, out cursor!) || cursor.Owner != owner || cursor.DeviceId != device.Id || cursor.NodeId != request.NodeId || cursor.Expires < DateTime.UtcNow)
            throw new KeyNotFoundException("浏览游标已过期或不属于当前用户及节点");
        // A cancelled first page still owns its newly-opened session and must close it.
        await cursor.Gate.WaitAsync(created ? CancellationToken.None : ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!_cursors.ContainsKey(key) || cursor.Revision != revision) throw new InvalidOperationException("设备配置或浏览会话已变化，请重新浏览");
            var page = await cursor.Session.BrowseAsync(request.NodeId, request.Limit, cursor.Continuation, ct);
            cursor.Continuation = page.Continuation; cursor.Expires = DateTime.UtcNow.AddMinutes(5);
            if (page.Continuation.IsEmpty || page.Continuation.IsNull) { await RemoveAsync(key); key = null; }
            return new(page.Nodes, key);
        }
        catch { if (key is not null) await RemoveAsync(key); throw; }
        finally { cursor.Gate.Release(); }
    }
    public async Task CloseAsync(int owner, int deviceId, string key, CancellationToken ct = default)
    {
        if (!_cursors.TryGetValue(key, out var cursor) || cursor.Owner != owner || cursor.DeviceId != deviceId) return;
        await cursor.Gate.WaitAsync(ct); try { await RemoveAsync(key); } finally { cursor.Gate.Release(); }
    }
    private async Task RemoveAsync(string key) { if (_cursors.TryRemove(key, out var cursor)) await cursor.Session.DisposeAsync(); }
    public async Task PruneAsync()
    { foreach (var (key, cursor) in _cursors) if (cursor.Expires < DateTime.UtcNow) await CloseAsync(cursor.Owner, cursor.DeviceId, key); }
    public async Task InvalidateAsync()
    { await _creation.WaitAsync(); try { await ClearAsync(); } finally { _creation.Release(); } }
    private async Task ClearAsync()
    { foreach (var (key, cursor) in _cursors) await CloseAsync(cursor.Owner, cursor.DeviceId, key); }
    public async ValueTask DisposeAsync()
    { await _creation.WaitAsync(); try { _disposed = true; await ClearAsync(); } finally { _creation.Release(); } }
}

public sealed class OpcBrowseRegistryCleanup(OpcBrowseRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30)); while (await timer.WaitForNextTickAsync(ct)) await registry.PruneAsync(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
