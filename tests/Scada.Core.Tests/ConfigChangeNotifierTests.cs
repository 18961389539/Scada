using Scada.Core.Configuration;

namespace Scada.Core.Tests;

public sealed class ConfigChangeNotifierTests
{
    [Fact]
    public void PublishSkipsEmptyAndDeliversToSubscribers()
    {
        var notifier = new ConfigChangeNotifier();
        var seen = new List<ConfigChangeSet>();
        using var sub = notifier.Subscribe(seen.Add);
        notifier.Publish(ConfigChangeSet.Empty);
        Assert.Empty(seen);
        notifier.Publish(ConfigChangeSet.Of(ConfigChangeKind.Alarms, ConfigChangeKind.Jobs));
        Assert.Single(seen);
        Assert.True(seen[0].Alarms);
        Assert.True(seen[0].Jobs);
        Assert.False(seen[0].Collection);
    }
}
