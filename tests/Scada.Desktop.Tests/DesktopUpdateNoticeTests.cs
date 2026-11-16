using Scada.Desktop.Api;
using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class DesktopUpdateNoticeTests
{
    [Fact]
    public void NoPolicyOrNoLatestVersionProducesNoNotice()
    {
        Assert.False(DesktopUpdateNotice.TryCreate(null, out _, out _));
        Assert.False(DesktopUpdateNotice.TryCreate(new VersionPolicy(null, null, null, false, false), out _, out _));
    }

    [Fact]
    public void NoticeWithoutDownloadUrlPointsToAdministrator()
    {
        var policy = new VersionPolicy("1.0.0", "1.2.0", null, false, true);

        Assert.True(DesktopUpdateNotice.TryCreate(policy, out var message, out var url));
        Assert.Contains("1.2.0", message);
        Assert.Null(url);
    }

    [Fact]
    public void HttpsDownloadUrlBecomesClickable()
    {
        var policy = new VersionPolicy("1.0.0", "1.2.0", "https://scada.example.com/desktop.zip", false, true);

        Assert.True(DesktopUpdateNotice.TryCreate(policy, out var message, out var url));
        Assert.Equal("https://scada.example.com/desktop.zip", url);
        Assert.Contains("点击", message);
    }

    [Fact]
    public void NonHttpDownloadUrlIsIgnored()
    {
        var policy = new VersionPolicy("1.0.0", "1.2.0", "javascript:alert(1)", false, true);

        Assert.True(DesktopUpdateNotice.TryCreate(policy, out _, out var url));
        Assert.Null(url);
    }
}
