using System.Diagnostics;
using Serilog;
using Serilog.Events;
using Scada.Core.Options;
using Scada.Data;
using Scada.Server.Services;
using Scada.Server.Startup;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = StartupPaths.ResolveWebRoot(args)
});
// 启动时打印实际静态文件根：部署与 E2E 最容易在这里用错目录（陈旧的 wwwroot 副本）。
Console.WriteLine($"[startup] webroot={builder.Environment.WebRootPath ?? "(null)"} contentRoot={builder.Environment.ContentRootPath}");
var dataRoot = StartupPaths.ResolveDataRoot(builder.Configuration);
Console.WriteLine($"[startup] dataRoot={dataRoot}");
// 站点配置文件（显式指定或首次运行自举）；环境变量与命令行仍优先。
var siteSettingsPath = StartupPaths.ResolveSiteSettings(builder.Configuration, dataRoot, builder.Environment);
if (siteSettingsPath is not null)
{
    builder.Configuration.AddJsonFile(siteSettingsPath, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}
StartupPaths.ApplyDataRoot(builder.Configuration, dataRoot);

// 配置只在这里绑定一次，后续消费方（日志目录、凭据密钥、启动选项、对外接口策略）共用同一份快照，
// 避免同一个 section 被反复 Get<T>() 绑定时各处出现不一致的默认值。
var scadaOptions = builder.Configuration.GetSection("Scada").Get<ScadaSettings>() ?? new ScadaSettings();
var externalRest = builder.Configuration.GetSection("External:Rest").Get<ExternalRestOptions>() ?? new ExternalRestOptions();

// 单实例：同一数据目录只允许一个进程，双击第二次启动时立刻退出并提示。
// 开发环境不启用：dotnet run、E2E 与集成测试会并行/反复启动宿主，端口与临时目录已隔开。
using var instanceLock = builder.Environment.IsDevelopment() ? null : SingleInstanceGuard.TryAcquire(dataRoot);
if (!builder.Environment.IsDevelopment() && instanceLock is null)
{
    var message = $"SCADA 已在运行：数据目录 {dataRoot} 已被另一个实例占用。";
    // 服务模式没有交互桌面，弹窗会被跳过，日志是唯一线索：写在 Serilog 同一目录的 startup.log。
    try
    {
        var logRoot = string.IsNullOrWhiteSpace(scadaOptions.LogRoot)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.GetFullPath(scadaOptions.LogRoot);
        Directory.CreateDirectory(logRoot);
        File.AppendAllText(Path.Combine(logRoot, "startup.log"),
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [WRN] {message}（本进程 PID {Environment.ProcessId} 退出启动）{Environment.NewLine}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    Console.WriteLine($"[startup] {message}");
    StartupErrorDialog.Show(
        $"{message}\n\n请从任务栏通知区域的托盘图标打开，或直接访问 http://localhost:5080；" +
        "如确需重新启动，请先退出正在运行的实例。",
        "SCADA 已在运行");
    Environment.ExitCode = 1;
    return;
}

builder.Host.UseWindowsService();
builder.Host.UseSystemd();
// 日志轮转：按天滚动，单文件超过上限也滚动；保留最近 N 个文件（含按大小拆出的文件）。目录可改到数据盘。
builder.Host.UseSerilog((context, _, configuration) => { var log = scadaOptions; configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(string.IsNullOrWhiteSpace(log.LogRoot) ? Path.Combine(AppContext.BaseDirectory, "logs") : Path.GetFullPath(log.LogRoot), "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: Math.Clamp(log.LogRetainedFiles, 1, 3650),
        fileSizeLimitBytes: Math.Clamp(log.LogFileSizeLimitMb, 1, 10240) * 1024L * 1024L,
        rollOnFileSizeLimit: true,
        shared: true); });

builder.AddScadaServer();
var security = builder.AddScadaSecurity(scadaOptions, externalRest);

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

app.UseScadaApi(externalRest, security.TemporaryKey);

// 桌面交互运行（双击发布版、dotnet run 调试）没有窗口可循：就绪后按需打开浏览器，并保留托盘入口。
// 服务模式与无交互会话不创建托盘；自动化/测试用 SCADA_NO_TRAY=1 关闭。
// 浏览器自动打开另由 Scada:OpenBrowserOnStart、SCADA_NO_BROWSER=1 控制（开发环境默认不自动打开）。
var startupSettings = scadaOptions;
var openBrowser = startupSettings.OpenBrowserOnStart ?? !builder.Environment.IsDevelopment();
var isWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var interactiveBrowser = openBrowser && OperatingSystem.IsWindows() && Environment.UserInteractive
    && Environment.GetEnvironmentVariable("SCADA_NO_BROWSER") != "1" && !isWindowsService;
var trayEnabled = OperatingSystem.IsWindows() && Environment.UserInteractive
    && Environment.GetEnvironmentVariable("SCADA_NO_TRAY") != "1" && !isWindowsService;
TrayIcon? tray = null;
if (interactiveBrowser || trayEnabled)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var urls = app.Urls.Count > 0
            ? app.Urls
            : new[] { app.Configuration["Kestrel:Endpoints:Http:Url"] ?? "http://localhost:5080" };
        var url = urls
            .Where(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || x.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Replace("0.0.0.0", "localhost", StringComparison.Ordinal).Replace("[::]", "localhost", StringComparison.Ordinal).TrimEnd('/'))
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url)) return;
        if (interactiveBrowser)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { app.Logger.LogWarning(ex, "自动打开浏览器失败：{Url}", url); }
        }
        if (trayEnabled)
        {
            try
            {
                tray = TrayIcon.Start(url, TrayIcon.ComposeTip(scadaOptions.SiteName, url), app.Configuration["Scada:LogRoot"] ?? Path.Combine(dataRoot, "logs"),
                    Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "favicon.ico"), app.Lifetime.StopApplication, app.Logger);
            }
            catch (Exception) { app.Logger.LogWarning("托盘图标启动失败"); }
        }
    });
    app.Lifetime.ApplicationStopping.Register(() => tray?.Dispose());
}

try
{
    app.Run();
}
catch (Exception ex)
{
    // 双击（WinExe）时没有控制台，失败必须落到日志并弹窗；服务模式只写日志，交给恢复策略拉起。
    // 写日志本身也可能抛（日志目录不可写），此时只剩下面的弹窗通道，因此同样要吞掉。
    try { app.Logger.LogCritical(ex, "SCADA 启动失败"); } catch (Exception) { }
    // 弹窗文案不经过 HTTP 响应，单独组装，避免被本地化覆盖率测试误认为接口提示。
    var startupText = $"{ex.Message}\n\n日志目录：{Path.Combine(dataRoot, "logs")}\n\n" +
        "常见原因：端口 5080 被占用、站点配置无效、数据目录无写权限。\n" +
        "修改端口可在启动时加 --urls http://0.0.0.0:5081，或调整 data\appsettings.Site.json。";
    StartupErrorDialog.Show(startupText);
    Environment.ExitCode = 1;
}
public partial class Program;
