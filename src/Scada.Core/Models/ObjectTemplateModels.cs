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

public sealed record ObjectInstanceParameters(string Name, int DeviceId, int BaseAddress);
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
            tag.Id = tags.Count + 1; tag.DeviceId = 1; tag.Address = item.AddressOffset;
            tag.NodeId = (tag.NodeId ?? "").Replace("{instance}", "example").Replace("{address}", item.AddressOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            if (node is null || node.Action is null) continue;
            if (node.TagId is not null || node.Action.TagId is not null) errors.Add("模板面板必须通过测点逻辑键绑定，不能保存现场测点编号");
            if (definition.Bindings.TryGetValue(node.Id ?? "", out var key))
            {
                if (key is not null && tags.TryGetValue(key, out var tag)) node.TagId = tag.Id;
                else if (forPublish) errors.Add($"图元 {node.Id} 的输入逻辑键不存在");
            }
            if (node.Action.Type == "command")
            {
                if (definition.ActionBindings.TryGetValue(node.Id ?? "", out var actionKey) && actionKey is not null && tags.TryGetValue(actionKey, out var tag)) node.Action.TagId = tag.Id;
                else if (!forPublish) node.Action.Type = "none";
            }
        }
        errors.AddRange(ScreenValidator.Validate(panel, forPublish ? tags.Values.ToDictionary(x => x.Id) : null).Select(x => $"面板 {x.NodeId}：{x.Message}"));
        if (forPublish)
        {
            var nodeIds = panel.Nodes.Where(n => n is not null).Select(n => n.Id).ToHashSet();
            foreach (var id in definition.Bindings.Keys.Concat(definition.ActionBindings.Keys))
                if (!nodeIds.Contains(id)) errors.Add($"面板绑定 {id} 对应的图元不存在");
        }
        return errors;
    }
    public static bool ValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex KeyPattern();
}

