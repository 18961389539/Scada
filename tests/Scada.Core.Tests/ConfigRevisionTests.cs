using Scada.Core.Configuration;

namespace Scada.Core.Tests;

public class ConfigRevisionTests
{
    [Fact]
    public void OmittedRevisionIsAcceptedForLegacyClients()
    {
        Assert.True(ConfigRevision.TryAccept(3, null, out var issue));
        Assert.Null(issue);
    }

    [Fact]
    public void ZeroRevisionIsAcceptedAsLegacyOmit()
    {
        Assert.True(ConfigRevision.TryAccept(3, 0, out var issue));
        Assert.Null(issue);
    }

    [Fact]
    public void MatchingRevisionIsAccepted()
    {
        Assert.True(ConfigRevision.TryAccept(3, 3, out var issue));
        Assert.Null(issue);
    }

    [Fact]
    public void MismatchedRevisionIsConflict()
    {
        Assert.False(ConfigRevision.TryAccept(3, 2, out var issue));
        Assert.NotNull(issue);
        Assert.Equal(ConfigRevision.ConflictCode, issue.Value.Code);
        Assert.Equal(ConfigRevision.ConflictMessage, issue.Value.Message);
    }

    [Fact]
    public void NextIncrementsFromAtLeastOne()
    {
        Assert.Equal(2, ConfigRevision.Next(1));
        Assert.Equal(2, ConfigRevision.Next(0));
        Assert.Equal(11, ConfigRevision.Next(10));
    }
}
