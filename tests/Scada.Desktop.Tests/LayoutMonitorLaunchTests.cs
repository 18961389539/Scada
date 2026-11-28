using Scada.Desktop.Configuration;
using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class LayoutMonitorLaunchTests
{
    [Fact]
    public void SingleLayoutWindowUsesConfiguredDisplay()
    {
        var settings = Settings("layout", count: 1, names: ["\\\\.\\DISPLAY2"]);
        settings.Display.MonitorDeviceName = "\\\\.\\DISPLAY1";
        var plan = LayoutMonitorLaunch.Plan(settings, []);
        Assert.Equal([new LayoutMonitorTarget(0, "\\\\.\\DISPLAY2")], plan);
    }

    [Fact]
    public void DualLayoutOpensMappedWindowsAndAutoFillsMissing()
    {
        var settings = Settings("layout", count: 2, names: ["\\\\.\\LEFT", ""]);
        var screens = new[]
        {
            new PhysicalMonitor("\\\\.\\RIGHT", false, 1920, 0),
            new PhysicalMonitor("\\\\.\\PRIMARY", true, 0, 0)
        };
        var plan = LayoutMonitorLaunch.Plan(settings, screens);
        Assert.Equal(2, plan.Count);
        Assert.Equal(0, plan[0].Index);
        Assert.Equal("\\\\.\\LEFT", plan[0].DeviceName);
        Assert.Equal(1, plan[1].Index);
        Assert.Equal("\\\\.\\PRIMARY", plan[1].DeviceName);
    }

    [Fact]
    public void NonLayoutTargetStaysSingleWindow()
    {
        var settings = Settings("screen", count: 2, names: ["A", "B"]);
        settings.Display.MonitorDeviceName = "MAIN";
        var plan = LayoutMonitorLaunch.Plan(settings, [new PhysicalMonitor("A", true, 0, 0), new PhysicalMonitor("B", false, 1, 0)]);
        Assert.Equal([new LayoutMonitorTarget(0, "MAIN")], plan);
    }

    private static DesktopSettings Settings(string kind, int count, string[] names) => new()
    {
        Target = new DesktopTarget
        {
            Kind = kind,
            LayoutId = 3,
            LayoutMonitorCount = count,
            LayoutMonitorDeviceNames = names
        }
    };
}
