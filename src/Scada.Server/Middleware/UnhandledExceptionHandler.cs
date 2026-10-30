using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Scada.Server.Middleware;

// 兜底处理器：业务未处理的异常统一记录日志并返回带追踪号的中文提示，避免默认英文 500 页面。
public sealed class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted) return false;
        var traceId = httpContext.TraceIdentifier;
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("请求已被客户端取消 {Method} {Path}，追踪号 {TraceId}", httpContext.Request.Method, httpContext.Request.Path, traceId);
            return true;
        }

        logger.LogError(exception, "未处理的异常 {Method} {Path}，追踪号 {TraceId}", httpContext.Request.Method, httpContext.Request.Path, traceId);
        const int status = StatusCodes.Status500InternalServerError;
        httpContext.Response.StatusCode = status;
        var message = $"服务器内部错误，请稍后重试（追踪号 {traceId}）";
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = message,
            Instance = httpContext.Request.Path,
            Extensions = { ["code"] = "internal_error", ["message"] = message, ["traceId"] = traceId }
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
