using Scada.Core.Options;
using Scada.Server.Desktop;

namespace Scada.Server.Tests;

public sealed class DesktopClientPolicyTests
{
    [Fact]
    public void BlocksBelowMinimumAndExposesDownload()
    {
        var policy = DesktopClientPolicy.Build("1.0.0", new DesktopClientOptions
        {
            MinimumVersion = "9.9.9",
            LatestVersion = "9.9.9",
            DownloadUrl = "https://example.com/desktop.zip"
        });

        Assert.True(policy.UpdateRequired);
        Assert.True(policy.UpdateAvailable);
        Assert.Equal("9.9.9", policy.MinimumVersion);
        Assert.Equal("https://example.com/desktop.zip", policy.DownloadUrl);
    }

    [Fact]
    public void UnknownVersionFailsOpen()
    {
        var policy = DesktopClientPolicy.Build("not-a-version", new DesktopClientOptions
        {
            MinimumVersion = "9.9.9"
        });
        Assert.False(policy.UpdateRequired);
    }

    [Fact]
    public void InvalidDownloadUrlIsDropped()
    {
        var policy = DesktopClientPolicy.Build("1.0.0", new DesktopClientOptions
        {
            DownloadUrl = "javascript:alert(1)",
            LatestVersion = "2.0.0"
        });
        Assert.Null(policy.DownloadUrl);
        Assert.True(policy.UpdateAvailable);
    }

    [Fact]
    public void EmptyPolicyDoesNotBlock()
    {
        var policy = DesktopClientPolicy.Build("1.0.0", new DesktopClientOptions());
        Assert.False(policy.UpdateRequired);
        Assert.False(policy.UpdateAvailable);
    }
}
