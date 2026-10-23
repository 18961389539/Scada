using System.Collections.Concurrent;

namespace Scada.Server.Services;

public sealed class RealtimeSubscriptionStore
{
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    public void Subscribe(string connectionId, IEnumerable<int> tagIds)
    {
        var subscription = _subscriptions.GetOrAdd(connectionId, _ => new Subscription());
        lock (subscription)
        {
            foreach (var tagId in tagIds)
            {
                subscription.TagIds.Add(tagId);
            }
        }
    }

    public void Unsubscribe(string connectionId, IEnumerable<int> tagIds)
    {
        if (!_subscriptions.TryGetValue(connectionId, out var subscription))
        {
            return;
        }

        lock (subscription)
        {
            foreach (var tagId in tagIds)
            {
                subscription.TagIds.Remove(tagId);
            }
        }
    }

    public void Remove(string connectionId) => _subscriptions.TryRemove(connectionId, out _);

    public IReadOnlyList<(string ConnectionId, IReadOnlyCollection<int> TagIds)> Snapshot()
    {
        return _subscriptions.Select(pair =>
        {
            lock (pair.Value)
            {
                return (pair.Key, (IReadOnlyCollection<int>)pair.Value.TagIds.ToArray());
            }
        }).ToList();
    }

    private sealed class Subscription
    {
        public HashSet<int> TagIds { get; } = new();
    }
}
