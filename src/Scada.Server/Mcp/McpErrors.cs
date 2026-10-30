using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Scada.Engine.History;

namespace Scada.Server.Mcp;

public static class McpErrorCodes
{
    public const string InvalidArgument = "invalid_argument";
    public const string NotFound = "not_found";
    public const string Unsupported = "unsupported";
    public const string DataUnavailable = "data_unavailable";
    public const string DataIntegrity = "data_integrity";
    public const string ResultTooLarge = "result_too_large";
    public const string QueryBusy = "query_busy";
    public const string QueryTimeout = "query_timeout";
    public const string Internal = "internal";
}

// 工具执行错误按 MCP 规范放在 isError 结果里，并附稳定错误码，客户端可据此决定重试或缩小范围。
public sealed class McpToolException : McpException
{
    public McpToolException(string code, string message, bool retryable = false) : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public McpToolException(string code, string message, bool retryable, Exception inner) : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }
    public bool Retryable { get; }
}

public sealed record McpError(string Code, string Message, bool Retryable);

public sealed record McpErrorEnvelope(McpError Error);

public sealed record McpErrorDefinition(string Code, bool Retryable, string Meaning);

public static class McpContract
{
    public const string Version = "1.2";

    public static readonly IReadOnlyList<McpErrorDefinition> Errors =
    [
        new(McpErrorCodes.InvalidArgument, false, "Missing, malformed or out-of-range argument."),
        new(McpErrorCodes.NotFound, false, "A referenced record or scope does not exist."),
        new(McpErrorCodes.Unsupported, false, "The requested data is not available through MCP, for example remote-gateway diagnostics."),
        new(McpErrorCodes.DataUnavailable, true, "A transient snapshot is not cached yet."),
        new(McpErrorCodes.DataIntegrity, false, "Stored data is malformed or contains invalid values."),
        new(McpErrorCodes.ResultTooLarge, false, "The result exceeds its size budget or a read limit was hit."),
        new(McpErrorCodes.QueryBusy, true, "The analysis query slots are busy."),
        new(McpErrorCodes.QueryTimeout, true, "The analysis query budget expired."),
        new(McpErrorCodes.Internal, false, "Unexpected server failure.")
    ];
}

public static class McpErrors
{
    public static CallToolResult Result(McpToolException exception)
    {
        var payload = new McpErrorEnvelope(new McpError(exception.Code, exception.Message, exception.Retryable));
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, McpEndpoints.Json) }],
            StructuredContent = JsonSerializer.SerializeToElement(payload, McpEndpoints.Json)
        };
    }

    public static McpToolException Map(HistoryQueryException exception) => exception.Code switch
    {
        "history_query_busy" => new(McpErrorCodes.QueryBusy, exception.Message, true),
        "history_query_timeout" => new(McpErrorCodes.QueryTimeout, exception.Message, true),
        "history_query_read_limit" => new(McpErrorCodes.ResultTooLarge, exception.Message),
        _ => new(McpErrorCodes.Internal, exception.Message, false, exception)
    };

    // 未预期异常（空引用、数据库故障等）必须收敛成稳定的 internal 码，否则客户端只能看到 SDK 默认错误文案。
    public static McpToolException Unexpected(Exception exception) =>
        new(McpErrorCodes.Internal, "工具执行时发生未预期错误，请查看服务端日志。", false, exception);
}
