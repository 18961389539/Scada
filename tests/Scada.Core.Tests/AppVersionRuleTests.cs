using Scada.Core.Versioning;

namespace Scada.Core.Tests;

public sealed class AppVersionRuleTests
{
    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.2", true)]
    [InlineData("1", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData(" 1.2.3 ", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("1.2.3+build.7", true)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("not-a-version", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParsesSemanticVersionsAndRejectsGarbage(string? value, bool expected) =>
        Assert.Equal(expected, AppVersionRule.TryParse(value, out _));

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    [InlineData("1.0.0", "1.0", false)]
    [InlineData("1.0.0", "1.0.0.1", true)]
    [InlineData("1.0.0", null, false)]
    [InlineData("1.0.0", "", false)]
    public void ComparesOnlyNumericSegments(string? current, string? required, bool expected) =>
        Assert.Equal(expected, AppVersionRule.IsBelow(current, required));

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.1", false)]
    [InlineData("garbage", "1.0.1", false)]
    [InlineData("1.0.1", "garbage", false)]
    public void UnparsableVersionFailsOpen(string? current, string? required, bool expected) =>
        Assert.Equal(expected, AppVersionRule.IsBelow(current, required));
}
