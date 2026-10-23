using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.IdentityModel.Tokens;
using Scada.Collector;
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

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Host.UseSerilog((context, _, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true));

builder.Services.AddScadaData(builder.Configuration);
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
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Operator", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
            context.User.IsInRole("Operator") || context.User.IsInRole("Admin")));
    options.AddPolicy("Admin", policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("Admin"));
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

builder.Services.AddSingleton<RealtimeSubscriptionStore>();
builder.Services.AddSingleton<IAlarmNotifier, SignalRAlarmNotifier>();
builder.Services.AddHostedService<SignalRRealtimeBroadcaster>();
builder.Services.AddSingleton<ConfigReloader>();
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
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapCollectorDiagnosticEndpoints();
app.MapOpcEndpoints();
app.MapTagEndpoints();
app.MapAlarmRuleEndpoints();
app.MapRealtimeEndpoints();
app.MapHistoryEndpoints();
app.MapAlarmEndpoints();
app.MapCommandEndpoints();
app.MapReportEndpoints();
app.MapLogEndpoints();
app.MapUserEndpoints();
app.MapScreenEndpoints();
app.MapObjectTemplateEndpoints();
app.MapHealthEndpoints();
app.MapHub<RealtimeHub>("/hubs/realtime");

var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(webRoot, "index.html")))
{
    // SPA 路由匿名可达，未知 API 仍返回 404。
    app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
    app.MapFallbackToFile("index.html").AllowAnonymous();
}

app.Run();
