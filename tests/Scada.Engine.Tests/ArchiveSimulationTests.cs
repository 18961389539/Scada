using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public class ArchiveSimulationTests
{
    [Fact]
    public void QualityUnchangedButSimulationStart_IsStored()
    {
        var tag = new Scada.Core.Entities.Tag { Id = 1, ArchiveMode = ArchiveMode.OnChange, Deadband = 0.1 };
        var current = new TagValue(1, 10, TagQuality.Good, 2000, Simulated: true);
        var last = new ArchiveState(1000, 10, TagQuality.Good, Simulated: false);
        Assert.True(ArchivePolicy.ShouldStore(tag, current, last, 2000));
    }
}
