using System.IO.Compression;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Scada.Collector;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine;
using Scada.Server.Ai;
using Scada.Server.External;
using Scada.Server.External.Rest;
using Scada.Server.Hubs;
using Scada.Server.Licensing;
using Scada.Server.Mcp;
using Scada.Server.Services;

namespace Scada.Server.Startup;

// 业务服务组合根：数据、采集、引擎、网关、对外接口、MCP/AI、实时推送与后台任务。
// 认证/鉴权/JWT/CORS 在 ScadaSecurityServices；二者都从 Program.cs 调用一次。
internal static class ScadaServerServices
{
    public static void AddScadaServer(this WebApplicationBuilder builder)
    {
        builder.Services.AddScadaData(builder.Configuration);
        // 授权：试用 365 天。过期后网页接口由 LicenseGateMiddleware 拒绝；下发看 ILicenseEnforcement.OutputsAllowed。采集和历史继续。
        builder.Services.Configure<Scada.Server.Licensing.LicenseOptions>(builder.Configuration.GetSection("Scada:License"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Scada.Server.Licensing.LicenseService>();
        builder.Services.AddSingleton<Scada.Core.Licensing.ILicenseEnforcement>(sp => sp.GetRequiredService<Scada.Server.Licensing.LicenseService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.Licensing.LicenseService>());
        builder.Services.AddOptions<ScadaSettings>()
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.SiteName) && settings.SiteName.Trim().Length <= 120,
                "Scada:SiteName must contain 1 to 120 characters.")
            .ValidateOnStart();
        // 主备：协调器同时是节点角色（INodeRole），采集、控制、通知、定时任务据此判断是否工作。未启用时恒为主机。
        builder.Services.AddOptions<Scada.Server.Ha.HaOptions>().Bind(builder.Configuration.GetSection("Ha"));
        builder.Services.AddSingleton<Scada.Server.Ha.HaReplication>();
        builder.Services.AddSingleton<Scada.Server.Ha.HaCoordinator>();
        builder.Services.AddSingleton<INodeRole>(sp => sp.GetRequiredService<Scada.Server.Ha.HaCoordinator>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.Ha.HaCoordinator>());
        builder.Services.AddScadaCollector(builder.Configuration);
        builder.Services.AddSingleton<OpcCatalogService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OpcCatalogService>());
        builder.Services.AddScadaEngine();
        builder.Services.AddSingleton<Scada.Server.Services.HealthSnapshotService>();
        builder.Services.AddOptions<GatewaySettings>().Bind(builder.Configuration.GetSection("Gateway"))
            .Validate(x => x.LeaseSeconds is >= 15 and <= 120 && x.MaxInboxPendingBytes >= 1024 && x.CommandTimeoutMs is >= 100 and <= 120000
                && x.DeviceOfflineDelayMs is >= 0 and <= 300000 && x.DeviceStateGraceMs is >= 1000 and <= 300000
                && x.MaxInboxAttempts is >= 1 and <= 100 && x.InboxRetryBaseMs is >= 100 and <= 60000
                && x.InboxRetryMaxMs >= x.InboxRetryBaseMs && x.InboxRetryMaxMs <= 3600000,
                "网关租约须为 15～120 秒，收件箱限额至少 1024 字节，命令期限须为 100～120000 毫秒；离线延时须为 0～300000 毫秒，状态宽限须为 1000～300000 毫秒；收件箱最大尝试次数须为 1～100，重试基数须为 100～60000 毫秒且不高于重试上限（不超过 3600000 毫秒）").ValidateOnStart();
        builder.Services.AddSingleton<GatewayCenterService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayCenterService>());
        builder.Services.AddSingleton<LocalCommandLedger>();
        builder.Services.AddSingleton<ICollectorControl, GatewayCollectorCoordinator>();

        builder.Services.AddOptions<Scada.Core.Options.ExternalIntegrationOptions>()
            .Bind(builder.Configuration.GetSection("External"))
            .ValidateOnStart();
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Scada.Core.Options.ExternalIntegrationOptions>,
            Scada.Server.External.ExternalIntegrationOptionsValidator>();
        builder.Services.AddSingleton<ExternalApiKeyService>();
        builder.Services.AddSingleton<Scada.Server.External.Tcp.ExternalTcpHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.External.Tcp.ExternalTcpHost>());
        builder.Services.AddSingleton<Scada.Server.External.OpcUa.ExternalOpcUaHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.External.OpcUa.ExternalOpcUaHost>());
        // 反向下行：对外 Modbus TCP 从站与 IEC 104 从站。两者共用点表映射与同一条控制写回闭环。
        builder.Services.AddSingleton<Scada.Server.External.Slave.ExportPointMap>();
        builder.Services.AddSingleton<Scada.Server.External.Slave.ExportWriteRouter>();
        builder.Services.AddSingleton<Scada.Server.External.Slave.ModbusSlaveHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.External.Slave.ModbusSlaveHost>());
        builder.Services.AddSingleton<Scada.Server.External.Slave.Iec104ServerHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.External.Slave.Iec104ServerHost>());

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddScadaMcp(builder.Configuration);
        // AI 只读 API：供大模型用 Function Calling 直接调用，与 MCP 共用数据与权限模型。
        builder.Services.Configure<Scada.Server.Ai.AiOptions>(builder.Configuration.GetSection("Scada:Ai"));
        builder.Services.AddOpenApi("v1", o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new() { Title = "Scada External API", Version = "v1", Description = "Stable external integration contract under /api/v1. Internal UI uses /api/*." };
            return Task.CompletedTask;
        }));
        builder.Services.AddOpenApi("ai", o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new() { Title = "Scada AI Read API", Version = "v1", Description = "Read-only endpoints under /api/ai for large-model tool calling. No endpoint in this group can change system state." };
            return Task.CompletedTask;
        }));

        builder.Services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment())
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
            });

        builder.Services.AddSingleton<SignalRAlarmNotifier>();
        builder.Services.AddSingleton<ISecretProtector, NotificationSecrets>();
        builder.Services.AddSingleton<NotificationHttp>();
        builder.Services.AddSingleton<Scada.Engine.Notifications.INotificationSender, Scada.Engine.Notifications.EmailNotificationSender>();
        builder.Services.AddSingleton<Scada.Engine.Notifications.INotificationSender>(sp => new Scada.Engine.Notifications.WebhookNotificationSender(sp.GetRequiredService<NotificationHttp>().Client));
        builder.Services.AddSingleton<Scada.Engine.Notifications.INotificationSender>(sp => new Scada.Engine.Notifications.SmsHttpNotificationSender(sp.GetRequiredService<NotificationHttp>().Client));
        builder.Services.AddSingleton<Scada.Engine.Notifications.AlarmNotificationDispatcher>(sp => new Scada.Engine.Notifications.AlarmNotificationDispatcher(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), sp.GetRequiredService<IDbContextFactory<RuntimeDbContext>>(),
            sp.GetRequiredService<AccessModelProvider>(), sp.GetRequiredService<ISecretProtector>(),
            sp.GetServices<Scada.Engine.Notifications.INotificationSender>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ScadaSettings>>(),
            sp.GetRequiredService<ILogger<Scada.Engine.Notifications.AlarmNotificationDispatcher>>()) { IsActive = () => sp.GetRequiredService<INodeRole>().IsActive });
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Engine.Notifications.AlarmNotificationDispatcher>());
        builder.Services.AddSingleton<IAlarmNotifier>(sp => new CompositeAlarmNotifier(
            new IAlarmNotifier[]
            {
                sp.GetRequiredService<SignalRAlarmNotifier>(),
                sp.GetRequiredService<Scada.Engine.Notifications.AlarmWebhookNotifier>(),
                sp.GetRequiredService<Scada.Engine.Notifications.AlarmNotificationDispatcher>(),
                sp.GetRequiredService<Scada.Engine.Alarms.AnnunciatorService>()
            },
            sp.GetRequiredService<ILogger<CompositeAlarmNotifier>>()));
        builder.Services.AddHostedService<SignalRRealtimeBroadcaster>();
        builder.Services.AddSingleton<ConfigReloader>();
        builder.Services.AddSingleton<Scada.Engine.Calculations.ICalcTagWriter, Scada.Engine.Calculations.ControlledCalcTagWriter>();
        builder.Services.Configure<Scada.Engine.Calculations.CSharpScriptOptions>(builder.Configuration.GetSection("Scada:CSharpScripts"));
        builder.Services.AddSingleton<Scada.Engine.Calculations.SerialProfileCatalog>();
        builder.Services.AddSingleton<Scada.Engine.Calculations.ISerialProfileCatalog>(sp => sp.GetRequiredService<Scada.Engine.Calculations.SerialProfileCatalog>());
        builder.Services.AddSingleton<Scada.Engine.Calculations.SerialProfileTelemetry>();
        builder.Services.AddSingleton<Scada.Engine.Calculations.ICSharpScriptRunner, Scada.Engine.Calculations.CSharpScriptHost>();
        builder.Services.AddSingleton<Scada.Engine.Calculations.ICSharpScriptLicense>(sp => new Scada.Server.Licensing.CSharpScriptLicenseGate(sp.GetRequiredService<Scada.Server.Licensing.LicenseService>()));
        builder.Services.AddSingleton<Scada.Engine.Calculations.CalcJobEngine>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Engine.Calculations.CalcJobEngine>());
        builder.Services.AddSingleton<Scada.Engine.Calculations.CalcJobConfiguration>();
        builder.Services.AddSingleton<Scada.Engine.Reports.ReportBuilder>();
        builder.Services.AddSingleton<Scada.Server.Projects.ProjectService>();
        builder.Services.AddSingleton<Scada.Server.Reports.ReportSigner>();
        builder.Services.AddSingleton<Scada.Server.Reports.ReportArchiveService>();
        builder.Services.AddSingleton<Scada.Server.Reports.ReportScheduler>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.Reports.ReportScheduler>());
        builder.Services.AddSingleton<TagExcelService>();
        builder.Services.AddSingleton<AlarmRuleExcelService>();
        // 测点/设备写入的唯一落地点：REST 与 MCP 共用同一套校验顺序与落盘顺序。
        builder.Services.AddSingleton<TagCommandService>();
        builder.Services.AddSingleton<DeviceCommandService>();
        builder.Services.AddSingleton<Scada.Engine.Templates.ObjectTemplateService>();

        // 首屏 JS/CSS 与历史/报警等大 JSON 走 Brotli/Gzip；Hashed 静态资源可长期缓存。
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
            {
                "application/manifest+json",
                "image/svg+xml"
            });
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
        builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
    }
}
