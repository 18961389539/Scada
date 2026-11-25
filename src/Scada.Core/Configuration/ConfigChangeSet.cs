using System.Collections.Immutable;

namespace Scada.Core.Configuration;

/// <summary>
/// 配置变更的轻量分类。由 <c>ConfigReloader</c> 与 <c>ConfigRefreshService</c> 共用，
/// 订阅者按需响应；不是消息总线，也不拆服务。
/// </summary>
public enum ConfigChangeKind
{
    Collection = 1,
    Access = 2,
    Alarms = 3,
    Jobs = 4,
    ExternalAddressSpace = 5
}

public sealed class ConfigChangeSet
{
    public static ConfigChangeSet Empty { get; } = new(ImmutableHashSet<ConfigChangeKind>.Empty);

    private readonly ImmutableHashSet<ConfigChangeKind> _kinds;

    private ConfigChangeSet(ImmutableHashSet<ConfigChangeKind> kinds) => _kinds = kinds;

    public bool Collection => Has(ConfigChangeKind.Collection);
    public bool Access => Has(ConfigChangeKind.Access);
    public bool Alarms => Has(ConfigChangeKind.Alarms);
    public bool Jobs => Has(ConfigChangeKind.Jobs);
    public bool ExternalAddressSpace => Has(ConfigChangeKind.ExternalAddressSpace);
    public bool IsEmpty => _kinds.Count == 0;
    public IReadOnlyCollection<ConfigChangeKind> Kinds => _kinds;

    public bool Has(ConfigChangeKind kind) => _kinds.Contains(kind);

    public static ConfigChangeSet Of(params ConfigChangeKind[] kinds)
    {
        if (kinds is null || kinds.Length == 0) return Empty;
        var set = kinds.ToImmutableHashSet();
        return set.Count == 0 ? Empty : new ConfigChangeSet(set);
    }
}

/// <summary>同进程内的配置变更通知。订阅者必须自行保证处理是短且无重入死锁的。</summary>
public interface IConfigChangeNotifier
{
    void Publish(ConfigChangeSet changes);
    IDisposable Subscribe(Action<ConfigChangeSet> handler);
}

public sealed class ConfigChangeNotifier : IConfigChangeNotifier
{
    private readonly object _gate = new();
    private readonly List<Action<ConfigChangeSet>> _handlers = [];

    public void Publish(ConfigChangeSet changes)
    {
        if (changes.IsEmpty) return;
        Action<ConfigChangeSet>[] snapshot;
        lock (_gate) snapshot = [.. _handlers];
        foreach (var handler in snapshot) handler(changes);
    }

    public IDisposable Subscribe(Action<ConfigChangeSet> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<ConfigChangeSet> handler)
    {
        lock (_gate) _handlers.Remove(handler);
    }

    private sealed class Subscription(ConfigChangeNotifier owner, Action<ConfigChangeSet> handler) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            owner.Unsubscribe(handler);
        }
    }
}
