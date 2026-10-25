using System.Globalization;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public sealed class BacnetDeviceOptions
{
    /// <summary>目标设备对象实例号（Device Instance，0～4194302），用于 Who-Is 定向与 I-Am 核对。</summary>
    public int DeviceInstance { get; set; } = -1;
    /// <summary>本机 UDP 端口，0 = 系统分配。设备只回 47808 时设为 47808（同机不可与其他 BACnet 软件共用）。</summary>
    public int LocalPort { get; set; }
    /// <summary>连接时向设备单播 Who-Is 并等待匹配的 I-Am；关闭则仅用 ReadProperty 读取设备对象确认可达。</summary>
    public bool WhoIs { get; set; } = true;
    public bool ReadMultiple { get; set; } = true;
    public int MaxObjectsPerRequest { get; set; } = 16;
    /// <summary>默认写优先级 1～16（8 = 手动操作员）。测点可用 ;pri= 覆盖。</summary>
    public int WritePriority { get; set; } = 8;
    /// <summary>读请求的重发次数（写命令不重发）。</summary>
    public int ReadRetries { get; set; } = 1;
    public bool UseCov { get; set; }
    public int CovLifetimeSec { get; set; } = 300;
}

public enum BacnetObjectKind { AI, AO, AV, BI, BO, BV, MSV }

/// <summary>BACnet 测点地址：<c>AI:1</c>、<c>AV:10;pri=12</c>、<c>BO:3</c>、<c>MSV:2</c>，读取 Present_Value 与 Status_Flags。</summary>
public sealed partial record BacnetPointAddress(BacnetObjectKind Kind, uint Instance, int? Priority)
{
    public bool IsBinary => Kind is BacnetObjectKind.BI or BacnetObjectKind.BO or BacnetObjectKind.BV;
    public bool IsCommandable => Kind is BacnetObjectKind.AO or BacnetObjectKind.AV or BacnetObjectKind.BO or BacnetObjectKind.BV or BacnetObjectKind.MSV;

    [GeneratedRegex(@"^(AI|AO|AV|BI|BO|BV|MSV):(\d{1,7})(?:;pri=(\d{1,2}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string text, out BacnetPointAddress address, out string? error)
    {
        address = null!; error = null;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) { error = "BACnet 地址格式应为 对象类型:实例号[;pri=1～16]，对象类型：AI/AO/AV/BI/BO/BV/MSV"; return false; }
        var instance = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (instance > 4194302) { error = "对象实例号须为 0～4194302"; return false; }
        int? pri = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : null;
        if (pri is < 1 or > 16) { error = "写优先级须为 1～16"; return false; }
        if (pri is 6) { error = "优先级 6 保留给最小开/关时间，不能用于写入"; return false; }
        address = new(Enum.Parse<BacnetObjectKind>(m.Groups[1].Value, true), instance, pri);
        return true;
    }
}

public sealed class BacnetSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.BacnetIp;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;
    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<BacnetDeviceOptions>(device, out var o) is string error) return error;
        if (o.DeviceInstance is < 0 or > 4194302) return "须填写设备实例号（0～4194302）";
        if (o.LocalPort is < 0 or > 65535) return "本机端口须为 0～65535";
        if (o.MaxObjectsPerRequest is < 1 or > 64) return "单次读取对象数须为 1～64";
        if (o.WritePriority is < 1 or > 16 || o.WritePriority == 6) return "写优先级须为 1～16（6 保留）";
        if (o.ReadRetries is < 0 or > 3) return "读重发次数须为 0～3";
        if (o.CovLifetimeSec is < 60 or > 28800) return "COV 订阅时长须为 60～28800 秒";
        return null;
    }
    public string? ValidateTag(Tag tag, Device? device)
    {
        if (!BacnetPointAddress.TryParse(tag.NodeId, out var a, out var error)) return error;
        if (a.IsBinary && tag.DataType != TagDataType.Bool) return "二值对象（BI/BO/BV）须为 Bool 类型";
        if (!a.IsBinary && tag.DataType == TagDataType.Bool) return "Bool 测点须对应 BI/BO/BV 对象";
        if (a.Kind == BacnetObjectKind.MSV && tag.DataType is TagDataType.Float or TagDataType.Double) return "多态值（MSV）状态号为正整数，请使用整数类型";
        if (tag.Writable && !a.IsCommandable) return "只有 AO/AV/BO/BV/MSV 可写";
        if (!tag.Writable && a.Priority is not null) return "pri 仅用于可写测点";
        return null;
    }
}
