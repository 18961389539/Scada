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

        var isClient = args.Any(arg => string.Equals(arg, "--client", StringComparison.OrdinalIgnoreCase));
        var parentProcessId = ParseParentProcessId(args);
        return isClient ? RunClient(paths, parentProcessId) : RunSupervisor(paths);
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
        var settings = store.Load(out var error);
        if (settings is null)
        {
            try
            {
                DesktopLogger.Info("显示首次配置窗口");
                using var form = new SettingsForm(paths, store, null, error);
                var result = form.ShowDialog();
                DesktopLogger.Info($"首次配置窗口返回：{result}");
                if (result != DialogResult.OK || form.SavedSettings is null) return ExitCodes.Success;
                settings = form.SavedSettings;
            }
            catch (Exception exception)
            {
                DesktopLogger.Error("首次配置窗口失败", exception);
                MessageBox.Show("无法打开桌面客户端设置：" + exception.Message, "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return ExitCodes.Configuration;
            }
        }

        using var context = new SupervisorContext(paths, store, settings);
        Application.Run(context);
        return context.ExitCode;
    }

    private static int RunClient(DesktopPaths paths, int parentProcessId)
    {
        using var single = new SingleInstance(@"Local\Scada.Desktop.Client");
        if (!single.IsOwner) return ExitCodes.Success;

        var store = new SettingsStore(paths.SettingsFile);
        var settings = store.Load(out var error);
        if (settings is null)
        {
            DesktopLogger.Error("画面进程启动失败：" + (error ?? "配置无效"));
            MessageBox.Show(error ?? "桌面客户端配置无效，请重新配置。", "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return ExitCodes.Configuration;
        }

        var validation = SettingsValidator.Validate(settings, requirePassword: false);
        if (validation is not null)
        {
            DesktopLogger.Error("画面进程配置校验失败：" + validation);
            MessageBox.Show(validation, "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return ExitCodes.Configuration;
        }

        try
        {
            using var form = new ClientForm(settings, paths, parentProcessId);
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

    private static int ParseParentProcessId(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--parent", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var pid) && pid > 0)
            {
                return pid;
            }
        }
        return 0;
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
