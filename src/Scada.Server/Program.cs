using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.IO.Compression;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using Scada.Collector;
using Scada.Collector.Opc;
using Scada.Core.Interfaces;
using Scada.Data;
using Scada.Engine;
using Scada.Server.Auth;
using Scada.Server.Endpoints;
using Scada.Server.Hubs;
using Scada.Server.Licensing;
using Scada.Server.Services;
using Scada.Server.External;
using Scada.Server.External.Rest;
using Serilog;
using Serilog.Events;
using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Options;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

// 静态文件根：WebApplicationBuilder 既解析不了 --webroot，也不支持建好之后再改 WebRootPath，
// 所以在创建 builder 前先取出来交给 WebApplicationOptions。这样 E2E 能直接指向 web/dist，
// 不会误用程序目录下可能陈旧的 wwwroot 副本。
static string? ResolveWebRoot(string[] args)
{
    const string prefix = "--webroot=";
    foreach (var arg in args)
    {
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && arg.Length > prefix.Length)
        {
            var path = Path.GetFullPath(arg[prefix.Length..]);
            if (Directory.Exists(path)) return path;
        }
    }
    var fromEnv = Environment.GetEnvironmentVariable("ASPNETCORE_WEBROOT");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        var path = Path.GetFullPath(fromEnv);
        if (Directory.Exists(path)) return path;
    }
    var bundled = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    return Directory.Exists(bundled) ? bundled : null;
}

// 数据根：便携部署落在程序目录 data/，与进程工作目录解耦（Windows 服务的 CWD 是 System32，双击则是程序目录）。
// 顺序：Scada:DataRoot → SCADA_DATA_ROOT → 程序目录 data/；程序目录不可写时回退公共应用数据目录。
static string ResolveDataRoot(IConfiguration configuration)
{
    var configured = configuration["Scada:DataRoot"];
    if (string.IsNullOrWhiteSpace(configured))
    {
        configured = Environment.GetEnvironmentVariable("SCADA_DATA_ROOT");
    }
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured);
    }

    var portable = Path.Combine(AppContext.BaseDirectory, "data");
    try
    {
        Directory.CreateDirectory(portable);
        return portable;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        var fallback = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Scada", "data")
            : "/var/lib/scada";
        Directory.CreateDirectory(fallback);
        Console.WriteLine($"[startup] 程序目录不可写，数据改存 {fallback}（原因：{ex.Message}）");
        return fallback;
    }
}

// 站点配置：显式 --SiteSettings 优先；其次程序目录、数据目录下已有的 appsettings.Site.json；
// 生产环境首次运行自动生成（含随机 JWT 密钥），让“拷贝文件夹后双击 exe”可以直接启动。
static string? ResolveSiteSettings(IConfiguration configuration, string dataRoot, IWebHostEnvironment environment)
{
    var explicitPath = configuration["SiteSettings"];
    if (!string.IsNullOrWhiteSpace(explicitPath))
    {
        return Path.GetFullPath(explicitPath);
    }

    foreach (var candidate in new[]
             {
                 Path.Combine(AppContext.BaseDirectory, "appsettings.Site.json"),
                 Path.Combine(dataRoot, "appsettings.Site.json")
             })
    {
        if (File.Exists(candidate)) return candidate;
    }

    return environment.IsDevelopment() ? null : CreatePortableSiteSettings(dataRoot);
}

