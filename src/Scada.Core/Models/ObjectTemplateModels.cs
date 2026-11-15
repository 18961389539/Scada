using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Models;

public sealed class ObjectTemplateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ObjectTagDefinition> Tags { get; set; } = [];
    public List<ObjectAlarmDefinition> Alarms { get; set; } = [];
    public ScreenDocument Panel { get; set; } = new() { Width = 800, Height = 480 };
    public Dictionary<string, string> Bindings { get; set; } = [];
    public Dictionary<string, string> ActionBindings { get; set; } = [];
}

public sealed class ObjectTagDefinition
{
    public string Key { get; set; } = string.Empty;
    public int AddressOffset { get; set; }
    public Tag Properties { get; set; } = new() { DeviceId = 1 };
}

public sealed class ObjectAlarmDefinition
{
    public string Key { get; set; } = string.Empty;
    public string TagKey { get; set; } = string.Empty;
    public AlarmRule Properties { get; set; } = new() { TagId = 1 };
}

public sealed class ObjectInstanceBaseline
{
    public Dictionary<string, Tag> Tags { get; set; } = [];
    public Dictionary<string, AlarmRule> Alarms { get; set; } = [];
    public string ScreenName { get; set; } = string.Empty;
    public ScreenDocument Panel { get; set; } = new();
}

public sealed record ObjectInstanceParameters(string Name, int DeviceId, int BaseAddress, string? StartAddress = null);

/// <summary>对象实例的地址起点。前缀为空时是寄存器号，否则是软元件，例如 D1000。</summary>
public readonly record struct AddressOrigin(string Prefix, int Number, string Text)
{
    public string Element(int offset) => Prefix + (Number + offset).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
public sealed record TemplateConflict(string Key, string Label, string Baseline, string Local, string Incoming, string[] Choices);
public sealed record TemplateChange(string Key, string Action, string Detail);

public static class ObjectTemplateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
    public static T Copy<T>(T value) => Read<T>(Write(value));
    public static bool Equal<T>(T left, T right) => Write(left) == Write(right);
}

