using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Scada.Desktop.App;

internal static class ClientLauncher
{
    public static Process Start(int supervisorProcessId, int? layoutMonitorIndex = null, string? monitorDeviceName = null)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定客户端程序路径");
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet run 调试：通过 dotnet <Scada.Desktop.dll> 启动同一入口。
            startInfo.FileName = executable;
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }
        else
        {
            startInfo.FileName = executable;
        }
        startInfo.ArgumentList.Add("--client");
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(supervisorProcessId.ToString(CultureInfo.InvariantCulture));
        if (layoutMonitorIndex is int index)
        {
            startInfo.ArgumentList.Add("--layout-monitor");
            startInfo.ArgumentList.Add(index.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(monitorDeviceName))
            {
                startInfo.ArgumentList.Add("--monitor-device");
                startInfo.ArgumentList.Add(monitorDeviceName);
            }
        }
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动画面进程");
    }
}
