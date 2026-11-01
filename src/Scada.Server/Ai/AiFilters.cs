using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.External;

namespace Scada.Server.Ai;

/// <summary>
/// AI 只读接口的鉴权辅助。
///
/// 复用对外 REST 的 API 密钥体系：密钥的 Scopes 决定能读什么，区域范围决定能看到哪些数据。
/// 这与 MCP 的行为一致——同一把密钥、同一个权限模型，两种协议都能用。
/// </summary>
public static class AiScopeGuard
{
    /// <summary>
    /// 校验调用者是否具备所需的外向读取范围。
    /// API 密钥按其 Scopes 判定；JWT 按用户权限映射后判定。返回非 null 表示拒绝。
    /// </summary>
    public static async Task<IResult?> RequireAsync(HttpContext http, ExternalApiScope need)
    {
        var keyScopes = http.GetExternalScopes();
        if (keyScopes is ExternalApiScope scopes)
        {
            if (!ExternalApiKeyService.HasScope(scopes, need))
            {
                return Results.Json(new { message = $"API 密钥缺少权限：{need}" }, statusCode: StatusCodes.Status403Forbidden);
            }
            return null;
        }

        // JWT：按用户权限映射成同样的范围，语义与外部 REST 保持一致。
        var access = await http.GetAccessAsync();
        var mapped = ExternalApiKeyService.ScopesFromUser(access);
        http.Items[ApiKeyAuthenticationHandler.ScopesItemKey] = mapped;
        if (!ExternalApiKeyService.HasScope(mapped, need))
        {
            return Results.Json(new { message = $"当前用户缺少权限：{need}" }, statusCode: StatusCodes.Status403Forbidden);
        }
        return null;
    }
}

/// <summary>
/// 限流：API 密钥走密钥自身的额度，JWT 走按用户/来源地址的内存令牌桶。
/// 目的是防止模型陷入重试循环把服务端打满，同时不让同一把密钥的额度被全局上限意外压制。
/// </summary>
public sealed class AiRateLimitFilter(
    ExternalApiKeyService keys,
    Microsoft.Extensions.Options.IOptionsMonitor<AiOptions> options) : IEndpointFilter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bucket> Buckets = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // API 密钥：使用密钥自己的每分钟额度（管理界面可逐个调整）。
        if (http.Items.TryGetValue("scada.external.rateKey", out var obj) && obj is ExternalApiKey key)
        {
            if (!keys.TryAcquire(key, out var retryAfter))
            {
                http.Response.Headers.RetryAfter = retryAfter.ToString();
                return Results.Json(new { message = "请求过于频繁" }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            return await next(context);
        }

        // JWT：按用户 ID 分桶；取不到用户时退回来源地址。
        var limit = Math.Clamp(options.CurrentValue.RequestsPerMinute, 1, 100_000);
        if (limit > 0)
        {
            var partition = http.User.GetUserId()?.ToString()
                ?? http.Connection.RemoteIpAddress?.MapToIPv6().ToString()
                ?? "unknown";
            if (!Buckets.GetOrAdd(partition, _ => new Bucket()).TryAcquire(limit, out var retryAfter))
            {
                http.Response.Headers.RetryAfter = retryAfter.ToString();
                return Results.Json(new { message = "请求过于频繁" }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        return await next(context);
    }

    private sealed class Bucket
    {
        private readonly Queue<long> _hits = new();
        private readonly object _gate = new();

        public bool TryAcquire(int limitPerMinute, out int retryAfterSec)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var window = now - 60_000;
                while (_hits.Count > 0 && _hits.Peek() < window) _hits.Dequeue();
                if (_hits.Count >= limitPerMinute)
                {
                    retryAfterSec = Math.Max(1, (int)Math.Ceiling((_hits.Peek() + 60_000 - now) / 1000.0));
                    return false;
                }
                _hits.Enqueue(now);
                retryAfterSec = 0;
                return true;
            }
        }
    }
}

/// <summary>
/// 审计：每次 AI 只读调用写一条操作日志，便于事后追溯「是谁、什么时候、用哪把密钥问了什么」。
/// 与外部 REST 的审计区别在于 TargetType 独立标记为 AiApi，便于按来源筛选。
/// </summary>
public sealed class AiAuditFilter(IDbContextFactory<ConfigDbContext> factory) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var result = await next(context);
        try
        {
            var status = http.Response.StatusCode;
            if (result is IStatusCodeHttpResult scr) status = scr.StatusCode ?? status;
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            var key = http.GetExternalKey();
            db.OperationLogs.Add(new OperationLog
            {
                UserId = http.User.GetUserId(),
                UserName = http.User.GetUserName(),
                Ts = ScadaTime.NowMs(),
                Action = $"{http.Request.Method} {http.Request.Path}",
                TargetType = "AiApi",
                TargetId = key?.Id.ToString() ?? http.User.GetUserId()?.ToString(),
                Details = JsonSerializer.Serialize(new { key = key?.Name, prefix = key?.KeyPrefix, query = http.Request.QueryString.Value }),
                Result = status is >= 200 and < 400 ? "成功" : status == 429 ? "拒绝：限流" : status == 401 ? "拒绝：未认证" : status == 403 ? "拒绝：权限不足" : $"HTTP {status}"
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        // 审计写失败不影响响应：调用方已经拿到结果，这次调用会在日志里缺失。
        catch { }
        return result;
    }
}