public static class ConfigurationValidator
{
    public static string? Device(Device device)
    {
        if (string.IsNullOrWhiteSpace(device.Name) || device.Name.Length > 100) return "设备名称须为 1～100 个字符";
        if (!Enum.IsDefined(device.Protocol) || !Enum.IsDefined(device.ByteOrder)) return "协议或字节序无效";
        if (device.PollIntervalMs is < 100 or > 600000 || device.TimeoutMs is < 100 or > 60000) return "采集周期或超时超出允许范围";
        if (Scada.Core.Protocols.ProtocolCatalog.TryGet(device.Protocol, out var spec))
        {
            if (spec.UsesHostPort && Scada.Core.Protocols.ProtocolCatalog.HostPort(device) is string hostError) return hostError;
            if (!spec.UsesCredentials && (!string.IsNullOrEmpty(device.OpcUserName) || device.OpcPasswordProtected is not null)) return "此协议不使用用户名密码";
            if (device.OpcUserName.Length > 200) return "用户名过长";
            return spec.ValidateDevice(device);
        }
        if (device.ProtocolOptions.Length > 0) return "此协议不使用协议参数";
        if (device.Protocol is not (ProtocolType.ModbusTcp or ProtocolType.ModbusRtu or ProtocolType.SiemensS7 or ProtocolType.OpcUa)) return $"协议 {device.Protocol} 尚未启用";
        if (device.Protocol == ProtocolType.OpcUa)
        {
            if (!Uri.TryCreate(device.OpcEndpointUrl, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp" || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || device.OpcEndpointUrl.Length > 1024) return "OPC UA 端点须为不含凭据的 opc.tcp:// 地址";
            if (!Enum.IsDefined(device.OpcSecurityMode) || !Enum.IsDefined(device.OpcSecurityPolicy)) return "OPC UA 安全模式或策略无效";
            if ((device.OpcSecurityMode == OpcSecurityMode.None) != (device.OpcSecurityPolicy == OpcSecurityPolicy.None)) return "无安全模式须配合 None 策略，安全模式须选择加密策略";
            if (device.OpcUserName.Length > 200) return "OPC UA 用户名过长";
            if (!string.IsNullOrEmpty(device.OpcUserName) && device.OpcSecurityMode != OpcSecurityMode.SignAndEncrypt) return "用户名密码认证须使用 SignAndEncrypt";
        }
        else if (device.Protocol == ProtocolType.ModbusRtu)
        {
            if (string.IsNullOrWhiteSpace(device.SerialPort) || device.SerialPort.Length > 128) return "串口名称无效";
            if (device.BaudRate is < 300 or > 921600 || device.DataBits != 8 || !Enum.IsDefined(device.Parity) || !Enum.IsDefined(device.StopBits)) return "串口通信参数无效，Modbus RTU 使用 8 个数据位";
            if (device.StationNumber is < 1 or > 247) return "RTU 采集站号须在 1～247 之间，不支持广播写入";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(device.Host) || device.Host.Length > 255 || device.Port is < 1 or > 65535) return "设备主机或端口无效";
            if (device.Protocol == ProtocolType.SiemensS7 && (!Enum.IsDefined(device.Cpu) || device.Rack is < 0 or > 7 || device.Slot is < 0 or > 31)) return "S7 CPU、机架或插槽无效";
        }
        return null;
    }

    public static bool IsS7(TagArea area) => area is TagArea.DataBlock or TagArea.Memory or TagArea.Input or TagArea.Output;

    public static string? Tag(Tag tag, Device? device = null)
    {
        if (!Enum.IsDefined(tag.Kind)) return "测点来源无效";
        if (tag.Kind == TagKind.Physical && tag.DeviceId is not > 0) return "必须指定设备";
        if (string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 100) return "测点名称须为 1～100 个字符";
        if (!Enum.IsDefined(tag.Area) || !Enum.IsDefined(tag.DataType) || !Enum.IsDefined(tag.ArchiveMode)) return "测点类型无效";
        if (!double.IsFinite(tag.Deadband) || tag.Deadband < 0) return "死区不能为负数或非有限值";
        if (tag.ArchiveMode == ArchiveMode.Periodic && tag.ArchivePeriodSec < 1) return "周期存储的周期必须大于 0";
        if (tag.Description?.Length > 500 || tag.Unit?.Length > 32) return "描述或单位过长";
        if (tag.Kind == TagKind.Calculated)
        {
            if (tag.DeviceId is not null || tag.Writable || tag.DataType != TagDataType.Double || tag.Scale != 1 || tag.Offset != 0) return "计算测点须独立于设备、只读并使用未缩放的 Double 类型";
            if (tag.CalculationIntervalMs is < 100 or > 600000 || tag.InputMaxAgeMs is < 100 or > 3600000) return "计算周期须为 100～600000ms，输入有效期须为 100～3600000ms";
            try { Scada.Core.Calculations.TagExpression.Compile(tag.Expression); }
            catch (Scada.Core.Calculations.TagExpressionException ex) { return ex.Message; }
            return null;
        }
        var text = tag.Area == TagArea.Address;
        if (device is not null && text != Scada.Core.Protocols.ProtocolCatalog.IsTextAddress(device.Protocol)) return "测点区域与设备协议不匹配";
        if (text)
        {
            if (string.IsNullOrWhiteSpace(tag.NodeId) || tag.NodeId.Length > 1024) return "地址须为 1～1024 个字符";
            if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset) || tag.Writable && tag.Scale == 0) return "倍率或偏移无效，可写测点的倍率不能为零";
            if (tag.MinValue is double tmin && !double.IsFinite(tmin) || tag.MaxValue is double tmax && !double.IsFinite(tmax)) return "量程必须是有限值";
            if (tag.MinValue > tag.MaxValue) return "最小值不能大于最大值";
            if (device is not null && Scada.Core.Protocols.ProtocolCatalog.TryGet(device.Protocol, out var spec)) return spec.ValidateTag(tag, device);
            return null;
        }
        var s7 = IsS7(tag.Area);
        var opc = tag.Area == TagArea.OpcNode;
        if (device is not null && (opc != (device.Protocol == ProtocolType.OpcUa) || s7 != (device.Protocol == ProtocolType.SiemensS7))) return "测点区域与设备协议不匹配";
        if (opc && !OpcNodeAddress.IsValid(tag.NodeId)) return "OPC UA NodeId 无效（支持 ns=、nsu= 及 i/s/g/b 标识）";
        if (!opc && (tag.Address < 0 || (long)tag.Address + (s7 ? tag.ByteCount : tag.RegisterCount) - 1 > 65535)) return "地址及数据长度超出 0～65535 范围";
        if (s7 && (tag.DbNumber is < 1 or > 65535 || tag.BitOffset is < 0 or > 7 || tag.DataType != TagDataType.Bool && tag.BitOffset != 0)) return "S7 DB 编号或位偏移无效，非 Bool 测点的位偏移须为 0";
        if (tag.Area is TagArea.Coil or TagArea.DiscreteInput && tag.DataType != TagDataType.Bool) return "线圈/离散输入区域只能使用 Bool 类型";
        if (tag.Writable && tag.Area is TagArea.DiscreteInput or TagArea.InputRegister or TagArea.Input) return "只读区域不能设置为可写";
        if (!double.IsFinite(tag.Deadband) || tag.Deadband < 0) return "死区不能为负数或非有限值";
        if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset) || tag.Writable && tag.Scale == 0) return "倍率或偏移无效，可写测点的倍率不能为零";
        if (tag.ArchiveMode == ArchiveMode.Periodic && tag.ArchivePeriodSec < 1) return "周期存储的周期必须大于 0";
        if (tag.MinValue is double min && !double.IsFinite(min) || tag.MaxValue is double max && !double.IsFinite(max)) return "量程必须是有限值";
        if (tag.MinValue > tag.MaxValue) return "最小值不能大于最大值";
        if (tag.Description?.Length > 500 || tag.Unit?.Length > 32) return "描述或单位过长";
        return null;
    }

    public static string? Alarm(AlarmRule alarm)
    {
        if (alarm.TagId <= 0) return "必须指定测点";
        if (!Enum.IsDefined(alarm.Type) || !Enum.IsDefined(alarm.Level)) return "报警类型或级别无效";
        if (!double.IsFinite(alarm.Threshold)) return "报警阈值无效";
        if (alarm.DelaySec < 0 || !double.IsFinite(alarm.RecoverDeadband) || alarm.RecoverDeadband < 0) return "报警延时或恢复死区无效";
        if (alarm.Message?.Length > 500) return "报警消息不能超过 500 个字符";
        return null;
    }
}
