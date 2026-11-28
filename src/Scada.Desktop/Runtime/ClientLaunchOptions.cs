namespace Scada.Desktop.Runtime;

/// <summary>画面进程启动参数：多屏布局时由守护进程为每个逻辑显示器拉起一个实例。</summary>
internal sealed class ClientLaunchOptions
{
    public int ParentProcessId { get; init; }
    public int LayoutMonitorIndex { get; init; }
    public string? MonitorDeviceName { get; init; }

    public string MutexName =>
        LayoutMonitorIndex <= 0
            ? @"Local\Scada.Desktop.Client"
            : $@"Local\Scada.Desktop.Client.m{LayoutMonitorIndex}";

    public static ClientLaunchOptions Parse(string[] args)
    {
        var parent = 0;
        var monitor = 0;
        string? device = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--parent", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) && pid > 0)
            {
                parent = pid;
                i++;
                continue;
            }
            if (string.Equals(args[i], "--layout-monitor", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length && int.TryParse(args[i + 1], out var index) && index >= 0)
            {
                monitor = index;
                i++;
                continue;
            }
            if (string.Equals(args[i], "--monitor-device", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length)
            {
                device = string.IsNullOrWhiteSpace(args[i + 1]) ? null : args[i + 1];
                i++;
            }
        }
        return new ClientLaunchOptions { ParentProcessId = parent, LayoutMonitorIndex = monitor, MonitorDeviceName = device };
    }

    public void Apply(Configuration.DesktopSettings settings)
    {
        if (LayoutMonitorIndex >= 0 && settings.Target.Kind == "layout")
            settings.Target.LayoutMonitorIndex = LayoutMonitorIndex;
        if (!string.IsNullOrWhiteSpace(MonitorDeviceName))
            settings.Display.MonitorDeviceName = MonitorDeviceName;
    }
}
