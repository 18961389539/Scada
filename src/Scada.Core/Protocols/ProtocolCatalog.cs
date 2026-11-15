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
    /// <summary>设备参数表单、默认端口与地址提示。前端只消费此元数据，不再维护平行目录。</summary>
    ProtocolFormSpec Form { get; }
    string? ValidateDevice(Device device);
    /// <summary>校验 Tag.NodeId 中的文本地址及数据类型/可写组合。</summary>
    string? ValidateTag(Tag tag, Device? device);
}

public static class ProtocolCatalog
{
    private static readonly object Gate = new();
    private static readonly List<IProtocolSpec> BuiltIn =
    [
        new MqttSpec(), new SparkplugSpec(), new MitsubishiMcSpec(), new OmronFinsSpec(), new Iec104Spec(), new BacnetSpec(), new OpcDaSpec(), new Iec61850Spec(), new EtherNetIpSpec(), new InovanceSpec(), new XinjeSpec(), new DeltaSpec(),
    ];
    private static readonly Dictionary<ProtocolType, IProtocolSpec> Specs = BuiltIn.ToDictionary(x => x.Protocol);
    private static readonly IReadOnlyList<IProtocolSpec> Snapshot = BuiltIn.AsReadOnly();

    public static IReadOnlyList<IProtocolSpec> All => Snapshot;

    /// <summary>
    /// 注册协议规格（外部驱动插件用）。覆盖已注册的协议必须显式 <paramref name="replace"/>：
    /// 驱动覆盖已经有 SHA-256 允许列表与 <c>DriverPluginOverrideProtocols</c> 两道开关，
    /// 表单与校验规格的覆盖不应比它更宽松。
    ///
    /// 只应在启动阶段调用一次；读取路径（<see cref="TryGet"/>、<see cref="Options{T}"/>）不加锁，
    /// 依赖"注册完成后集合不再变化"。
    /// </summary>
    public static void Register(IProtocolSpec spec, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (Gate)
        {
            var index = BuiltIn.FindIndex(x => x.Protocol == spec.Protocol);
            if (index >= 0 && !replace)
                throw new InvalidOperationException($"协议规格 {spec.Protocol} 已注册；确需覆盖请显式传 replace: true");
            if (index >= 0) BuiltIn[index] = spec;
            else BuiltIn.Add(spec);
            Specs[spec.Protocol] = spec;
        }
    }

    public static bool TryGet(ProtocolType protocol, out IProtocolSpec spec) => Specs.TryGetValue(protocol, out spec!);

    /// <summary>该协议是否使用文本地址（<see cref="Tag.NodeId"/>）而不是寄存器号。</summary>
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

    public static string? HostPort(Device device, Scada.Core.Options.ScadaSettings? settings = null) =>
        string.IsNullOrWhiteSpace(device.Host) || device.Host.Length > 255 || device.Port is < 1 or > 65535
            ? "设备主机或端口无效"
            : OutboundTargetPolicy.Validate(device.Host, settings);
}
