using Scada.Desktop.Configuration;
using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class TargetUrlTests
{
    [Fact]
    public void BuildsScreenUrlWithMonitorMode()
    {
        var settings = Settings("screen", screenId: 7, monitorOnly: true);
        Assert.Equal("http://localhost:5080/kiosk/screens/7?monitorOnly=1", TargetUrl.Build(settings));
    }

    [Fact]
    public void BuildsScreenUrlWithoutMonitorMode()
    {
        var settings = Settings("screen", screenId: 7, monitorOnly: false);
        Assert.Equal("http://localhost:5080/kiosk/screens/7", TargetUrl.Build(settings));
    }

    [Fact]
    public void BuildsLayoutUrl()
    {
        var settings = Settings("layout", layoutId: 3);
        Assert.Equal("http://localhost:5080/console/3", TargetUrl.Build(settings));
    }

    [Fact]
    public void BuildsWorkstationConsoleUrl()
    {
        var settings = Settings("workstation", workstationId: 5);
        Assert.Equal("http://localhost:5080/console", TargetUrl.Build(settings));
    }

    [Fact]
    public void BuildsWorkspaceUrl()
    {
        var settings = Settings("workspace");
        Assert.Equal("http://localhost:5080/workspace", TargetUrl.Build(settings));
    }

    [Theory]
    [InlineData("/login", true)]
    [InlineData("/login/", true)]
    [InlineData("/console", false)]
    [InlineData("/kiosk/screens/1", false)]
    public void DetectsLoginPage(string path, bool expected)
    {
        Assert.Equal(expected, TargetUrl.IsLoginPage(new Uri("http://localhost:5080" + path)));
    }

    private static DesktopSettings Settings(string kind, int? screenId = null, int? layoutId = null, int? workstationId = null, bool monitorOnly = false) =>
        new()
        {
            ServerUrl = "http://localhost:5080/",
            Target = new DesktopTarget
            {
                Kind = kind,
                ScreenId = screenId,
                LayoutId = layoutId,
                WorkstationId = workstationId,
                MonitorOnly = monitorOnly
            }
        };
}
