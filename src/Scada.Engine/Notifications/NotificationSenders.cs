using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Engine.Notifications;

// 通道的非敏感参数；敏感值（SMTP 口令、鉴权头的值）单独加密保存，不在这里。
public sealed class NotificationChannelSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? UserName { get; set; }
    public string? From { get; set; }
    public string? Url { get; set; }
    public string? HeaderName { get; set; }
    public int TimeoutMs { get; set; } = 5000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static NotificationChannelSettings Parse(string? json)
    {
        try { return JsonSerializer.Deserialize<NotificationChannelSettings>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string? Validate(NotificationChannelType type)
    {
        switch (type)
        {
            case NotificationChannelType.Email:
                if (string.IsNullOrWhiteSpace(Host)) return "邮件通道必须填写 SMTP 服务器";
                if (Port is < 1 or > 65535) return "SMTP 端口无效";
                if (string.IsNullOrWhiteSpace(From) || !MailAddress.TryCreate(From, out _)) return "发件人地址无效";
                break;
            default:
                if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return "通道地址必须是 http/https URL";
                if (!string.IsNullOrEmpty(uri.UserInfo)) return "URL 中不能包含账号口令，请改用鉴权头";
                break;
        }
        if (!string.IsNullOrWhiteSpace(HeaderName) && !System.Text.RegularExpressions.Regex.IsMatch(HeaderName, "^[A-Za-z0-9-]{1,64}$"))
            return "鉴权头名称无效";
        if (TimeoutMs is < 500 or > 60000) return "超时须在 500～60000 毫秒之间";
        return null;
    }
}

public sealed record NotificationMessage(string Subject, string Body, object Payload);

// 可插拔的发送通道：新增短信厂商等只需实现本接口并注册。
public interface INotificationSender
{
    NotificationChannelType Type { get; }
    Task SendAsync(NotificationChannelSettings settings, string? secret, IReadOnlyList<string> recipients, NotificationMessage message, CancellationToken ct);
}

public sealed class EmailNotificationSender : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public async Task SendAsync(NotificationChannelSettings settings, string? secret, IReadOnlyList<string> recipients, NotificationMessage message, CancellationToken ct)
    {
        var to = recipients.Where(x => MailAddress.TryCreate(x, out _)).ToList();
        if (to.Count == 0) throw new InvalidOperationException("没有有效的收件邮箱");
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseSsl, Timeout = settings.TimeoutMs, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrWhiteSpace(settings.UserName)) client.Credentials = new NetworkCredential(settings.UserName, secret ?? "");
        using var mail = new MailMessage { From = new MailAddress(settings.From!), Subject = message.Subject, Body = message.Body, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
        foreach (var address in to) mail.To.Add(address);
        await client.SendMailAsync(mail, ct);
    }
}

public abstract class HttpNotificationSender(HttpClient http) : INotificationSender
{
    public abstract NotificationChannelType Type { get; }
    protected abstract object Body(IReadOnlyList<string> recipients, NotificationMessage message);

    public async Task SendAsync(NotificationChannelSettings settings, string? secret, IReadOnlyList<string> recipients, NotificationMessage message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Clamp(settings.TimeoutMs, 500, 60000));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Body(recipients, message), NotificationChannelSettings.Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (!string.IsNullOrWhiteSpace(settings.HeaderName) && !string.IsNullOrEmpty(secret))
            request.Headers.TryAddWithoutValidation(settings.HeaderName, secret);
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"通道返回 HTTP {(int)response.StatusCode}");
    }
}

public sealed class WebhookNotificationSender(HttpClient http) : HttpNotificationSender(http)
{
    public override NotificationChannelType Type => NotificationChannelType.Webhook;
    protected override object Body(IReadOnlyList<string> recipients, NotificationMessage message) => message.Payload;
}

// 通用短信 HTTP 网关适配：POST {"phones":[...],"message":"..."}，鉴权放在自定义头里；厂商格式不同时另写一个 sender。
public sealed class SmsHttpNotificationSender(HttpClient http) : HttpNotificationSender(http)
{
    public override NotificationChannelType Type => NotificationChannelType.SmsHttp;
    protected override object Body(IReadOnlyList<string> recipients, NotificationMessage message) =>
        new { phones = recipients, message = message.Body.Length > 300 ? message.Body[..300] : message.Body };
}