static string CreatePortableSiteSettings(string dataRoot)
{
    var path = Path.Combine(dataRoot, "appsettings.Site.json");
    Directory.CreateDirectory(dataRoot);
    if (File.Exists(path)) return path;

    var payload = new Dictionary<string, object>
    {
        ["Scada"] = new Dictionary<string, object>
        {
            ["DataRoot"] = dataRoot,
            ["ConfigDbPath"] = Path.Combine(dataRoot, "scada.db"),
            ["HistoryRoot"] = Path.Combine(dataRoot, "history"),
            ["BackupRoot"] = Path.Combine(dataRoot, "backups"),
            ["DiagnosticsRoot"] = Path.Combine(dataRoot, "diagnostics"),
            ["ReportsRoot"] = Path.Combine(dataRoot, "reports"),
            ["LogRoot"] = Path.Combine(dataRoot, "logs")
        },
        ["Jwt"] = new Dictionary<string, object>
        {
            ["Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        }
    };
    var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        // 站点配置是给人看的运维文件：中文路径与 Base64 密钥保持原样，不转成 \uXXXX。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
    // 先写临时文件再原子改名：两个进程同时首次启动时不会读到写了一半的配置。
    var temp = Path.Combine(dataRoot, $".appsettings.Site.{Environment.ProcessId}.tmp");
    File.WriteAllText(temp, json, new UTF8Encoding(false));
    try
    {
        File.Move(temp, path, overwrite: false);
        Console.WriteLine($"[startup] 已生成便携站点配置 {path}（含随机 JWT 密钥，请与数据目录一起备份）");
    }
    catch (IOException) when (File.Exists(path))
    {
        File.Delete(temp);
    }
    return path;
}

// 站点配置里的相对路径统一以数据根为基准，避免落到进程工作目录（服务场景是 System32）。
static void ApplyDataRoot(IConfiguration configuration, string dataRoot)
{
    var defaults = new Dictionary<string, string>
    {
        ["ConfigDbPath"] = "scada.db",
        ["HistoryRoot"] = "history",
        ["BackupRoot"] = "backups",
        ["DiagnosticsRoot"] = "diagnostics",
        ["ReportsRoot"] = "reports",
        ["LogRoot"] = "logs"
    };
    foreach (var (name, fallback) in defaults)
    {
        var key = $"Scada:{name}";
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) value = fallback;
        if (!Path.IsPathRooted(value))
        {
            configuration[key] = Path.GetFullPath(Path.Combine(dataRoot, value));
        }
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = ResolveWebRoot(args)
});
// 启动时打印实际静态文件根：部署与 E2E 最容易在这里用错目录（陈旧的 wwwroot 副本）。
Console.WriteLine($"[startup] webroot={builder.Environment.WebRootPath ?? "(null)"} contentRoot={builder.Environment.ContentRootPath}");
var dataRoot = ResolveDataRoot(builder.Configuration);
Console.WriteLine($"[startup] dataRoot={dataRoot}");
// 站点配置文件（显式指定或首次运行自举）；环境变量与命令行仍优先。
var siteSettingsPath = ResolveSiteSettings(builder.Configuration, dataRoot, builder.Environment);
if (siteSettingsPath is not null)
{
    builder.Configuration.AddJsonFile(siteSettingsPath, optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}
ApplyDataRoot(builder.Configuration, dataRoot);

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
// 授权：试用 120 天，过期后除登录与激活外全部功能不可用（LicenseGateMiddleware 统一拦截）。
builder.Services.Configure<Scada.Server.Licensing.LicenseOptions>(builder.Configuration.GetSection("Scada:License"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Scada.Server.Licensing.LicenseService>();
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
        && x.DeviceOfflineDelayMs is >= 0 and <= 300000 && x.DeviceStateGraceMs is >= 1000 and <= 300000
        && x.MaxInboxAttempts is >= 1 and <= 100 && x.InboxRetryBaseMs is >= 100 and <= 60000
        && x.InboxRetryMaxMs >= x.InboxRetryBaseMs && x.InboxRetryMaxMs <= 3600000,
        "网关租约须为 15～120 秒，收件箱限额至少 1024 字节，命令期限须为 100～120000 毫秒；离线延时须为 0～300000 毫秒，状态宽限须为 1000～300000 毫秒；收件箱最大尝试次数须为 1～100，重试基数须为 100～60000 毫秒且不高于重试上限（不超过 3600000 毫秒）").ValidateOnStart();
builder.Services.AddSingleton<GatewayCenterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayCenterService>());
builder.Services.AddSingleton<ICollectorControl, GatewayCollectorCoordinator>();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<Scada.Server.Auth.MobileOptions>(builder.Configuration.GetSection("Mobile"));
builder.Services.Configure<Scada.Core.Options.ExternalIntegrationOptions>(builder.Configuration.GetSection("External"));
builder.Services.AddSingleton<ExternalApiKeyService>();
builder.Services.AddSingleton<Scada.Server.External.Tcp.ExternalTcpHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.External.Tcp.ExternalTcpHost>());
builder.Services.AddSingleton<Scada.Server.External.OpcUa.ExternalOpcUaHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Scada.Server.External.OpcUa.ExternalOpcUaHost>());
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
                // 对外 API 密钥也用 Bearer 头，前缀 scada_；交给 ApiKey 方案，不按 JWT 解析。
                var auth = context.Request.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer scada_", StringComparison.OrdinalIgnoreCase))
                    context.NoResult();

                return Task.CompletedTask;
            }
        };
    }).AddScheme<AuthenticationSchemeOptions, GatewayAuthenticationHandler>(GatewayAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("GatewayAgent", policy => policy.AddAuthenticationSchemes(GatewayAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireClaim("gateway-id"));
    // 对外 REST：API 密钥 与（可选）登录 JWT 均可；具体 scope 在端点内再判。
    options.AddPolicy(ExternalRestEndpoints.Policy, policy =>
    {
        var schemes = new List<string> { ApiKeyAuthenticationHandler.SchemeName };
        if ((builder.Configuration.GetSection("External:Rest").Get<Scada.Core.Options.ExternalRestOptions>() ?? new()).AllowJwt)
            schemes.Insert(0, JwtBearerDefaults.AuthenticationScheme);
        policy.AddAuthenticationSchemes(schemes.ToArray()).RequireAuthenticatedUser();
    });
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
builder.Services.AddExceptionHandler<Scada.Server.Middleware.HistoryQueryExceptionHandler>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
    var externalOrigins = (builder.Configuration.GetSection("External:Rest").Get<Scada.Core.Options.ExternalRestOptions>() ?? new()).CorsOrigins
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (externalOrigins.Length > 0)
    {
        options.AddPolicy("external", policy => policy
            .WithOrigins(externalOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod());
    }
});
builder.Services.AddOpenApi("v1", o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info = new() { Title = "Scada External API", Version = "v1", Description = "Stable external integration contract under /api/v1. Internal UI uses /api/*." };
    return Task.CompletedTask;
}));

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
        sp.GetRequiredService<Scada.Engine.Notifications.AlarmNotificationDispatcher>(),
        sp.GetRequiredService<Scada.Engine.Alarms.AnnunciatorService>()
    },
    sp.GetRequiredService<ILogger<CompositeAlarmNotifier>>()));
