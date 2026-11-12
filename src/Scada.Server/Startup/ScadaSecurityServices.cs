using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;
using Scada.Server.Ai;
using Scada.Server.Auth;
using Scada.Server.External;
using Scada.Server.External.Rest;
using Scada.Server.Hubs;
using Scada.Server.Services;

namespace Scada.Server.Startup;

internal sealed record ScadaSecuritySetup(JwtOptions Jwt, bool TemporaryKey);

// 认证与鉴权组合根：DataProtection、JWT、策略、CORS 与异常处理。
// 业务服务注册在 ScadaServerServices；本方法返回 JWT 结果供启动日志与管道使用。
internal static class ScadaSecurityServices
{
    public static ScadaSecuritySetup AddScadaSecurity(this WebApplicationBuilder builder, ScadaSettings scadaOptions, ExternalRestOptions externalRest)
    {
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        builder.Services.Configure<Scada.Server.Auth.MobileOptions>(builder.Configuration.GetSection("Mobile"));

        var credentialKeys = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scadaOptions.ConfigDbPath))!, "keys"));
        var protection = builder.Services.AddDataProtection().SetApplicationName("Scada").PersistKeysToFileSystem(credentialKeys);
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        builder.Services.AddSingleton<IDeviceCredentials, DeviceCredentials>();

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
        builder.Services.AddSingleton<TokenUserCache>();
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
                        if (id is not int userId) { context.Fail("登录状态已变更，请重新登录"); return; }
                        // 账号状态走 5 秒缓存，避免每个请求都查一次 Users 表（见 TokenUserCache）。
                        var cache = context.HttpContext.RequestServices.GetRequiredService<TokenUserCache>();
                        var valid = await cache.IsValidAsync(
                            userId,
                            context.Principal?.FindFirstValue(ClaimTypes.Role),
                            context.Principal?.FindFirstValue("credentialsVersion"),
                            context.HttpContext.RequestAborted);
                        if (!valid) context.Fail("登录状态已变更，请重新登录");
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
                if (externalRest.AllowJwt)
                    schemes.Insert(0, JwtBearerDefaults.AuthenticationScheme);
                policy.AddAuthenticationSchemes(schemes.ToArray()).RequireAuthenticatedUser();
            });
            // AI 只读接口：与对外 REST 同一套鉴权（API 密钥 / 可选 JWT），但只挂只读端点。
            options.AddPolicy(AiEndpoints.Policy, policy =>
            {
                var schemes = new List<string> { ApiKeyAuthenticationHandler.SchemeName };
                if (externalRest.AllowJwt)
                    schemes.Insert(0, JwtBearerDefaults.AuthenticationScheme);
                policy.AddAuthenticationSchemes(schemes.ToArray()).RequireAuthenticatedUser();
            });
            AccessPolicies.Register(options);
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        builder.Services.AddExceptionHandler<Scada.Server.Middleware.HistoryQueryExceptionHandler>();
        // 顺序有意义：先由业务异常处理器筛选，未认领的异常落到兜底处理器记录并返回追踪号。
        builder.Services.AddExceptionHandler<Scada.Server.Middleware.UnhandledExceptionHandler>();
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("web", policy => policy
                .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
            var externalOrigins = externalRest.CorsOrigins
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (externalOrigins.Length > 0)
            {
                options.AddPolicy("external", policy => policy
                    .WithOrigins(externalOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod());
            }
        });

        builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();
        builder.Services.AddSingleton<RealtimeSubscriptionStore>();
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddHostedService<SessionReaper>();

        return new ScadaSecuritySetup(jwtOptions, jwtKeyIsTemporary);
    }
}
