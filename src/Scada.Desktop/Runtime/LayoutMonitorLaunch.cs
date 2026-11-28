using Scada.Desktop.Configuration;

namespace Scada.Desktop.Runtime;

internal readonly record struct LayoutMonitorTarget(int Index, string? DeviceName);

internal readonly record struct PhysicalMonitor(string DeviceName, bool Primary, int X, int Y);

/// <summary>
/// 已发布多屏布局的窗口计划：每个逻辑显示器一个画面进程，放到已映射的物理屏；
/// 未映射时按从左到右的显示器顺序自动分配。
/// </summary>
internal static class LayoutMonitorLaunch
{
    public static IReadOnlyList<LayoutMonitorTarget> Plan(DesktopSettings settings) =>
        Plan(settings, Screen.AllScreens.Select(item => new PhysicalMonitor(item.DeviceName, item.Primary, item.Bounds.X, item.Bounds.Y)));

    public static IReadOnlyList<LayoutMonitorTarget> Plan(DesktopSettings settings, IEnumerable<PhysicalMonitor> screens)
    {
        if (!string.Equals(settings.Target.Kind, "layout", StringComparison.OrdinalIgnoreCase))
            return [new LayoutMonitorTarget(0, settings.Display.MonitorDeviceName)];

        var names = settings.Target.LayoutMonitorDeviceNames;
        var count = settings.Target.LayoutMonitorCount
            ?? (names is { Length: > 0 } ? names.Length : 1);
        if (count < 1) count = 1;
        if (count == 1)
            return [new LayoutMonitorTarget(0, FirstName(names) ?? settings.Display.MonitorDeviceName)];

        var ordered = screens
            .OrderBy(item => item.Primary ? 0 : 1)
            .ThenBy(item => item.X)
            .ThenBy(item => item.Y)
            .ToArray();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new LayoutMonitorTarget[count];
        for (var i = 0; i < count; i++)
        {
            var named = names is not null && i < names.Length && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : null;
            if (named is not null) used.Add(named);
            result[i] = new LayoutMonitorTarget(i, named);
        }
        var auto = 0;
        for (var i = 0; i < count; i++)
        {
            if (result[i].DeviceName is not null) continue;
            while (auto < ordered.Length && used.Contains(ordered[auto].DeviceName)) auto++;
            var fallback = auto < ordered.Length ? ordered[auto++].DeviceName : settings.Display.MonitorDeviceName;
            if (fallback is not null) used.Add(fallback);
            result[i] = new LayoutMonitorTarget(i, fallback);
        }
        return result;
    }

    private static string? FirstName(string[]? names) =>
        names is { Length: > 0 } && !string.IsNullOrWhiteSpace(names[0]) ? names[0] : null;
}
