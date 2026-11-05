using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Mcp;

// 写工具的幂等键：同一个 requestId 在 TTL 内只会真正执行一次，重试直接拿到首次结果。
//
// 两层保护，各解决一个问题：
// 1) 进程内单飞（Slot）：并发的同键请求共享同一次执行，不会重复建对象、重复挂牌或重复启动顺控。
// 2) 数据库占位（McpIdempotencyRecord）：进程重启、主备切换或多实例部署时，
//    唯一索引 (Tool, RequestId) 保证只有一个执行者能占位，其余请求等待或读到已存结果。
//
// 数据库不可用时降级为纯进程内幂等并记日志：宁可少一层保护，也不能让已经成功的写操作因为幂等表写不进去而失败。
public sealed class McpIdempotency
{
    internal const int TtlMinutes = 10;
    private const int Capacity = 2048;

    // 等待首次执行的上限：超过后返回可重试的 idempotency_in_progress，而不是无限挂住 HTTP 请求。
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);

    // 占位超过这个时间仍未完成，视为进程崩溃留下的僵尸记录，允许后来者接管。
    // 取值明显大于任何单个工具的合理执行时间，避免把仍在运行的长任务误判为僵尸而重复执行。
    internal static readonly TimeSpan DefaultPendingTimeout = TimeSpan.FromMinutes(5);

    private const long PruneIntervalMs = 5 * 60_000L;

    // 幂等结果本身受 1 MiB 全局结果上限约束；这里再留一道保险，避免异常大的结果撑爆运行库。
    private const int MaxStoredResultChars = 64 * 1024;

    // 工具入参说明：同一段文案复用到所有可幂等的写工具，客户端从 inputSchema 就能看到。
    public const string Hint =
        "Optional idempotency key. Retrying with the same GUID within 10 minutes returns the first result " +
        "instead of repeating the effect (no duplicate record, lockout, sequence run, recipe download or report), " +
        "including after a server restart or a failover to another node. An empty GUID is rejected.";

    private readonly IDbContextFactory<RuntimeDbContext>? _factory;
    private readonly ILogger<McpIdempotency>? _logger;
    private readonly TimeSpan _waitTimeout;
    private readonly TimeSpan _pendingTimeout;

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private long _calls, _hits, _misses, _replaysFromStore, _lastPruneTs;

    public McpIdempotency(
        IDbContextFactory<RuntimeDbContext>? factory = null,
        ILogger<McpIdempotency>? logger = null,
        TimeSpan? waitTimeout = null,
        TimeSpan? pendingTimeout = null)
    {
        _factory = factory;
        _logger = logger;
        _waitTimeout = waitTimeout ?? DefaultWaitTimeout;
        _pendingTimeout = pendingTimeout ?? DefaultPendingTimeout;
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>重启或跨实例后，从数据库读回首次结果的重放次数。</summary>
    public long ReplaysFromStore => Interlocked.Read(ref _replaysFromStore);

    public async Task<T> RunAsync<T>(string tool, Guid? requestId, CancellationToken cancellationToken, Func<Task<T>> action)
    {
        if (requestId is not { } id)
        {
            Interlocked.Increment(ref _misses);
            return await action().ConfigureAwait(false);
        }
        if (id == Guid.Empty)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "requestId must not be an empty GUID.");

        Prune();
        await PruneStoreAsync(cancellationToken).ConfigureAwait(false);

        var key = tool + ":" + id.ToString("N");
        var (slot, owner) = Claim(key);
        if (!owner)
        {
            Interlocked.Increment(ref _hits);
            return await AwaitOwnerAsync<T>(slot, tool, id, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var value = await ExecuteAsync(tool, id, cancellationToken, action).ConfigureAwait(false);
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

    // 同进程的后来者：先等本地执行者；它迟迟不结束时再读一次库，可能是另一实例已经完成的结果。
    private async Task<T> AwaitOwnerAsync<T>(Slot slot, string tool, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var replay = await slot.Done.Task.WaitAsync(_waitTimeout, cancellationToken).ConfigureAwait(false);
            return (T)replay!;
        }
        catch (TimeoutException)
        {
            var stored = await TryReadCompletedAsync<T>(tool, id, cancellationToken).ConfigureAwait(false);
            if (stored.Found) return stored.Value;
            throw InProgress(tool);
        }
    }

    private async Task<T> ExecuteAsync<T>(string tool, Guid id, CancellationToken cancellationToken, Func<Task<T>> action)
    {
        var stored = await TryReadCompletedAsync<T>(tool, id, cancellationToken).ConfigureAwait(false);
        if (stored.Found) return stored.Value;

        switch (await TryReserveAsync(tool, id, cancellationToken).ConfigureAwait(false))
        {
            case Reservation.Completed:
                var replay = await TryReadCompletedAsync<T>(tool, id, cancellationToken).ConfigureAwait(false);
                if (replay.Found) return replay.Value;
                // 记录明确显示已完成，却读不出结果：状态不一致时宁可拒绝，也不重复执行一次带副作用的写操作。
                throw new McpToolException(McpErrorCodes.DataIntegrity,
                    "The stored idempotent result is not readable. Use a new requestId instead of retrying this one.");
            case Reservation.Busy:
                var polled = await PollCompletedAsync<T>(tool, id, cancellationToken).ConfigureAwait(false);
                if (polled.Found) return polled.Value;
                throw InProgress(tool);
        }

        try
        {
            var value = await action().ConfigureAwait(false);
            await TryCompleteAsync(tool, id, value).ConfigureAwait(false);
            return value;
        }
        catch
        {
            // 执行失败就释放占位，让同一个 requestId 的重试可以真正再跑一次。
            await TryReleaseAsync(tool, id).ConfigureAwait(false);
            throw;
        }
    }

    private enum Reservation
    {
        /// <summary>占位成功，由当前调用执行。</summary>
        Held,
        /// <summary>另一个实例正在执行同名占位。</summary>
        Busy,
        /// <summary>另一个实例已经完成，可直接重放。</summary>
        Completed,
        /// <summary>持久化不可用，退化为纯进程内幂等。</summary>
        Degraded
    }

    private async Task<Reservation> TryReserveAsync(string tool, Guid id, CancellationToken cancellationToken)
    {
        if (_factory is null) return Reservation.Degraded;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await RemoveStaleAsync(db, tool, id, cancellationToken).ConfigureAwait(false);
            db.McpIdempotencyRecords.Add(new McpIdempotencyRecord
            {
                Tool = tool,
                RequestId = id,
                Status = McpIdempotencyStates.Pending,
                CreatedTs = ScadaTime.NowMs()
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Reservation.Held;
        }
        catch (DbUpdateException)
        {
            // 唯一索引冲突：另一个执行者已经占位。它可能刚完成，也可能仍在跑。
            var existing = await ReadAsync(tool, id, cancellationToken).ConfigureAwait(false);
            if (existing is null) return Reservation.Degraded;
            return existing.Status == McpIdempotencyStates.Completed ? Reservation.Completed : Reservation.Busy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "MCP 幂等占位写入失败（{Tool}），本次仅保留进程内幂等。", tool);
            return Reservation.Degraded;
        }
    }

    // 过期记录让 TTL 语义生效；僵尸占位让进程崩溃后仍能恢复，而不是永久堵住同一个 requestId。
    private async Task RemoveStaleAsync(RuntimeDbContext db, string tool, Guid id, CancellationToken cancellationToken)
    {
        var now = ScadaTime.NowMs();
        var completedCutoff = now - TtlMinutes * 60_000L;
        var pendingCutoff = now - (long)_pendingTimeout.TotalMilliseconds;
        var stale = await db.McpIdempotencyRecords
            .Where(x => x.Tool == tool && x.RequestId == id
                && ((x.Status == McpIdempotencyStates.Completed && x.CreatedTs < completedCutoff)
                    || (x.Status == McpIdempotencyStates.Pending && x.CreatedTs < pendingCutoff)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (stale.Count == 0) return;
        db.McpIdempotencyRecords.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stored<T>> PollCompletedAsync<T>(string tool, Guid id, CancellationToken cancellationToken)
    {
        var deadline = ScadaTime.NowMs() + (long)_waitTimeout.TotalMilliseconds;
        while (ScadaTime.NowMs() < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var stored = await TryReadCompletedAsync<T>(tool, id, cancellationToken).ConfigureAwait(false);
            if (stored.Found) return stored;
        }
        return Stored<T>.Missing;
    }

    private async Task<Stored<T>> TryReadCompletedAsync<T>(string tool, Guid id, CancellationToken cancellationToken)
    {
        var record = await ReadAsync(tool, id, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Status != McpIdempotencyStates.Completed) return Stored<T>.Missing;
        if (ScadaTime.NowMs() - record.CreatedTs > TtlMinutes * 60_000L) return Stored<T>.Missing;
        if (string.IsNullOrEmpty(record.ResultJson))
            throw new McpToolException(McpErrorCodes.DataIntegrity,
                "The stored idempotent result is empty. Use a new requestId instead of retrying this one.");

        try
        {
            var value = JsonSerializer.Deserialize<T>(record.ResultJson, McpEndpoints.Json);
            if (value is null) return Stored<T>.Missing;
            Interlocked.Increment(ref _replaysFromStore);
            return new Stored<T>(true, value);
        }
        catch (JsonException ex)
        {
            // 结构变更后旧结果的字段可能已经对不上。宁可拒绝，也不重新执行一次带副作用的写操作。
            throw new McpToolException(McpErrorCodes.DataIntegrity,
                "The stored idempotent result cannot be read by this server version. Use a new requestId.", false, ex);
        }
    }

    private async Task<McpIdempotencyRecord?> ReadAsync(string tool, Guid id, CancellationToken cancellationToken)
    {
        if (_factory is null) return null;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.McpIdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Tool == tool && x.RequestId == id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "MCP 幂等记录读取失败（{Tool}），本次退化为进程内幂等。", tool);
            return null;
        }
    }

    // 结果落库不跟随请求取消：工具已经产生副作用，结果必须记下来，否则客户端断开就会丢失重放依据。
    private async Task TryCompleteAsync<T>(string tool, Guid id, T value)
    {
        if (_factory is null) return;
        try
        {
            var json = JsonSerializer.Serialize(value, McpEndpoints.Json);
            var oversized = json.Length > MaxStoredResultChars;
            await using var db = await _factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var record = await db.McpIdempotencyRecords
                .SingleOrDefaultAsync(x => x.Tool == tool && x.RequestId == id, CancellationToken.None).ConfigureAwait(false);
            if (record is null) return;
            record.Status = McpIdempotencyStates.Completed;
            record.CompletedTs = ScadaTime.NowMs();
            record.ResultJson = oversized ? null : json;
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            if (oversized)
                _logger?.LogWarning("MCP 幂等结果超过 {Chars} 字符，仅记录完成状态（{Tool}）。", MaxStoredResultChars, tool);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP 幂等结果写入失败（{Tool}）；工具已执行成功，本次重试将无法重放。", tool);
        }
    }

    private async Task TryReleaseAsync(string tool, Guid id)
    {
        if (_factory is null) return;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await db.McpIdempotencyRecords
                .Where(x => x.Tool == tool && x.RequestId == id)
                .ExecuteDeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP 幂等占位释放失败（{Tool}）；该 requestId 会保留到占位超时。", tool);
        }
    }

    // 过期记录只在低频路径上批量清理，不让幂等检查变成每次调用一次全表扫描。
    private async Task PruneStoreAsync(CancellationToken cancellationToken)
    {
        if (_factory is null) return;
        var now = ScadaTime.NowMs();
        var last = Interlocked.Read(ref _lastPruneTs);
        if (last != 0 && now - last < PruneIntervalMs) return;
        if (Interlocked.CompareExchange(ref _lastPruneTs, now, last) != last) return;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var cutoff = now - TtlMinutes * 60_000L;
            await db.McpIdempotencyRecords.Where(x => x.CreatedTs < cutoff)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "MCP 幂等过期记录清理失败，下次调用会重试。");
        }
    }

    private static McpToolException InProgress(string tool) =>
        new(McpErrorCodes.IdempotencyInProgress,
            $"An earlier call of {tool} with this requestId is still running. Retry later; the stored result is returned once it completes.",
            true);

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

    private readonly record struct Stored<T>(bool Found, T Value)
    {
        public static Stored<T> Missing => new(false, default!);
    }
}
