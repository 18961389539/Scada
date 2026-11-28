using Scada.Desktop.App;
using Scada.Desktop.Configuration;
using Scada.Desktop.Runtime;
using Scada.Desktop.Ui;

namespace Scada.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var paths = new DesktopPaths();
        try
        {
            paths.EnsureCreated();
            DesktopLogger.Initialize(paths.LogsDirectory);
        }
        catch
        {
            // 数据目录不可写时仍尽量启动，由后续配置/日志错误给出提示。
        }
        WireExceptionHandlers();
        DesktopLogger.Info($"客户端启动：{Environment.ProcessPath}");

        if (ProvisionCommand.IsRequested(args)) return ProvisionCommand.Run(args, paths);

        var isClient = args.Any(arg => string.Equals(arg, "--client", StringComparison.OrdinalIgnoreCase));
        return isClient ? RunClient(paths, args) : RunSupervisor(paths);
    }

    private static int RunSupervisor(DesktopPaths paths)
    {
        using var single = new SingleInstance(@"Local\Scada.Desktop.Supervisor");
        if (!single.IsOwner)
        {
            MessageBox.Show("SCADA 桌面客户端已在运行。", "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return ExitCodes.Success;
        }

        var store = new SettingsStore(paths.SettingsFile);
        var machineStore = new MachineSettingsStore(paths.MachineSettingsFile);
        var user = store.Load(out var userError);
        var machine = machineStore.Load(out var machineError);
        var managedAutostart = InstallManifestStore.IsManagedAutostart(paths);
        var resolved = DesktopConfigResolver.Resolve(user, machine, managedAutostart);
        var settings = resolved?.Settings;
        if (settings is null || resolved?.Error is not null || user is null)
        {
            try
            {
                DesktopLogger.Info("显示配置窗口");
                using var form = new SettingsForm(paths, store, settings, resolved?.Error ?? userError ?? machineError, machine);
                var result = form.ShowDialog();
                DesktopLogger.Info($"配置窗口返回：{result}");
                if (result != DialogResult.OK || form.SavedSettings is null) return ExitCodes.Success;
                settings = form.SavedSettings;
            }
            catch (Exception exception)
            {
                DesktopLogger.Error("配置窗口失败", exception);
                MessageBox.Show("无法打开桌面客户端设置：" + exception.Message, "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return ExitCodes.Configuration;
            }
        }

        if (settings is null) return ExitCodes.Configuration;
        ClientIdentity.Ensure(settings, store);
        AutostartStartup.Apply(settings);

        using var context = new SupervisorContext(paths, store, settings);
        Application.Run(context);
        return context.ExitCode;
    }

    private static int RunClient(DesktopPaths paths, string[] args)
    {
        var launch = ClientLaunchOptions.Parse(args);
        using var single = new SingleInstance(launch.MutexName);
        if (!single.IsOwner) return ExitCodes.Success;

        var store = new SettingsStore(paths.SettingsFile);
        var machine = new MachineSettingsStore(paths.MachineSettingsFile).Load(out var machineError);
        var managedAutostart = InstallManifestStore.IsManagedAutostart(paths);
        var user = store.Load(out var userError);
        var resolved = DesktopConfigResolver.Resolve(user, machine, managedAutostart);
        if (resolved is null || resolved.Error is not null)
        {
            var error = resolved?.Error ?? userError ?? machineError ?? "桌面客户端配置无效";
            DesktopLogger.Error("画面进程启动失败：" + error);
            MessageBox.Show(error, "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return ExitCodes.Configuration;
        }
        var settings = resolved.Settings;
        ClientIdentity.Ensure(settings, store);
        launch.Apply(settings);

        try
        {
            using var form = new ClientForm(settings, paths, store, launch.ParentProcessId);
            Application.Run(form);
            return form.ResultCode;
        }
        catch (Exception exception)
        {
            DesktopLogger.Error("画面进程启动失败", exception);
            MessageBox.Show("画面进程启动失败：" + exception.Message, "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return ExitCodes.Configuration;
        }
    }

    private static void WireExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            DesktopLogger.Error("界面线程未处理异常", e.Exception);
            Environment.Exit(ExitCodes.Crash);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DesktopLogger.Error("进程未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DesktopLogger.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }
}
