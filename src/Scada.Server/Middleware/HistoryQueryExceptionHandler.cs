using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Scada.Engine.History;

namespace Scada.Server.Middleware;

public sealed class HistoryQueryExceptionHandler(ILogger<HistoryQueryExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not HistoryQueryException query) return false;
        var status = query.Code switch
        {
            "history_query_busy" => StatusCodes.Status503ServiceUnavailable,
            "history_query_timeout" => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status422UnprocessableEntity
        };
        logger.LogWarning("History query rejected: {Code}", query.Code);
        httpContext.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable) httpContext.Response.Headers.RetryAfter = "5";
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = query.Message,
            Instance = httpContext.Request.Path,
            Extensions = { ["code"] = query.Code, ["message"] = query.Message }
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}