public static partial class ObjectTemplateValidator
{
    public static IReadOnlyList<string> Validate(ObjectTemplateDocument? definition, bool forPublish = true)
    {
        var errors = new List<string>();
        if (definition is null) return ["模板内容不能为空"];
        if (definition.SchemaVersion != 1) errors.Add("模板格式版本不支持");
        if (definition.Tags is null || definition.Tags.Count > 500 || forPublish && definition.Tags.Count == 0)
            return ["模板须包含 1～500 个测点，草稿允许暂不配置测点"];
        if (definition.Alarms is null || definition.Alarms.Count > 2000 || definition.Panel is null || definition.Bindings is null || definition.ActionBindings is null)
            return ["模板报警、面板或绑定配置无效"];
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, Tag>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in definition.Tags)
        {
            if (item is null || !ValidKey(item.Key) || !keys.Add(item.Key)) { errors.Add("测点逻辑键无效或重复"); continue; }
            if (item.Properties is null) { errors.Add($"{item.Key} 测点定义不能为空"); continue; }
            var tag = ObjectTemplateJson.Copy(item.Properties);
            tag.Id = tags.Count + 1; tag.DeviceId = 1;
            BindTemplateAddress(tag, item.AddressOffset, "example");
            if (string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 50 || !names.Add(tag.Name)) errors.Add($"{item.Key} 名称须为不重复的 1～50 个字符");
            var error = ConfigurationValidator.Tag(tag);
            if (error is not null) errors.Add($"{item.Key}：{error}");
            tags[item.Key] = tag;
        }
        if (forPublish && tags.Values.Select(x => x.Area == TagArea.Address ? 3 : x.Area == TagArea.OpcNode ? 2 : ConfigurationValidator.IsS7(x.Area) ? 1 : 0).Distinct().Count() > 1)
            errors.Add("同一对象模板不能混合不同协议的地址区域");
        keys.Clear();
        foreach (var item in definition.Alarms)
        {
            if (item is null || !ValidKey(item.Key) || !keys.Add(item.Key)) { errors.Add("报警逻辑键无效或重复"); continue; }
            if (item.Properties is null) { errors.Add($"{item.Key} 报警定义不能为空"); continue; }
            if (forPublish && (item.TagKey is null || !tags.ContainsKey(item.TagKey))) errors.Add($"{item.Key} 引用了不存在的测点逻辑键");
            var alarm = ObjectTemplateJson.Copy(item.Properties); alarm.TagId = 1;
            var error = ConfigurationValidator.Alarm(alarm);
            if (error is not null) errors.Add($"{item.Key}：{error}");
        }
        // Templates keep symbolic keys. Concrete tag IDs are only assigned when creating an instance.
        var panel = ObjectTemplateJson.Copy(definition.Panel);
        if (panel.Nodes is null) return [.. errors, "面板图元不能为空"];
        foreach (var node in panel.Nodes)
        {
            if (node is null) continue;
            if (node.TagId is not null || node.Action?.TagId is not null || node.AlarmTagId is not null || node.Tags is { Count: > 0 } || node.Animation?.TagId is not null)
                errors.Add("模板面板必须通过测点逻辑键绑定，不能保存现场测点编号");
        }
        errors.AddRange(MaterializePanel(panel, definition.Bindings, definition.ActionBindings, tags, forPublish));
        errors.AddRange(ScreenValidator.Validate(panel, forPublish ? tags.Values.ToDictionary(x => x.Id) : null).Select(x => $"面板 {x.NodeId}：{x.Message}"));
        if (forPublish)
        {
            var nodeIds = panel.Nodes.Where(n => n is not null).Select(n => n.Id).ToHashSet();
            foreach (var id in definition.Bindings.Keys.Concat(definition.ActionBindings.Keys))
                if (!nodeIds.Contains(id)) errors.Add($"面板绑定 {id} 对应的图元不存在");
        }
        return errors;
    }
    /// <summary>
    /// {address} 展开后，软元件地址只留在 NodeId。寄存器号必须回到 0，
    /// 否则“文本地址不能同时填写寄存器地址”会拒绝任何非零基址。
    /// 节点已带元件前缀时，{address} 只替换数字；裸 {address} 换成完整软元件。
    /// </summary>
    public static void BindTemplateAddress(Tag tag, int address, string instanceName) =>
        BindTemplateAddress(tag, new AddressOrigin("", address, address.ToString(System.Globalization.CultureInfo.InvariantCulture)), 0, instanceName);

    public static void BindTemplateAddress(Tag tag, AddressOrigin origin, int offset, string instanceName)
    {
        var number = origin.Number + offset;
        var node = tag.NodeId ?? "";
        var token = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (tag.Area == TagArea.Address && !ElementPrefix(node).IsMatch)
            token = origin.Element(offset);
        tag.NodeId = node.Replace("{instance}", instanceName).Replace("{address}", token);
        if (tag.Area == TagArea.Address)
        {
            tag.Address = 0;
            tag.BitOffset = 0;
        }
        else tag.Address = number;
    }

    /// <summary>
    /// 软元件模板的起点是一段地址文本。同一模板里 D 和 M 不能共用一个序号。
    /// 只写了数字、且所有带 {address} 的测点前缀一致时，沿用该前缀，兼容已生成的实例。
    /// </summary>
    public static string? ResolveOrigin(ObjectTemplateDocument definition, string? startAddress, int baseAddress, out AddressOrigin origin)
    {
        origin = default;
        var textTags = definition.Tags.Where(x => x?.Properties?.Area == TagArea.Address).ToList();
        var prefixes = textTags.Select(x => ElementPrefix(x.Properties.NodeId)).Where(x => x.IsMatch).Select(x => x.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var text = startAddress?.Trim() ?? "";
        if (text.Length == 0)
        {
            if (baseAddress is < 0 or > 65535) return "基址须在 0～65535 之间";
            if (textTags.Count == 0) { origin = new("", baseAddress, baseAddress.ToString(System.Globalization.CultureInfo.InvariantCulture)); return null; }
            if (prefixes.Count != 1) return "软元件模板的起点须写成 D1000 这样的地址，不能只写寄存器号";
            origin = new(prefixes[0], baseAddress, prefixes[0] + baseAddress.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(text, "^([A-Za-z]*)(\\d+)$");
        if (!match.Success) return "起点地址须为寄存器号或软元件，例如 100 或 D1000";
        if (!int.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number is < 0 or > 65535)
            return "起点地址的编号须在 0～65535 之间";
        var prefix = match.Groups[1].Value;
        if (textTags.Count == 0 && prefix.Length > 0) return "寄存器模板的起点只能是 0～65535 的编号";
        if (textTags.Count > 0 && prefix.Length == 0) return "软元件模板的起点须写成 D1000 这样的地址，不能只写寄存器号";
        foreach (var item in textTags)
        {
            var element = ElementPrefix(item.Properties.NodeId);
            if (element.IsMatch && !element.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return $"{item.Key} 的地址 {item.Properties.NodeId} 与起点 {text} 不是同一软元件";
        }
        origin = new(prefix, number, text);
        return null;
    }

    private readonly record struct ElementMatch(bool IsMatch, string Prefix);
    private static ElementMatch ElementPrefix(string? nodeId)
    {
        var node = nodeId ?? "";
        var at = node.IndexOf("{address}", StringComparison.Ordinal);
        if (at < 0) return new(false, "");
        var prefix = node[..at];
        return prefix.Length > 0 && prefix.All(char.IsLetter) ? new(true, prefix) : new(false, "");
    }

    /// <summary>图库占位符和逻辑键表都展开成现场测点编号，展开后的画面不再保留占位符。</summary>
    public static List<string> MaterializePanel(ScreenDocument panel, IReadOnlyDictionary<string, string>? bindings, IReadOnlyDictionary<string, string>? actionBindings, IReadOnlyDictionary<string, Tag> tags, bool required)
    {
        var errors = new List<string>();
        bindings ??= new Dictionary<string, string>();
        actionBindings ??= new Dictionary<string, string>();
        foreach (var node in panel.Nodes ?? [])
        {
            if (node is null) continue;
            var key = First(node.Bind, bindings, node.Id);
            if (key is null) { /* 图元可以不绑测点 */ }
            else if (!tags.TryGetValue(key, out var tag)) { node.Bind = null; if (required) errors.Add($"图元 {node.Id} 的输入逻辑键不存在"); }
            else { node.TagId = tag.Id; node.Bind = null; }
            if (node.AlarmBind is not null)
            {
                if (!tags.TryGetValue(node.AlarmBind, out var alarmTag)) { if (required) errors.Add($"图元 {node.Id} 的报警逻辑键不存在"); }
                else { node.AlarmTagId = alarmTag.Id; node.AlarmBind = null; }
            }
            if (node.Action is { Type: "command" })
            {
                var actionKey = First(node.Action.Bind, actionBindings, node.Id);
                if (actionKey is not null && tags.TryGetValue(actionKey, out var actionTag)) { node.Action.TagId = actionTag.Id; node.Action.Bind = null; }
                else if (!required) node.Action.Type = "none";
                else { node.Action.Bind = null; errors.Add($"图元 {node.Id} 的控制逻辑键不存在"); }
            }
            else if (node.Action?.Bind is not null) node.Action.Bind = null;
            if (node.AnimationBind is not null && node.Animation is not null)
            {
                if (!tags.TryGetValue(node.AnimationBind, out var animTag)) { if (required) errors.Add($"图元 {node.Id} 的动画逻辑键不存在"); }
                else { node.Animation.TagId = animTag.Id; node.AnimationBind = null; }
            }
            if (node.TagBinds is { Count: > 0 })
            {
                var ids = new List<int>();
                foreach (var bind in node.TagBinds)
                {
                    if (bind is not null && tags.TryGetValue(bind, out var chartTag)) ids.Add(chartTag.Id);
                    else if (required) errors.Add($"图元 {node.Id} 的图表逻辑键不存在");
                }
                node.Tags = ids;
                node.TagBinds = null;
            }
            if (node.Commands is { Count: > 0 })
            {
                node.Commands = node.Commands.Select(command =>
                {
                    // 元素声明非空，但工程包导入的 JSON 里可能出现 [null]；保留原样而不是抛 NRE。
                    if (command is null || command.Bind is null) return command;
                    if (!tags.TryGetValue(command.Bind, out var commandTag))
                    {
                        if (required) errors.Add($"图元 {node.Id} 的面板命令逻辑键不存在");
                        return command;
                    }
                    return command with { TagId = commandTag.Id, Bind = null };
                }).Cast<ScreenPanelCommand>().ToList();
            }
        }
        return errors;
    }

    private static string? First(string? bind, IReadOnlyDictionary<string, string> map, string? id)
    {
        if (!string.IsNullOrWhiteSpace(bind)) return bind;
        return id is not null && map.TryGetValue(id, out var key) && !string.IsNullOrWhiteSpace(key) ? key : null;
    }

    public static bool ValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// 配置校验入口。对外仍然提供返回首条文案的 <c>Device</c>/<c>Tag</c>/<c>Channel</c>（调用点大多是
/// <c>is string error</c> 的判定），同时提供返回 <see cref="ValidationIssue"/> 列表的 <c>*Issues</c> 版本：
/// 表单可以一次标红所有字段，界面按错误码本地化，不再匹配中文文案。
/// </summary>
public static class ConfigurationValidator
{
    private static ValidationIssue Issue(string code, string? field, string message) => new(code, field, message);

    /// <summary>通道是物理链路的唯一权威：端点参数只在这里校验。设备侧的端点由通道下发。</summary>
    public static string? Channel(AcquisitionChannel channel, Scada.Core.Options.ScadaSettings? settings = null) =>
        ChannelIssues(channel, settings).FirstMessage();

    public static IReadOnlyList<ValidationIssue> ChannelIssues(AcquisitionChannel channel, Scada.Core.Options.ScadaSettings? settings = null)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(channel.Name) || channel.Name.Length > 100)
            issues.Add(Issue("channel.name", nameof(channel.Name), "通道名称须为 1～100 个字符"));
        if (channel.Description?.Length > 500)
            issues.Add(Issue("channel.description", nameof(channel.Description), "通道描述过长"));
        if (!Enum.IsDefined(channel.Protocol))
            issues.Add(Issue("channel.protocol", nameof(channel.Protocol), "通道协议无效"));
        else if (!ChannelProtocols.Supports(channel.Protocol))
            issues.Add(Issue("channel.protocol.shared", nameof(channel.Protocol),
                "只有 Modbus，以及汇川、信捷、台达的 RTU，允许共享通道；OPC UA、S7、MQTT 等协议须为每台设备单独建立会话"));
        if (channel.TimeoutMs is < 100 or > 60000)
            issues.Add(Issue("channel.timeout", nameof(channel.TimeoutMs), "通道超时须在 100～60000ms 之间"));
        if (ChannelProtocols.UsesSerial(channel.Protocol))
        {
            if (Scada.Core.Protocols.SerialSettings.Validate(channel.SerialPort, channel.BaudRate, channel.DataBits, channel.Parity, channel.StopBits) is string serial)
                issues.Add(Issue("channel.serial", nameof(channel.SerialPort), serial));
            return issues;
        }
        if (string.IsNullOrWhiteSpace(channel.Host) || channel.Host.Length > 255 || channel.Port is < 1 or > 65535)
            issues.Add(Issue("channel.host", nameof(channel.Host), "通道主机或端口无效"));
        else if (Scada.Core.Protocols.OutboundTargetPolicy.Validate(channel.Host, settings) is string target)
            issues.Add(Issue("channel.target", nameof(channel.Host), target));
        return issues;
    }

    public static string? Device(Device device, Scada.Core.Options.ScadaSettings? settings = null, Scada.Core.Interfaces.IProtocolDriverAvailability? drivers = null) =>
        DeviceIssues(device, settings, drivers).FirstMessage();

    public static IReadOnlyList<ValidationIssue> DeviceIssues(Device device, Scada.Core.Options.ScadaSettings? settings = null,
        Scada.Core.Interfaces.IProtocolDriverAvailability? drivers = null)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(device.Name) || device.Name.Length > 100)
            issues.Add(Issue("device.name", nameof(device.Name), "设备名称须为 1～100 个字符"));
        if (!Enum.IsDefined(device.Protocol) || !Enum.IsDefined(device.ByteOrder))
            issues.Add(Issue("device.protocol", nameof(device.Protocol), "协议或字节序无效"));
        if (device.PollIntervalMs is < 100 or > 600000 || device.TimeoutMs is < 100 or > 60000)
            issues.Add(Issue("device.polling", nameof(device.PollIntervalMs), "采集周期或超时超出允许范围"));
        // 仿真设备不连接现场，协议连接参数可以留待切回真实设备时再补；仿真参数本身仍要校验。
        if (Scada.Core.Simulation.SimulationOptions.Validate(device) is string simulation)
            issues.Add(Issue("device.simulation", nameof(device.SimulationOptions), simulation));
        if (device.ChannelId is int channelId)
        {
            if (channelId <= 0)
                issues.Add(Issue("device.channel", nameof(device.ChannelId), "通道标识无效"));
            else
            {
                if (device.GatewayId is not null)
                    issues.Add(Issue("device.channel", nameof(device.ChannelId), "网关设备的采集由网关执行，不能绑定本机通道"));
                if (device.SimulationEnabled)
                    issues.Add(Issue("device.channel", nameof(device.ChannelId), "仿真设备不占用物理通道，请先关闭仿真"));
                if (!ChannelProtocols.Supports(device.Protocol))
                    issues.Add(Issue("device.channel", nameof(device.ChannelId), "该协议无法共享物理通道；只有 Modbus，以及汇川、信捷、台达的 RTU，支持绑定通道"));
                else if (Scada.Core.Protocols.SoftElementByteOrder.Applies(device.Protocol) && !Scada.Core.Protocols.SoftElementByteOrder.IsRtu(device))
                    issues.Add(Issue("device.channel", nameof(device.ChannelId), "汇川、信捷、台达只有 RTU 可以绑定通道，TCP 保持一设备一条连接"));
            }
        }
        if (device.SimulationEnabled) return issues;
        if (Scada.Core.Protocols.ProtocolCatalog.TryGet(device.Protocol, out var spec))
        {
            if (spec.UsesHostPort && !Scada.Core.Protocols.InovanceAddress.UsesSerial(device) && !Scada.Core.Protocols.XinjeAddress.UsesSerial(device)
                && !Scada.Core.Protocols.DeltaAddress.UsesSerial(device) && Scada.Core.Protocols.ProtocolCatalog.HostPort(device, settings) is string hostError)
                issues.Add(Issue("device.host", nameof(device.Host), hostError));
            if (!spec.UsesCredentials && (!string.IsNullOrEmpty(device.OpcUserName) || device.OpcPasswordProtected is not null))
                issues.Add(Issue("device.credentials", nameof(device.OpcUserName), "此协议不使用用户名密码"));
            if (device.OpcUserName.Length > 200)
                issues.Add(Issue("device.credentials", nameof(device.OpcUserName), "用户名过长"));
            var protocolError = spec is Scada.Core.Protocols.Iec61850Spec iec
                ? iec.ValidateDevice(device, drivers)
                : spec.ValidateDevice(device);
            if (protocolError is not null)
                issues.Add(Issue($"device.protocol.{device.Protocol}", nameof(device.ProtocolOptions), protocolError));
            return issues;
        }
        if (device.ProtocolOptions.Length > 0)
            issues.Add(Issue("device.protocolOptions", nameof(device.ProtocolOptions), "此协议不使用协议参数"));
        if (device.Protocol is not (ProtocolType.ModbusTcp or ProtocolType.ModbusRtu or ProtocolType.ModbusAscii or ProtocolType.ModbusRtuOverTcp or ProtocolType.SiemensS7 or ProtocolType.OpcUa))
            issues.Add(Issue("device.protocol.unsupported", nameof(device.Protocol), $"协议 {device.Protocol} 尚未启用"));
        if (device.Protocol == ProtocolType.OpcUa)
        {
            if (!Uri.TryCreate(device.OpcEndpointUrl, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp" || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || device.OpcEndpointUrl.Length > 1024)
                issues.Add(Issue("device.opc.endpoint", nameof(device.OpcEndpointUrl), "OPC UA 端点须为不含凭据的 opc.tcp:// 地址"));
            else if (Scada.Core.Protocols.OutboundTargetPolicy.Validate(uri.Host, settings) is string targetError)
                issues.Add(Issue("device.host", nameof(device.OpcEndpointUrl), targetError));
            if (!Enum.IsDefined(device.OpcSecurityMode) || !Enum.IsDefined(device.OpcSecurityPolicy))
                issues.Add(Issue("device.opc.security", nameof(device.OpcSecurityMode), "OPC UA 安全模式或策略无效"));
            else if ((device.OpcSecurityMode == OpcSecurityMode.None) != (device.OpcSecurityPolicy == OpcSecurityPolicy.None))
                issues.Add(Issue("device.opc.security", nameof(device.OpcSecurityMode), "无安全模式须配合 None 策略，安全模式须选择加密策略"));
            if (device.OpcUserName.Length > 200)
                issues.Add(Issue("device.credentials", nameof(device.OpcUserName), "OPC UA 用户名过长"));
            if (!string.IsNullOrEmpty(device.OpcUserName) && device.OpcSecurityMode != OpcSecurityMode.SignAndEncrypt)
                issues.Add(Issue("device.opc.security", nameof(device.OpcSecurityMode), "用户名密码认证须使用 SignAndEncrypt"));
        }
        else if (device.Protocol is ProtocolType.ModbusRtu or ProtocolType.ModbusAscii)
        {
            if (Scada.Core.Protocols.SerialSettings.Validate(device.SerialPort, device.BaudRate, device.DataBits, device.Parity, device.StopBits) is string serial)
                issues.Add(Issue("device.serial", nameof(device.SerialPort), serial));
            if (Scada.Core.Protocols.SerialSettings.ValidateStation(device.StationNumber) is string station)
                issues.Add(Issue("device.station", nameof(device.StationNumber), station));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(device.Host) || device.Host.Length > 255 || device.Port is < 1 or > 65535)
                issues.Add(Issue("device.host", nameof(device.Host), "设备主机或端口无效"));
            else if (Scada.Core.Protocols.OutboundTargetPolicy.Validate(device.Host, settings) is string targetError)
                issues.Add(Issue("device.host", nameof(device.Host), targetError));
            if (device.Protocol == ProtocolType.SiemensS7 && (!Enum.IsDefined(device.Cpu) || device.Rack is < 0 or > 7 || device.Slot is < 0 or > 31))
                issues.Add(Issue("device.s7", nameof(device.Cpu), "S7 CPU、机架或插槽无效"));
        }
        return issues;
    }

    public static bool IsS7(TagArea area) => area is TagArea.DataBlock or TagArea.Memory or TagArea.Input or TagArea.Output;

    public static string? Tag(Tag tag, Device? device = null) => TagIssues(tag, device).FirstMessage();

    public static IReadOnlyList<ValidationIssue> TagIssues(Tag tag, Device? device = null)
    {
        var issues = new List<ValidationIssue>();
        if (!Enum.IsDefined(tag.Kind)) issues.Add(Issue("tag.kind", nameof(tag.Kind), "测点来源无效"));
        if (Scada.Core.Entities.ScanCadence.Validate(tag) is string scanError) issues.Add(Issue("tag.scanRate", nameof(tag.ScanRateMs), scanError));
        if (tag.Kind == TagKind.Physical && tag.DeviceId is not > 0) issues.Add(Issue("tag.device", nameof(tag.DeviceId), "必须指定设备"));
        if (string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 100) issues.Add(Issue("tag.name", nameof(tag.Name), "测点名称须为 1～100 个字符"));
        if (!Enum.IsDefined(tag.Area) || !Enum.IsDefined(tag.DataType) || !Enum.IsDefined(tag.ArchiveMode))
            issues.Add(Issue("tag.type", nameof(tag.DataType), "测点类型无效"));
        if (tag.DataType == TagDataType.String)
        {
            var maxTextLength = device?.Protocol switch
            {
                ProtocolType.SiemensS7 => 198,
                ProtocolType.ModbusTcp or ProtocolType.ModbusRtu or ProtocolType.ModbusAscii or ProtocolType.ModbusRtuOverTcp => 250,
                ProtocolType.OpcUa => 254,
                // 模板保存时没有具体协议，按最严格的 S7 上限校验，实例化时再按设备协议放宽。
                _ => 198
            };
            if (tag.TextLength < 1 || tag.TextLength > maxTextLength) issues.Add(Issue("tag.text.length", nameof(tag.TextLength), $"字符串长度须为 1～{maxTextLength} 字节"));
            if (tag.Scale != 1 || tag.Offset != 0) issues.Add(Issue("tag.text.scale", nameof(tag.Scale), "字符串测点不支持倍率或偏移"));
            if (tag.Deadband != 0) issues.Add(Issue("tag.text.deadband", nameof(tag.Deadband), "字符串测点不支持死区"));
            if (tag.MinValue is not null || tag.MaxValue is not null) issues.Add(Issue("tag.text.range", nameof(tag.MinValue), "字符串测点不支持量程"));
            if (tag.BitOffset != 0) issues.Add(Issue("tag.text.bitOffset", nameof(tag.BitOffset), "字符串测点不能设置位偏移"));
            if (device is not null && device.Protocol is not (ProtocolType.OpcUa or ProtocolType.ModbusTcp or ProtocolType.ModbusRtu or ProtocolType.ModbusAscii or ProtocolType.ModbusRtuOverTcp or ProtocolType.SiemensS7))
                issues.Add(Issue("tag.text.protocol", nameof(tag.DataType), "该协议不支持字符串测点"));
            return issues;
        }
        if (tag.TextLength != 0) issues.Add(Issue("tag.textLength", nameof(tag.TextLength), "只有字符串测点可以设置文本长度"));
        if (!double.IsFinite(tag.Deadband) || tag.Deadband < 0) issues.Add(Issue("tag.deadband", nameof(tag.Deadband), "死区不能为负数或非有限值"));
        if (tag.ArchiveMode == ArchiveMode.Periodic && tag.ArchivePeriodSec < 1) issues.Add(Issue("tag.archivePeriod", nameof(tag.ArchivePeriodSec), "周期存储的周期必须大于 0"));
        if (tag.Description?.Length > 500 || tag.Unit?.Length > 32) issues.Add(Issue("tag.text.tooLong", nameof(tag.Description), "描述或单位过长"));
        if (tag.Kind == TagKind.Calculated)
        {
            if (tag.DeviceId is not null || tag.Writable || tag.DataType != TagDataType.Double || tag.Scale != 1 || tag.Offset != 0)
                issues.Add(Issue("tag.calculated.shape", nameof(tag.Kind), "计算测点须独立于设备、只读并使用未缩放的 Double 类型"));
            if (tag.CalculationIntervalMs is < 100 or > 600000 || tag.InputMaxAgeMs is < 100 or > 3600000)
                issues.Add(Issue("tag.calculated.interval", nameof(tag.CalculationIntervalMs), "计算周期须为 100～600000ms，输入有效期须为 100～3600000ms"));
            try { Scada.Core.Calculations.TagExpression.Compile(tag.Expression); }
            catch (Scada.Core.Calculations.TagExpressionException ex) { issues.Add(Issue("tag.calculated.expression", nameof(tag.Expression), ex.Message)); }
            return issues;
        }
        if (Scada.Core.Protocols.PointAddress.ValidateShape(tag) is string shape) issues.Add(Issue("tag.address.shape", nameof(tag.NodeId), shape));
        var text = tag.Area == TagArea.Address;
        if (device is not null && text != Scada.Core.Protocols.ProtocolCatalog.IsTextAddress(device.Protocol))
            issues.Add(Issue("tag.address.mismatch", nameof(tag.Area), "测点区域与设备协议不匹配"));
        if (text)
        {
            if (string.IsNullOrWhiteSpace(tag.NodeId) || tag.NodeId.Length > 1024) issues.Add(Issue("tag.address.text", nameof(tag.NodeId), "地址须为 1～1024 个字符"));
            if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset) || tag.Writable && tag.Scale == 0)
                issues.Add(Issue("tag.scale", nameof(tag.Scale), "倍率或偏移无效，可写测点的倍率不能为零"));
            if (tag.MinValue is double tmin && !double.IsFinite(tmin) || tag.MaxValue is double tmax && !double.IsFinite(tmax))
                issues.Add(Issue("tag.range", nameof(tag.MinValue), "量程必须是有限值"));
            if (tag.MinValue > tag.MaxValue) issues.Add(Issue("tag.range.order", nameof(tag.MinValue), "最小值不能大于最大值"));
            if (device is not null && Scada.Core.Protocols.ProtocolCatalog.TryGet(device.Protocol, out var spec)
                && spec.ValidateTag(tag, device) is string textError)
                issues.Add(Issue($"tag.address.{device.Protocol}", nameof(tag.NodeId), textError));
            return issues;
        }
        var s7 = IsS7(tag.Area);
        var opc = tag.Area == TagArea.OpcNode;
        if (device is not null && (opc != (device.Protocol == ProtocolType.OpcUa) || s7 != (device.Protocol == ProtocolType.SiemensS7)))
            issues.Add(Issue("tag.address.mismatch", nameof(tag.Area), "测点区域与设备协议不匹配"));
        if (opc && !OpcNodeAddress.IsValid(tag.NodeId)) issues.Add(Issue("tag.address.opc", nameof(tag.NodeId), "OPC UA NodeId 无效（支持 ns=、nsu= 及 i/s/g/b 标识）"));
        if (!opc && (tag.Address < 0 || (long)tag.Address + (s7 ? Scada.Core.Protocols.PointAddress.ByteCount(tag) : Scada.Core.Protocols.PointAddress.RegisterCount(tag)) - 1 > 65535))
            issues.Add(Issue("tag.address.range", nameof(tag.Address), "地址及数据长度超出 0～65535 范围"));
        if (s7 && (tag.DbNumber is < 1 or > 65535 || tag.BitOffset is < 0 or > 7 || tag.DataType != TagDataType.Bool && tag.BitOffset != 0))
            issues.Add(Issue("tag.address.s7", nameof(tag.DbNumber), "S7 DB 编号或位偏移无效，非 Bool 测点的位偏移须为 0"));
        if (tag.Area is TagArea.Coil or TagArea.DiscreteInput && tag.DataType != TagDataType.Bool)
            issues.Add(Issue("tag.type.bool", nameof(tag.DataType), "线圈/离散输入区域只能使用 Bool 类型"));
        if (tag.Writable && tag.Area is TagArea.DiscreteInput or TagArea.InputRegister or TagArea.Input)
            issues.Add(Issue("tag.writable.readonly", nameof(tag.Writable), "只读区域不能设置为可写"));
        if (!double.IsFinite(tag.Deadband) || tag.Deadband < 0) issues.Add(Issue("tag.deadband", nameof(tag.Deadband), "死区不能为负数或非有限值"));
        if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset) || tag.Writable && tag.Scale == 0)
            issues.Add(Issue("tag.scale", nameof(tag.Scale), "倍率或偏移无效，可写测点的倍率不能为零"));
        if (tag.ArchiveMode == ArchiveMode.Periodic && tag.ArchivePeriodSec < 1) issues.Add(Issue("tag.archivePeriod", nameof(tag.ArchivePeriodSec), "周期存储的周期必须大于 0"));
        if (tag.MinValue is double min && !double.IsFinite(min) || tag.MaxValue is double max && !double.IsFinite(max))
            issues.Add(Issue("tag.range", nameof(tag.MinValue), "量程必须是有限值"));
        if (tag.MinValue > tag.MaxValue) issues.Add(Issue("tag.range.order", nameof(tag.MinValue), "最小值不能大于最大值"));
        if (tag.Description?.Length > 500 || tag.Unit?.Length > 32) issues.Add(Issue("tag.text.tooLong", nameof(tag.Description), "描述或单位过长"));
        return issues;
    }

    public static string? Alarm(AlarmRule alarm) => AlarmIssues(alarm).FirstMessage();

    public static IReadOnlyList<ValidationIssue> AlarmIssues(AlarmRule alarm)
    {
        var issues = new List<ValidationIssue>();
        if (alarm.TagId <= 0) issues.Add(Issue("alarm.tag", nameof(alarm.TagId), "必须指定测点"));
        if (!Enum.IsDefined(alarm.Type) || !Enum.IsDefined(alarm.Level)) issues.Add(Issue("alarm.type", nameof(alarm.Type), "报警类型或级别无效"));
        if (!Enum.IsDefined(alarm.ActiveWhen)) issues.Add(Issue("alarm.activeWhen", nameof(alarm.ActiveWhen), "位报警触发条件无效"));
        if (!Enum.IsDefined(alarm.Category) || !Enum.IsDefined(alarm.Consequence)) issues.Add(Issue("alarm.classification", nameof(alarm.Category), "报警类别或后果无效"));
        if (alarm.ResponseTimeSec is < 0 or > 86400) issues.Add(Issue("alarm.responseTime", nameof(alarm.ResponseTimeSec), "允许响应时间须在 0～86400 秒之间"));
        if (alarm.AutoPriority && alarm.ResponseTimeSec <= 0) issues.Add(Issue("alarm.autoPriority", nameof(alarm.ResponseTimeSec), "自动优先级需要填写允许响应时间"));
        if (!double.IsFinite(alarm.Threshold)) issues.Add(Issue("alarm.threshold", nameof(alarm.Threshold), "报警阈值无效"));
        if (alarm.DelaySec < 0 || !double.IsFinite(alarm.RecoverDeadband) || alarm.RecoverDeadband < 0)
            issues.Add(Issue("alarm.delay", nameof(alarm.DelaySec), "报警延时或恢复死区无效"));
        if (alarm.Message?.Length > 500) issues.Add(Issue("alarm.message.length", nameof(alarm.Message), "报警消息不能超过 500 个字符"));
        if (alarm.Message is { Length: > 0 } message && AlarmRuleText.IsPlaceholderOnly(message))
            issues.Add(Issue("alarm.message.placeholderOnly", nameof(alarm.Message), "报警消息不能只写报警类型，请说明现象与处置（可留空自动生成）"));
        if (alarm.Cause?.Length > 200 || alarm.Action?.Length > 200) issues.Add(Issue("alarm.description", nameof(alarm.Cause), "原因与处置各不能超过 200 个字符"));
        return issues;
    }
}