builder.Services.AddHostedService<SignalRRealtimeBroadcaster>();
builder.Services.AddSingleton<ConfigReloader>();
builder.Services.AddSingleton<Scada.Engine.Calculations.ICalcTagWriter, Scada.Engine.Calculations.ControlledCalcTagWriter>();
builder.Services.Configure<Scada.Engine.Calculations.CSharpScriptOptions>(builder.Configuration.GetSection("Scada:CSharpScripts"));
builder.Services.AddSingleton<Scada.Engine.Calculations.ICSharpScriptRunner, Scada.Engine.Calculations.CSharpScriptHost>();
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

var app = builder.Build();

if (jwtKeyIsTemporary)
{
    app.Logger.LogWarning("Jwt:Key 未配置，本次使用临时签名密钥，重启后已签发的令牌全部失效");
}

await DatabaseInitializer.InitializeAsync(app.Services);

app.UseSerilogRequestLogging();
// 压缩必须在本地化外层：本地化要读取明文 JSON 再翻译，翻译后的结果再交给压缩。
app.UseResponseCompression();
// 英文界面：翻译 /api 响应中的提示文字（异常处理产生的提示也会被翻译）。
app.UseMiddleware<Scada.Server.Localization.ResponseLocalization>();
app.UseExceptionHandler();
app.UseCors("web");
if ((app.Configuration.GetSection("External:Rest").Get<Scada.Core.Options.ExternalRestOptions>() ?? new()).CorsOrigins.Length > 0)
    app.UseCors("external");
// 静态文件只认一个根：WebApplicationBuilder 的 WebRootFileProvider 会同时包含
// ContentRoot/wwwroot，导致即使指定了 --webroot 也仍命中程序目录下的陈旧副本（wwwroot 优先）。
// 这里显式构造单一 FileProvider，让 DefaultFiles / StaticFiles / SPA fallback 用同一来源。
var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (!Directory.Exists(webRoot))
{
    // 发布包必须自带 wwwroot。缺失时不要直接崩溃：双击启动没有控制台可看，
    // 建空目录让 API 与日志先可用，页面 404 时能通过日志定位。
    Directory.CreateDirectory(webRoot);
    app.Logger.LogWarning("静态文件根不存在，已创建空目录 {WebRoot}；发布包缺少 wwwroot 时前端不可用", webRoot);
}
var webRootFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
var staticFiles = new StaticFileOptions
{
    FileProvider = webRootFiles,
    OnPrepareResponse = context =>
    {
        var headers = context.Context.Response.Headers;
        var path = context.Context.Request.Path.Value ?? string.Empty;
        // Vite 产出的 assets 文件名带内容哈希，可放心长缓存；入口 html 必须每次协商，才能及时切到新版本。
        headers["Cache-Control"] = path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        headers["Vary"] = "Accept-Encoding";
    }
};
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRootFiles });
app.UseStaticFiles(staticFiles);
app.UseMiddleware<Scada.Server.Ha.HaStandbyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<Scada.Server.Licensing.LicenseGateMiddleware>();
app.UseMiddleware<Scada.Server.Auth.ClientControlPolicy>();

