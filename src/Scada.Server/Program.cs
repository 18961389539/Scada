using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.IdentityModel.Tokens;
using Scada.Collector;
using Scada.Collector.Opc;
using Scada.Core.Interfaces;
using Scada.Data;
using Scada.Engine;
using Scada.Server.Auth;
using Scada.Server.Endpoints;
using Scada.Server.Hubs;
using Scada.Server.Services;
using Serilog;
using Serilog.Events;
using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Options;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
// 站点配置文件（Windows 服务安装脚本写在数据目录，升级替换程序目录时不受影响）；环境变量与命令行仍优先。
if (builder.Configuration["SiteSettings"] is { Length: > 0 } siteSettings)
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(siteSettings), optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

builder.Host.UseWindowsService();
builder.Host.UseSystemd();
// 日志轮转：按天滚动，单文件超过上限也滚动；保留最近 N 个文件（含按大小拆出的文件）。目录可改到数据盘。
builder.Host.UseSerilog((context, _, configuration) => { var log = context.Configuration.GetSection("Scada").Get<ScadaSettings>() ?? new ScadaSettings(); configuration
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

builder.Services.AddScadaData(builder.Configuration);
// 主备：协调器同时是节点角色（INodeRole），采集、控制、通知、定时任务据此判断是否工作。未启用时恒为主机。
builder.Services.AddOptions<Scada.Server.Ha.HaOptions>().Bind(builder.Configuration.GetSection("Ha"));
builder.Services.AddSingleton<Scada.Server.Ha.HaReplication>();
builder.Services.AddSingleton<Scada.Server.Ha.HaCoordinator>();
builder.Services.AddSingleton<INodeRole>(sp => sp.GetRequiredService<Scada.Server.Ha.HaCoordinator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.Ha.HaCoordinator>());
builder.Services.AddScadaCollector(builder.Configuration);
var credentialSettings = builder.Configuration.GetSection("Scada").Get<ScadaSettings>() ?? new ScadaSettings();
var credentialKeys = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(credentialSettings.ConfigDbPath))!, "keys"));
var protection = builder.Services.AddDataProtection().SetApplicationName("Scada").PersistKeysToFileSystem(credentialKeys);
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<IDeviceCredentials, DeviceCredentials>();
builder.Services.AddSingleton<OpcCertificateService>();
builder.Services.AddSingleton<OpcCatalogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OpcCatalogService>());
builder.Services.AddScadaEngine();
builder.Services.AddOptions<GatewaySettings>().Bind(builder.Configuration.GetSection("Gateway"))
    .Validate(x => x.LeaseSeconds is >= 15 and <= 120 && x.MaxInboxPendingBytes >= 1024 && x.CommandTimeoutMs is >= 100 and <= 120000
        && x.DeviceOfflineDelayMs is >= 0 and <= 300000 && x.DeviceStateGraceMs is >= 1000 and <= 300000,
        "网关租约须为 15～120 秒，收件箱限额至少 1024 字节，命令期限须为 100～120000 毫秒；离线延时须为 0～300000 毫秒，状态宽限须为 1000～300000 毫秒").ValidateOnStart();
builder.Services.AddSingleton<GatewayCenterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayCenterService>());
builder.Services.AddSingleton<ICollectorControl, GatewayCollectorCoordinator>();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var jwtKeyIsTemporary = false;
if (string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("未配置 Jwt:Key，请通过环境变量 Jwt__Key 提供签名密钥");
    }

    // 仓库里不保留固定密钥，开发环境改用临时密钥；代价是重启后已签发的令牌失效。
    jwtOptions.Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    builder.Services.Configure<JwtOptions>(options => options.Key = jwtOptions.Key);
    jwtKeyIsTemporary = true;
}
else if (Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
{
    throw new InvalidOperationException("Jwt:Key 长度不足，签名密钥至少需要 32 字节");
}

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var id = context.Principal?.GetUserId();
                var factory = context.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
                await using var db = await factory.CreateDbContextAsync(context.HttpContext.RequestAborted);
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, context.HttpContext.RequestAborted);
                if (user is null || !user.Enabled || context.Principal?.FindFirstValue(ClaimTypes.Role) != user.Role.ToString()
                    || context.Principal?.FindFirstValue("credentialsVersion") != JwtTokenService.CredentialsVersion(user))
                    context.Fail("登录状态已变更，请重新登录");
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    }).AddScheme<AuthenticationSchemeOptions, GatewayAuthenticationHandler>(GatewayAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("GatewayAgent", policy => policy.AddAuthenticationSchemes(GatewayAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireClaim("gateway-id"));
    AccessPolicies.Register(options);
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment())
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
    });

builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();
builder.Services.AddSingleton<RealtimeSubscriptionStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddHostedService<SessionReaper>();
builder.Services.AddSingleton<SignalRAlarmNotifier>();
builder.Services.AddSingleton<ISecretProtector, NotificationSecrets>();
builder.Services.AddSingleton<NotificationHttp>();
builder.Services.AddSingleton<Scada.Engine.Notifications.INotificationSender, Scada.Engine.Notifications.EmailNotificationSender>();
builder.Services.AddSingleton<Scada.Engine.Notifications.INotificationSender>(sp => new Scada.Engine.Notifications.WebhookNotificationSender(sp.GetRequiredService<NotificationHttp>().Client));
builder.Services.AddSingleton<Scada.Engine.Notifications.INotificationSender>(sp => new Scada.Engine.Notifications.SmsHttpNotificationSender(sp.GetRequiredService<NotificationHttp>().Client));
builder.Services.AddSingleton<Scada.Engine.Notifications.AlarmNotificationDispatcher>(sp => new Scada.Engine.Notifications.AlarmNotificationDispatcher(
    sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), sp.GetRequiredService<AccessModelProvider>(), sp.GetRequiredService<ISecretProtector>(),
    sp.GetServices<Scada.Engine.Notifications.INotificationSender>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ScadaSettings>>(),
    sp.GetRequiredService<ILogger<Scada.Engine.Notifications.AlarmNotificationDispatcher>>()) { IsActive = () => sp.GetRequiredService<INodeRole>().IsActive });
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Engine.Notifications.AlarmNotificationDispatcher>());
builder.Services.AddSingleton<IAlarmNotifier>(sp => new CompositeAlarmNotifier(
    new IAlarmNotifier[]
    {
        sp.GetRequiredService<SignalRAlarmNotifier>(),
        sp.GetRequiredService<Scada.Engine.Notifications.AlarmWebhookNotifier>(),
        sp.GetRequiredService<Scada.Engine.Notifications.AlarmNotificationDispatcher>()
    },
    sp.GetRequiredService<ILogger<CompositeAlarmNotifier>>()));
builder.Services.AddHostedService<SignalRRealtimeBroadcaster>();
builder.Services.AddSingleton<ConfigReloader>();
builder.Services.AddSingleton<Scada.Engine.Calculations.ICalcTagWriter, Scada.Engine.Calculations.ControlledCalcTagWriter>();
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
builder.Services.AddSingleton<Scada.Engine.Templates.ObjectTemplateService>();

var app = builder.Build();

if (jwtKeyIsTemporary)
{
    app.Logger.LogWarning("Jwt:Key 未配置，本次使用临时签名密钥，重启后已签发的令牌全部失效");
}

await DatabaseInitializer.InitializeAsync(app.Services);

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseCors("web");
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<Scada.Server.Ha.HaStandbyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapGatewayEndpoints();
app.MapCollectorDiagnosticEndpoints();
app.MapOpcEndpoints();
app.MapTagEndpoints();
app.MapCalculatedTagEndpoints();
app.MapCalcJobEndpoints();
app.MapReportTemplateEndpoints();
app.MapProjectEndpoints();
app.MapAlarmRuleEndpoints();
app.MapRealtimeEndpoints();
app.MapHistoryEndpoints();
app.MapAlarmEndpoints();
app.MapAlarmShelveEndpoints();
app.MapNotificationEndpoints();
app.MapCommandEndpoints();
app.MapControlEndpoints();
app.MapRecipeEndpoints();
app.MapReportEndpoints();
app.MapLogEndpoints();
app.MapUserEndpoints();
app.MapAccessEndpoints();
app.MapScreenEndpoints();
app.MapObjectTemplateEndpoints();
app.MapHealthEndpoints();
app.MapMetricsEndpoints();
app.MapMaintenanceEndpoints();
Scada.Server.Ha.HaEndpoints.MapHaEndpoints(app);
app.MapHub<RealtimeHub>("/hubs/realtime");

var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(webRoot, "index.html")))
{
    // SPA 路由匿名可达，未知 API 仍返回 404。
    app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
    app.MapFallbackToFile("index.html").AllowAnonymous();
}

app.Run();
public partial class Program;
