using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Scada.Core.Models;

namespace Scada.Collector;

/// <summary>
/// 把驱动抛出的异常归到稳定的错误类别。驱动层为了表达现场细节会产生大量自由文本，
/// 运维需要的是可聚合的维度（超时/连接/协议/认证/配置/IO），否则只能靠人读日志。
/// 判断顺序：具体异常类型优先，其次在异常链上逐层匹配；消息关键字只作兜底，
/// 拿不准归 Unknown，避免把"设备未连接"误报成"协议错误"这类误导性归因。
/// </summary>
internal static class CollectorErrorClassifier
{
    private static readonly string[] ProtocolHints =
        ["异常码", "响应码", "报文", "校验", "帧", "end code", "protocol", "malformed"];
    private static readonly string[] TimeoutHints =
        ["超时", "timeout", "timed out", "无响应", "no response"];
    private static readonly string[] ConnectionHints =
        ["尚未连接", "未连接", "连接断开", "连接已断开", "连接关闭", "连接", "断开", "不可达", "拒绝",
         "connect", "refused", "unreachable", "reset", "closed", "socket", "network"];
    private static readonly string[] AuthorizationHints =
        ["认证", "证书", "密码", "凭据", "拒绝访问", "unauthorized", "forbidden", "certificate", "access denied"];

    public static CollectorErrorKind Classify(Exception? exception)
    {
        var sawIo = false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var kind = ClassifyOne(current);
            if (kind != CollectorErrorKind.None) return kind;
            if (current is IOException) sawIo = true;
        }
        // 消息里没有任何可用线索时，IOException 仍归 IO；其他类型才归 Unknown。
        return exception is null ? CollectorErrorKind.None : sawIo ? CollectorErrorKind.Io : CollectorErrorKind.Unknown;
    }

    private static CollectorErrorKind ClassifyOne(Exception exception) => exception switch
    {
        // 设备返回的协议异常码：属于"设备在线但拒绝了这次请求"，与链路故障分开统计。
        Mitsubishi.McEndCodeException or Omron.FinsEndCodeException => CollectorErrorKind.Protocol,
        TimeoutException or OperationCanceledException => CollectorErrorKind.Timeout,
        AuthenticationException or UnauthorizedAccessException => CollectorErrorKind.Authorization,
        CryptographicException => CollectorErrorKind.Authorization,
        SocketException => CollectorErrorKind.Connection,
        FormatException or OverflowException => CollectorErrorKind.Configuration,
        NotSupportedException => CollectorErrorKind.Configuration,
        ArgumentException => CollectorErrorKind.Configuration,
        IOException io => ClassifyIo(io.Message),
        _ => CollectorErrorKind.None
    };

    private static CollectorErrorKind ClassifyIo(string message)
    {
        if (ContainsAny(message, ProtocolHints)) return CollectorErrorKind.Protocol;
        if (ContainsAny(message, TimeoutHints)) return CollectorErrorKind.Timeout;
        if (ContainsAny(message, ConnectionHints)) return CollectorErrorKind.Connection;
        if (ContainsAny(message, AuthorizationHints)) return CollectorErrorKind.Authorization;
        // 没有线索时交给异常链继续判断；整条链都没有线索才归 IO。
        return CollectorErrorKind.None;
    }

    private static bool ContainsAny(string message, string[] hints)
    {
        foreach (var hint in hints)
            if (message.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
