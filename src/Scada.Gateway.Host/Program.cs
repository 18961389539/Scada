using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Scada.Gateway;
using Scada.Gateway.Hosting;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
var options = new GatewayAgentOptions();
builder.Configuration.GetSection("GatewayAgent").Bind(options);
try { options.Validate(); }
catch (ArgumentException) { Console.Error.WriteLine("网关配置无效：请检查 GatewayAgent 的中心地址、编号、密钥和目录参数。"); return 1; }
Directory.CreateDirectory(options.DataRoot);
builder.Services.AddWindowsService(x => x.ServiceName = "ScadaGateway");
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
await builder.Build().RunAsync();
return Environment.ExitCode;
