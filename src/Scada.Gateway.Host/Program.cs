using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Scada.Gateway;
using Scada.Gateway.Hosting;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
GatewayAgentOptions options;
bool validateOnly;
try { options = GatewayAgentConfiguration.Read(builder.Configuration); validateOnly = GatewayAgentConfiguration.ValidationRequested(args); }
catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or System.Text.Json.JsonException)
{ Console.Error.WriteLine("网关配置无效：请检查 GatewayConfig 文件及 GatewayAgent 的中心地址、编号、密钥和目录参数。"); return 1; }
if (validateOnly)
{
    Console.WriteLine($"网关配置校验通过：编号 {options.GatewayId}，服务 {options.ServiceName}；未启动采集。"); return 0;
}
Directory.CreateDirectory(options.DataRoot);
builder.Services.AddWindowsService(x => x.ServiceName = options.ServiceName);
builder.Services.Configure<HostOptions>(x => x.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(options);
var protection = builder.Services.AddDataProtection().SetApplicationName("Scada.Gateway")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataRoot, "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton(_ => new GatewayAgentStore(Path.Combine(options.DataRoot, "gateway.db"), options.MaxPendingBytes));
builder.Services.AddSingleton<GatewayConfigurationCache>();
builder.Services.AddSingleton<IGatewayAcquisitionRuntime, GatewayAcquisitionRuntime>();
builder.Services.AddSingleton<GatewayAgentClient>();
builder.Services.AddSingleton<GatewayCommandRunner>();
builder.Services.AddSingleton<GatewayEngineeringRunner>();
builder.Services.AddHostedService<GatewayAgentService>();
var host = builder.Build();
var hostLifetime = host.Services.GetRequiredService<IHostLifetime>();
using var stopRegistration = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
{
    // A caught fatal error stops the host cleanly, but SCM still needs a failure
    // status to apply its configured non-crash recovery policy.
    if (OperatingSystem.IsWindows() && hostLifetime is Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceLifetime service)
        service.ExitCode = Environment.ExitCode;
});
await host.RunAsync();
return Environment.ExitCode;
