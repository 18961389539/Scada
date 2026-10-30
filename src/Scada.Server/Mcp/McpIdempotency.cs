using System.Collections.Concurrent;
using Scada.Core.Models;

namespace Scada.Server.Mcp;

// 写工具的幂等键：同一个 requestId 在 TTL 内只会真正执行一次，重试直接拿到首次结果。
// 单飞（single-flight）语义：并发的同键请求共享同一次执行，不会重复建对象、重复挂牌或重复启动顺控。
// 只保留内存态——MCP 是无状态传输，跨重启的强幂等需要业务表唯一约束兜底（区域/设备/测点本就有唯一名校验）。
public sealed class McpIdempotency
{
    internal const int TtlMinutes = 10;
    private const int Capacity = 2048;

    // 工具入参说明：同一段文案复用到所有可幂等的写工具，客户端从 inputSchema 就能看到。
    public const string Hint =
        "Optional idempotency key. Retrying with the same GUID within 10 minutes returns the first result " +
        "instead of repeating the effect (no duplicate record, lockout, sequence run, recipe download or report). " +
        "An empty GUID is rejected.";

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private long _calls, _hits, _misses;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public async Task<T> RunAsync<T>(string tool, Guid? requestId, Func<Task<T>> action)
    {
        if (requestId is not { } id)
        {
            Interlocked.Increment(ref _misses);
            return await action().ConfigureAwait(false);
        }
        if (id == Guid.Empty)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "requestId must not be an empty GUID.");

        var key = tool + ":" + id.ToString("N");
        Prune();
        var (slot, owner) = Claim(key);
        if (!owner)
        {
            Interlocked.Increment(ref _hits);
            var replay = await slot.Done.Task.ConfigureAwait(false);
            return (T)replay!;
        }
        try
        {
            var value = await action().ConfigureAwait(false);
            slot.Done.TrySetResult(value);
            return value;
        }
        catch (Exception ex)
        {
            // 失败不缓存：同一个 requestId 重试时重新执行，避免把一次性故障固化成永久结果。
            _slots.TryRemove(new KeyValuePair<string, Slot>(key, slot));
            slot.Done.TrySetException(ex);
            throw;
        }
    }

    private (Slot Slot, bool Owner) Claim(string key)
    {
        while (true)
        {
            var now = ScadaTime.NowMs();
            if (_slots.TryGetValue(key, out var existing))
            {
                if (existing.IsExpired(now))
                {
                    _slots.TryRemove(new KeyValuePair<string, Slot>(key, existing));
                    continue;
                }
                return (existing, false);
            }
            var candidate = new Slot(now);
            if (_slots.TryAdd(key, candidate)) return (candidate, true);
        }
    }

    private void Prune()
    {
        // 每 64 次调用扫一次，避免热点路径上做全表遍历。
        if ((Interlocked.Increment(ref _calls) & 63) != 0 && _slots.Count <= Capacity) return;
        var now = ScadaTime.NowMs();
        foreach (var pair in _slots)
            if (pair.Value.IsExpired(now)) _slots.TryRemove(pair);
        if (_slots.Count <= Capacity) return;
        foreach (var pair in _slots.OrderBy(x => x.Value.StoredTs).Take(_slots.Count - Capacity))
            _slots.TryRemove(pair);
    }

    private sealed class Slot(long storedTs)
    {
        public long StoredTs { get; } = storedTs;
        public TaskCompletionSource<object?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // 仍在执行中的条目不参与过期清理，否则同一个键可能被执行两次。
        public bool IsExpired(long now) => Done.Task.IsCompleted && now - StoredTs > TtlMinutes * 60_000L;
    }
}
