using Scada.Desktop.Api;
using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class VersionPolicyEvaluatorTests
{
    [Fact]
    public void BlocksBelowMinimum()
    {
        var policy = new VersionPolicy("1.1.0", "1.3.0", "https://example.com/a.zip", false, false);
        Assert.Equal(VersionGate.Block, VersionPolicyEvaluator.Evaluate("1.0.0", policy));
    }

    [Fact]
    public void ReportsUpdateAvailable()
    {
        var policy = new VersionPolicy("1.0.0", "1.3.0", null, false, false);
        Assert.Equal(VersionGate.UpdateAvailable, VersionPolicyEvaluator.Evaluate("1.0.0", policy));
    }

    [Fact]
    public void AcceptsCurrentVersion()
    {
        var policy = new VersionPolicy("1.0.0", "1.0.0", null, false, false);
        Assert.Equal(VersionGate.Ok, VersionPolicyEvaluator.Evaluate("1.0.0", policy));
    }

    [Fact]
    public void IgnoresPrereleaseSuffix()
    {
        var policy = new VersionPolicy("1.0.0", null, null, false, false);
        Assert.Equal(VersionGate.Ok, VersionPolicyEvaluator.Evaluate("1.0.0-beta.1", policy));
    }

    [Fact]
    public void FailsOpenForUnknownCurrentVersion()
    {
        var policy = new VersionPolicy("9.9.9", null, null, true, false);
        Assert.Equal(VersionGate.Unknown, VersionPolicyEvaluator.Evaluate("not-a-version", policy));
    }

    [Fact]
    public void TrustsServerFlagsWhenStringsAreEmpty()
    {
        Assert.Equal(VersionGate.Block, VersionPolicyEvaluator.Evaluate("1.0.0", new VersionPolicy(null, null, null, true, false)));
        Assert.Equal(VersionGate.UpdateAvailable, VersionPolicyEvaluator.Evaluate("1.0.0", new VersionPolicy(null, null, null, false, true)));
    }

    [Fact]
    public void EmptyPolicyIsUnknown()
    {
        Assert.Equal(VersionGate.Unknown, VersionPolicyEvaluator.Evaluate("1.0.0", new VersionPolicy(null, null, null, false, false)));
        Assert.Equal(VersionGate.Unknown, VersionPolicyEvaluator.Evaluate("1.0.0", null));
    }

    [Theory]
    [InlineData("1.0", "1.0.0.0", false)]
    [InlineData("1.0.0", "1.0.0.1", true)]
    [InlineData("2.0", "1.9.9.9", false)]
    public void ComparesNormalizedVersions(string current, string required, bool below)
    {
        Assert.Equal(below, VersionPolicyEvaluator.IsBelow(current, required));
    }
}
