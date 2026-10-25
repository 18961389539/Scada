using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

/// <summary>新协议接入点：设备参数校验与文本地址校验。驱动在 Scada.Collector 中按协议实现。</summary>
public interface IProtocolSpec
{
    ProtocolType Protocol { get; }
    /// <summary>是否使用 Device.Host/Port。</summary>
    bool UsesHostPort { get; }
    /// <summary>是否允许保存用户名/密码（复用 OpcUserName / OpcPasswordProtected）。</summary>
    bool UsesCredentials { get; }
    string? ValidateDevice(Device device);
    /// <summary>校验 Tag.NodeId 中的文本地址及数据类型/可写组合。</summary>
    string? ValidateTag(Tag tag, Device? device);
}

public static class ProtocolCatalog
{
    private static readonly Dictionary<ProtocolType, IProtocolSpec> Specs = new IProtocolSpec[]
    {
        new MqttSpec(), new SparkplugSpec(),
    }.ToDictionary(x => x.Protocol);

    public static IReadOnlyCollection<IProtocolSpec> All => Specs.Values;
    public static bool TryGet(ProtocolType protocol, out IProtocolSpec spec) => Specs.TryGetValue(protocol, out spec!);
    public static bool IsTextAddress(ProtocolType protocol) => Specs.ContainsKey(protocol);

    public static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>解析设备的协议参数；空串得到默认值。格式错误抛 FormatException。</summary>
    public static T Options<T>(Device device) where T : new()
    {
        if (string.IsNullOrWhiteSpace(device.ProtocolOptions)) return new T();
        try { return JsonSerializer.Deserialize<T>(device.ProtocolOptions, OptionsJson) ?? new T(); }
        catch (JsonException ex) { throw new FormatException($"协议参数 JSON 无效：{ex.Message}", ex); }
    }

    public static string? TryOptions<T>(Device device, out T options) where T : new()
    {
        options = new T();
        if (device.ProtocolOptions.Length > 16000) return "协议参数过长（最多 16000 字符）";
        try { options = Options<T>(device); return null; }
        catch (FormatException ex) { return ex.Message; }
    }

    public static string? HostPort(Device device) =>
        string.IsNullOrWhiteSpace(device.Host) || device.Host.Length > 255 || device.Port is < 1 or > 65535 ? "设备主机或端口无效" : null;
}
