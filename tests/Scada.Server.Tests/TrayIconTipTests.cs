using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class TrayIconTipTests
{
    [Theory]
    [InlineData("SCADA", "http://localhost:5080", "SCADA · http://localhost:5080")]
    [InlineData("锅炉厂", "http://localhost:5080", "锅炉厂 SCADA · http://localhost:5080")]
    [InlineData("Plant A SCADA", "http://localhost:5080", "Plant A SCADA · http://localhost:5080")]
    [InlineData(" ", "http://localhost:5080", "SCADA · http://localhost:5080")]
    public void ComposeTip(string siteName, string url, string expected)
    {
        Assert.Equal(expected, TrayIcon.ComposeTip(siteName, url));
    }

    [Fact]
    public void LongSiteNameKeepsUrlVisible()
    {
        var tip = TrayIcon.ComposeTip(new string('长', 200), "http://localhost:5080");
        Assert.True(tip.Length <= 120);
        Assert.EndsWith("· http://localhost:5080", tip);
    }
}