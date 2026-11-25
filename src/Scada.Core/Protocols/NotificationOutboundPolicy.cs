using System.Net;
using System.Net.Sockets;
using Scada.Core.Options;

namespace Scada.Core.Protocols;

/// <summary>
/// 通知出站目标策略。与采集侧并列：拒绝链路本地，避免填错地址打到 169.254/fe80。
/// 内网、环回与主机名默认放行（现场常用云邮件/短信以域名访问，不能默认禁止所有公网）。
/// 公网 IP 须命中内置云网关名单或 <see cref="ScadaSettings.NotificationTargetAllowlist"/>。
/// </summary>
public static class NotificationOutboundPolicy
{
    /// <summary>现场常用的公网邮件与短信网关，默认放行，不必先配白名单。</summary>
    public static readonly string[] BuiltInCloudHosts =
    [
        "smtp.office365.com", "smtp.gmail.com", "smtp-mail.outlook.com", "smtp.aliyun.com",
        "smtp.exmail.qq.com", "smtp.qq.com", "smtp.163.com", "smtp.126.com", "smtp.yeah.net",
        "smtp.mxhichina.com", "smtp.sendgrid.net", "smtp.mailgun.org", "smtp.zoho.com",
        "email-smtp.us-east-1.amazonaws.com", "email-smtp.us-west-2.amazonaws.com",
        "email-smtp.eu-west-1.amazonaws.com",
        "dysmsapi.aliyuncs.com", "dysmsapi.ap-southeast-1.aliyuncs.com",
        "sms.tencentcloudapi.com", "yun.tim.qq.com", "api.twilio.com", "api.nexmo.com"
    ];

    public static string? ValidateHost(string? host, ScadaSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var target = host.Trim();
        var allowlist = CombinedAllowlist(settings);
        if (OutboundTargetPolicy.IsAllowlisted(target, allowlist)) return null;
        if (!IPAddress.TryParse(Unwrap(target), out var address)) return null; // 主机名默认放行
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IsLinkLocal(address))
            return $"通知目标 {target} 属于链路本地地址，不允许作为邮件或 Webhook/短信出站地址";
        if (OutboundTargetPolicy.IsLocal(address)) return null;
        return $"通知目标 {target} 属于公网 IP，请改用域名（云邮件或短信网关的主机名默认可用），或请运维在出站名单中放行";
    }

    public static string? ValidateHttpUrl(string? url, ScadaSettings? settings)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "通道地址必须是 http/https URL";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "通道地址必须是 http/https URL";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "URL 中不能包含账号口令，请改用鉴权头";
        return ValidateHost(uri.IdnHost, settings);
    }

    public static IReadOnlyList<string> CombinedAllowlist(ScadaSettings? settings)
    {
        var extra = settings?.NotificationTargetAllowlist;
        if (extra is not { Count: > 0 }) return BuiltInCloudHosts;
        var list = new string[BuiltInCloudHosts.Length + extra.Count];
        BuiltInCloudHosts.CopyTo(list, 0);
        extra.CopyTo(list, BuiltInCloudHosts.Length);
        return list;
    }

    public static bool IsLinkLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal;
        var bytes = address.GetAddressBytes();
        return bytes.Length >= 2 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static string Unwrap(string host) =>
        host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
}

