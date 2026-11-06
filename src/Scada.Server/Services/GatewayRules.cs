using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Options;

namespace Scada.Server.Services;

// 网关协议里"纯规则"的部分：凭据指纹、配置版本、死信排序、失败分类、重试退避、数据包校验。
// 这些函数不碰数据库、不碰缓存，抽出来是为了能单独测——失败分类直接决定一个坏包是重试还是转死信，
// 判错就会把可恢复的 IO 错误当成永久错误丢弃，或者让坏包永久占住序号。
internal static class GatewayRules
{
    public static string TokenHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // 管理面版本只覆盖"管理员改得到"的字段：改名称、启停、换密钥都要让前端知道配置变了。
    public static string AdminRevision(AcquisitionGateway gateway) =>
        TokenHash(JsonSerializer.Serialize(new { gateway.Id, gateway.Name, gateway.Enabled, gateway.SessionId, gateway.TokenHash }, GatewayProtocol.Json));

    // 下发给网关的配置指纹：包含协议密码的受保护值，密码轮换也必须换版本。
    public static GatewayConfiguration ConfigurationMetadata(IReadOnlyList<Device> devices, IReadOnlyList<Tag> tags)
    {
        var publicDevices = devices.Select(x => { var clone = x.Clone(); clone.OpcPasswordProtected = null; return new GatewayDeviceConfiguration(clone, null); }).ToArray();
        var configuration = new GatewayConfiguration("", publicDevices, tags);
        var fingerprint = JsonSerializer.Serialize(configuration, GatewayProtocol.Json) + string.Join('|', devices.Select(x => $"{x.Id}:{x.OpcPasswordProtected}"));
        return configuration with { Revision = TokenHash(fingerprint) };
    }

    // 排序是失败时间、序号、数据流。同一时间同一序号时数据流按文本序接在后面，避免翻页时漏掉或重复。
    public static bool IsOlderLetter(GatewayInboxBatch row, long ts, long seq, Guid stream)
    {
        var dead = row.DeadLetteredTs ?? 0;
        if (dead != ts) return dead < ts;
        if (row.Sequence != seq) return row.Sequence < seq;
        return string.Compare(row.StreamId.ToString("N"), stream.ToString("N"), StringComparison.Ordinal) < 0;
    }

    // 失败分类：数据库忙/IO/超时只退避重试；解析、配置缺失、数据无效等永久错误累计到上限后转死信。
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6 or 10) return true;
            if (current is IOException or TimeoutException) return true;
        }
        return false;
    }

    public static int RetryDelayMs(GatewaySettings settings, int attempts)
    {
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 10);
        var delay = (long)settings.InboxRetryBaseMs * (1L << exponent);
        return (int)Math.Clamp(delay, settings.InboxRetryBaseMs, Math.Max(settings.InboxRetryMaxMs, settings.InboxRetryBaseMs));
    }

    // 收件箱在网关侧产生，服务端必须假设载荷不可信：版本、序号、时标偏移、质量枚举、重复测点都要挡住。
    public static void ValidatePacket(GatewayTelemetry packet, long now)
    {
        if (packet.Version != GatewayProtocol.Version || packet.StreamId == Guid.Empty || packet.Sequence < 1 || packet.DeviceId < 1 || !GatewayProtocol.IsRevision(packet.ConfigurationRevision)
            || packet.CreatedTs < 1 || packet.CreatedTs > now + 120000 || packet.Values is null || packet.Values.Count is < 1 or > GatewayProtocol.MaxValuesPerBatch
            || packet.Values.Any(x => x is null || x.TagId < 1 || !double.IsFinite(x.Value) || !Enum.IsDefined(x.Quality) || x.Ts < 1 || x.Ts > now + 120000 || x.SourceTs is < 0 || x.Error?.Length > 1000)
            || packet.Values.Select(x => x.TagId).Distinct().Count() != packet.Values.Count) throw new GatewayRequestException(400, "网关数据包无效");
    }

    public static string Name(string? name) =>
        name?.Trim() is string value && value.Length is > 0 and <= 100 ? value : throw new GatewayRequestException(400, "网关名称须为 1～100 个字符");

    // 只有真正用到用户名密码的协议才需要解出凭据；否则下发 null，避免把无意义的密文带到网关侧。
    public static bool UsesCredentials(ProtocolType protocol) => protocol == ProtocolType.OpcUa
        || Scada.Core.Protocols.ProtocolCatalog.TryGet(protocol, out var spec) && spec.UsesCredentials;

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
