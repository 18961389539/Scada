using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Server.Auth;
using Scada.Server.External;
using Scada.Server.Mcp;

namespace Scada.Server.Startup;

/// <summary>
/// 流量治理与可观测性组合根：限流、请求超时、OpenTelemetry。
///
/// 与 <see cref="ScadaServerServices"/>（业务服务）和 <see cref="ScadaSecurityServices"/>（认证鉴权）分开，
/// 是因为这三块的生命周期与调优目标完全不同：业务改动很少碰这里，而这里的阈值按现场规模调。
/// </summary>
internal static class ScadaResilience
{
    /// <summary>长任务端点（导入、备份、报表生成）显式挂这个策略，避免被默认读超时打断。</summary>
    public const string WriteTimeoutPolicy = "scada-write";

    public static void AddScadaResilience(this WebApplicationBuilder builder, ScadaSettings scada)
    {
        // 上传上限：Kestrel 默认 30MB 对工程包与画面资源偏小，超限会直接 413 且不带业务提示。
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = Math.Clamp(scada.MaxUploadMb, 1, 4096) * 1024L * 1024L);

        AddForwardedHeaders(builder, scada);
        AddRateLimiting(builder, scada);
        AddRequestTimeouts(builder, scada);
        AddTelemetry(builder);
    }

    // ── 反向代理 ────────────────────────────────────────────────────────────
    // TLS 终结在 nginx/IIS 时 RemoteIpAddress 是代理地址；限流按「身份 + 终端地址」分区，
    // 不还原真实地址就会把所有终端合并成一个桶。只信任显式列出的代理，避免伪造 XFF 绕过限流。
    private static void AddForwardedHeaders(WebApplicationBuilder builder, ScadaSettings scada)
    {
        if (!scada.ReverseProxy.Enabled) return;
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var value in scada.ReverseProxy.KnownProxies)
            {
                if (IPAddress.TryParse(value.Trim(), out var address)) options.KnownProxies.Add(address);
            }
        });
    }

    // ── 限流 ────────────────────────────────────────────────────────────────
    // 此前只有登录、MCP 与对外 REST 有限流，内部 /api 完全没有：一个已登录脚本反复打 OEE 或
    // 报警分析这类聚合查询，就能把 SQLite 的读连接占满。这里按身份分两档：
    // 有凭据（JWT / API 密钥 / 网关）按「身份 + 终端」发令牌桶，匿名按来源 IP 固定窗口。
    private static void AddRateLimiting(WebApplicationBuilder builder, ScadaSettings scada)
    {
        var perUser = Math.Max(0, scada.ApiRateLimitPerMinute);
        var perAnonymous = Math.Max(0, scada.AnonymousRateLimitPerMinute);
        var mcp = builder.Configuration.GetSection("Mcp").Get<McpOptions>() ?? new McpOptions();

        // 整个进程只在这里调用 AddRateLimiter。此前 AddScadaMcp 也调过一次并设了自己的 OnRejected，
        // 后注册的回调会静默覆盖前一个：MCP 的 429 一度丢掉 Retry-After。
        // 全局策略、MCP 端点策略、拒绝响应合并进同一个配置委托，不再受注册顺序影响。
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // /api/mcp 端点用 RequireRateLimiting 挂这个策略。策略必须与端点注册同时存在，
            // 否则宿主启动时会因"找不到策略名"直接失败。
            if (mcp.Enabled)
            {
                var mcpLimit = Math.Clamp(mcp.RequestsPerMinute, 1, 10000);
                options.AddPolicy(McpEndpoints.RatePolicy, http =>
                    RateLimitPartition.GetFixedWindowLimiter(McpEndpoints.ClientAddress(http), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = mcpLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
            }
            options.OnRejected = (context, ct) =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>().CreateLogger("Scada.RateLimiting");
                logger.LogWarning("请求被限流：{Method} {Path} 来源 {Address}",
                    context.HttpContext.Request.Method, context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress);
                // 429 必须带 Retry-After：MCP 客户端、采集网关与 HA 对端靠它决定退避时长，
                // 缺了这个头客户端只能立刻重试，反而把限流窗口一直打满。
                // 令牌桶/固定窗口的 lease 通常带 RetryAfter 元数据；拿不到时按一分钟窗口给保守值。
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Ceiling(retry.TotalSeconds)
                    : 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                // MCP 客户端会把 429 的 JSON 体当作错误信息展示，恢复它原有的英文文案；
                // 内部 /api 的 429 保持真空响应，避免本地化中间件为一条限流提示做无谓翻译。
                return context.HttpContext.Request.Path.StartsWithSegments(McpEndpoints.Path)
                    ? new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
                        new { message = "MCP request rate limit exceeded." }, ct))
                    : ValueTask.CompletedTask;
            };

            if (perUser == 0 && perAnonymous == 0)
            {
                // 显式关闭：仍然注册中间件（管道里已调用 UseRateLimiter），分区器直接放行。
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter("disabled"));
                return;
            }

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                // 静态资源与存活探针不计入配额：首屏一次要拉几十个 hashed 资源，
                // 把它们算进限流只会让正常用户在弱网下反复 429。
                if (BypassesRateLimit(http.Request.Path))
                    return RateLimitPartition.GetNoLimiter("static");

                // 令牌桶按「账号 + 终端地址」分区，不是只按账号：控制室里几十台操作台
                // 常用同一个只读账号自动登录（客户端设置页也建议"专用账号、只授查看权限"），
                // 只按 userId 时它们的固定轮询会合用一个桶——单台报警条 2 请求/3s、
                // 版本检查 6/min，25 台就能把 1200/min 打满，之后操作员的确认、趋势、
                // 配置请求全部 429。加上来源地址后每台终端各有配额；同一台机器上的
                // 多标签页仍共享一个桶，脚本刷接口的放大幅度没有变化。
                // API 密钥与网关凭据只在 authorization policy 里求值（UseAuthorization 阶段），
                // 而限流在认证之后、授权之前：只看 http.User 会把它们全归进匿名 IP 桶。
                // 单台网关 250ms 轮询约 480/min，足以打满 120/min 的匿名配额，命令与遥测持续 429。
                // 这里直接从请求头提取凭据指纹做分区，不改变任何鉴权结果。
                if (perUser > 0 && CredentialPartitionKey(http) is { } identity)
                    return RateLimitPartition.GetTokenBucketLimiter(PartitionKey(http, identity), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = perUser,
                        TokensPerPeriod = perUser,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });

                if (perAnonymous > 0)
                    return RateLimitPartition.GetFixedWindowLimiter($"ip:{AddressKey(http)}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = perAnonymous,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });

                // 匿名限流被显式关闭（=0）才放行。
                return RateLimitPartition.GetNoLimiter("anonymous-disabled");
            });
        });
    }

    /// <summary>终端地址参与分区：拿不到地址时（TestServer、转发场景）退回 <c>local</c>。</summary>
    internal static string AddressKey(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "local";

    /// <summary>
    /// 限流分区用的身份键。只提取并哈希凭据，不做认证判断——鉴权仍由 AuthenticationHandler
    /// 与 authorization policy 完成。API 密钥和网关凭据在限流阶段还没有进入 http.User，
    /// 不从请求头识别就会全部落进匿名 IP 桶。返回 null 表示匿名请求。
    /// </summary>
    internal static string? CredentialPartitionKey(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true && http.User.GetUserId() is int userId)
            return $"user:{userId}";
        if (ApiKeyAuthenticationHandler.Extract(http.Request) is { Length: > 0 } secret)
            return "apikey:" + Fingerprint(secret);
        var authorization = http.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Gateway ", StringComparison.OrdinalIgnoreCase))
            return "gateway:" + Fingerprint(authorization[8..]);
        return null;
    }

    private static string Fingerprint(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16];

    /// <summary>
    /// 每终端一条配额：身份（账号 / API 密钥 / 网关凭据）+ 来源地址。同一账号的多台操作台
    /// 各用各的桶，同一台机器上的多标签页仍共享一个；拿不到地址时退回 <c>local</c>。
    /// </summary>
    internal static string PartitionKey(HttpContext http, string identity) =>
        $"{identity}:ip:{AddressKey(http)}";

    internal static bool BypassesRateLimit(PathString path) =>
        path.StartsWithSegments("/assets")
        || path.StartsWithSegments("/health/live")
        || path.StartsWithSegments("/api/health/live")
        // HA 心跳/选主是集群内部的高频通信，有独立的 peer 凭据；计入匿名配额会在
        // 故障切换时把最关键的状态探测打成 429，导致双主或长时间无主。
        || path.StartsWithSegments("/api/ha/peer")
        // 桌面客户端每 30 秒探测一次主备角色（HaEndpoints 里是匿名可调），几百台终端
        // 走同一个出口时会把 120/min 的匿名配额吃满，连带登录等匿名请求一起 429。
        // 与 peer 同理：这是集群状态探测，读多写零，直接豁免。
        || path.StartsWithSegments("/api/ha/role")
        // MCP 有按来源 IP 的专用策略（ScadaMcp）。再叠一层全局匿名配额，有效配额会变成
        // 两者中的较小值，且两个窗口的起算时刻不同，客户端无法预期何时被拒。
        || path.StartsWithSegments(McpEndpoints.Path)
        || path.Value is "/favicon.ico" or "/manifest.webmanifest" or "/icon.svg" or "/sw.js" or "/robots.txt";

    // ── 请求超时 ─────────────────────────────────────────────────────────────
    // 默认策略覆盖读请求（30 秒）；导入、备份、报表生成这类长任务在端点上挂 WriteTimeoutPolicy。
    // 没有超时的时候，一个不读响应的客户端可以让请求线程挂到 TCP keepalive 超时。
    private static void AddRequestTimeouts(WebApplicationBuilder builder, ScadaSettings scada)
    {
        var read = scada.ReadRequestTimeoutSeconds;
        var write = scada.WriteRequestTimeoutSeconds;
        if (read <= 0 && write <= 0) return;

        builder.Services.AddRequestTimeouts(options =>
        {
            if (read > 0)
            {
                options.DefaultPolicy = new RequestTimeoutPolicy
                {
                    Timeout = TimeSpan.FromSeconds(read),
                    TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
                };
            }
            if (write > 0)
                options.AddPolicy(WriteTimeoutPolicy, TimeSpan.FromSeconds(write));
        });
    }

    // ── OpenTelemetry ────────────────────────────────────────────────────────
    private static void AddTelemetry(WebApplicationBuilder builder)
    {
        var telemetry = builder.Configuration.GetSection("Scada:Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        var endpoint = string.IsNullOrWhiteSpace(telemetry.Endpoint)
            ? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            : telemetry.Endpoint;
        if (!telemetry.Enabled || string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var otlp))
        {
            // 未配置接收端时完全不注册也不告警：这是默认状态，不是错误配置。
            return;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(telemetry.ServiceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = builder.Environment.EnvironmentName
                }))
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new TraceIdRatioBasedSampler(Math.Clamp(telemetry.SamplingRatio, 0d, 1d)));
                tracing.AddAspNetCoreInstrumentation(o => o.Filter = context =>
                    !context.Request.Path.StartsWithSegments("/health/live")
                    && !context.Request.Path.StartsWithSegments("/metrics")
                    && !context.Request.Path.StartsWithSegments("/assets"));
                tracing.AddHttpClientInstrumentation();
                // EF Core 自带同名 ActivitySource，订阅后每条 SQL 会生成子 span（db.system/sqlite、
                // 语句模板与耗时），足以回答"这一次 /api/history 的时间花在哪"。
                // 官方 OpenTelemetry.Instrumentation.EntityFrameworkCore 至今只有 beta 版，
                // 不给生产依赖图引入预发布包；要更细的标签时再评估升级。
                if (telemetry.EntityFrameworkCore)
                    tracing.AddSource("Microsoft.EntityFrameworkCore");
                tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlp);
            });
    }
}