app.MapLicenseEndpoints();
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
app.MapAlarmAnalyticsEndpoints();
app.MapAnnunciatorEndpoints();
app.MapOeeEndpoints();
app.MapAlarmShelveEndpoints();
app.MapNotificationEndpoints();
app.MapCommandEndpoints();
app.MapControlEndpoints();
app.MapRecipeEndpoints();
app.MapTraceabilityEndpoints();
app.MapReportEndpoints();
app.MapLogEndpoints();
app.MapUserEndpoints();
app.MapAccessEndpoints();
app.MapScreenEndpoints();
app.MapHmiLibraryEndpoints();
app.MapHmiLayoutEndpoints();
// 客户端策略：前端据此在手机上隐藏/禁用控制入口（服务端中间件仍会强制拒绝）。
app.MapGet("/api/client-policy", (HttpContext http, Microsoft.Extensions.Options.IOptionsMonitor<Scada.Server.Auth.MobileOptions> mobile) =>
    Results.Ok(new { mobileControl = mobile.CurrentValue.Permits(http.User) })).RequireAuthorization();
app.MapObjectTemplateEndpoints();
app.MapExternalRestEndpoints();
app.MapExternalAdminEndpoints();
if ((app.Configuration.GetSection("External:Rest").Get<Scada.Core.Options.ExternalRestOptions>() ?? new()).Enabled)
    app.MapOpenApi("/openapi/v1.json").AllowAnonymous();
app.MapHealthEndpoints();
app.MapMetricsEndpoints();
app.MapMaintenanceEndpoints();
Scada.Server.Ha.HaEndpoints.MapHaEndpoints(app);
app.MapHub<RealtimeHub>("/hubs/realtime");

if (File.Exists(Path.Combine(webRoot, "index.html")))
{
    // SPA 路由匿名可达，未知 API 仍返回 404。
    app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
    app.MapFallbackToFile("index.html", staticFiles).AllowAnonymous();
}

// 双击启动（WinExe）没有控制台也没有反馈：就绪后打开浏览器，并在通知区域保留托盘入口。
// 服务模式、开发/自动化环境不启用；可用 Scada:OpenBrowserOnStart、SCADA_NO_BROWSER=1、SCADA_NO_TRAY=1 关闭。
var startupSettings = builder.Configuration.GetSection("Scada").Get<ScadaSettings>() ?? new ScadaSettings();
var openBrowser = startupSettings.OpenBrowserOnStart ?? !builder.Environment.IsDevelopment();
var isWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var interactiveBrowser = openBrowser && OperatingSystem.IsWindows() && Environment.UserInteractive
    && Environment.GetEnvironmentVariable("SCADA_NO_BROWSER") != "1" && !isWindowsService;
var trayEnabled = OperatingSystem.IsWindows() && Environment.UserInteractive
    && Environment.GetEnvironmentVariable("SCADA_NO_TRAY") != "1"
    && !builder.Environment.IsDevelopment() && !isWindowsService;
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
                tray = TrayIcon.Start(url, app.Configuration["Scada:LogRoot"] ?? Path.Combine(dataRoot, "logs"),
                    app.Lifetime.StopApplication, app.Logger);
            }
            catch (Exception ex) { app.Logger.LogWarning(ex, "托盘图标启动失败"); }
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
    try { app.Logger.LogCritical(ex, "SCADA 启动失败"); } catch (Exception) { }
    StartupErrorDialog.Show(
        $"{ex.Message}\n\n日志目录：{Path.Combine(dataRoot, "logs")}\n\n" +
        "常见原因：端口 5080 被占用、站点配置无效、数据目录无写权限。\n" +
        "修改端口可在启动时加 --urls http://0.0.0.0:5081，或调整 data\\appsettings.Site.json。");
    Environment.ExitCode = 1;
}
public partial class Program;
