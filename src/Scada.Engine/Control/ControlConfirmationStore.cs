using System.Collections.Concurrent;
using Scada.Core.Models;

namespace Scada.Engine.Control;

// 服务端确认凭据：只有通过 CheckAndGrantAsync 且无拒绝项的检查才会签发，
// 绑定“用户 + 测点 + 目标值（字符串按内容）”，有效期短且只能消费一次。
// 客户端传来的 confirmed 布尔因此不再是充分条件。
public sealed class ControlConfirmationStore
{
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(60);

    private readonly record struct Key(int UserId, int TagId, long ValueBits, string? Text);
    private sealed record IssuedGrant(long IssuedTs);

    private readonly ConcurrentDictionary<Key, IssuedGrant> _grants = new();
    private long _lastPurgeTs;

    public void Grant(int? userId, int tagId, double value, string? text)
    {
        if (userId is not int id) return;
        Purge();
        _grants[new Key(id, tagId, BitConverter.DoubleToInt64Bits(value), text)] = new IssuedGrant(ScadaTime.NowMs());
    }

    public bool TryConsume(int? userId, int tagId, double value, string? text, out long issuedTs)
    {
        issuedTs = 0;
        if (userId is not int id) return false;
        if (!_grants.TryRemove(new Key(id, tagId, BitConverter.DoubleToInt64Bits(value), text), out var grant)) return false;
        if (ScadaTime.NowMs() - grant.IssuedTs > (long)Lifetime.TotalMilliseconds) return false;
        issuedTs = grant.IssuedTs;
        return true;
    }

    private void Purge()
    {
        var now = ScadaTime.NowMs();
        if (now - Interlocked.Read(ref _lastPurgeTs) < (long)Lifetime.TotalMilliseconds) return;
        Interlocked.Exchange(ref _lastPurgeTs, now);
        var cutoff = now - (long)Lifetime.TotalMilliseconds;
        foreach (var pair in _grants)
            if (pair.Value.IssuedTs < cutoff) _grants.TryRemove(pair.Key, out _);
    }
}