using Scada.Desktop.Configuration;

namespace Scada.Desktop.Tests;

public sealed class AutostartDecisionTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void OnlyWritesRunKeyWhenNotManaged(bool enabled, bool managed, bool expected)
    {
        Assert.Equal(expected, AutostartDecision.ShouldWriteRun(enabled, managed));
    }
}
