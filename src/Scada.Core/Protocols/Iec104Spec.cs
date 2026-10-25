using System.Globalization;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public enum Iec104CommandMode { SelectExecute = 0, Direct = 1 }

public sealed class Iec104DeviceOptions
{
    /// <summary>ASDU 公共地址（站地址）1～65534。</summary>
    public int CommonAddress { get; set; } = 1;
    public int OriginatorAddress { get; set; }
    public int T1Sec { get; set; } = 15;
    public int T2Sec { get; set; } = 10;
    public int T3Sec { get; set; } = 20;
    public int K { get; set; } = 12;
    public int W { get; set; } = 8;
    /// <summary>周期总召唤（秒），0 = 仅连接建立与子站初始化结束时召唤。</summary>
    public int GiIntervalSec { get; set; } = 900;
    /// <summary>周期电度量召唤（秒），0 = 不召唤（仍接收自发上送的累计量）。</summary>
    public int CounterIntervalSec { get; set; }
    public bool ClockSync { get; set; } = true;
    /// <summary>子站时标按 UTC 解释；false 按服务器本地时区解释。</summary>
    public bool DeviceTimeUtc { get; set; } = true;
    public Iec104CommandMode CommandMode { get; set; } = Iec104CommandMode.SelectExecute;
    /// <summary>命令确认后等待激活终止（ACTTERM）；未收到时报告“结果未知”。</summary>
    public bool WaitActTerm { get; set; } = true;
}

public enum Iec104Monitor { SP, DP, ME_NA, ME_NB, ME_NC, IT, ST, BO }
public enum Iec104Command { SC, DC, SE_NA, SE_NB, SE_NC }

/// <summary>
/// 104 测点地址：<c>M_SP:1001</c>、<c>M_DP:2001</c>、<c>M_ME_NA|NB|NC:3001</c>、<c>M_IT:4001</c>、<c>M_ST:…</c>、<c>M_BO:…</c>；
/// 可写测点追加 <c>;cmd=C_SC|C_DC|C_SE_NA|C_SE_NB|C_SE_NC:5001</c>，可选 <c>;sbo=0|1</c> 覆盖设备的选择-执行设置。
/// </summary>
public sealed partial record Iec104Address(Iec104Monitor Monitor, int Ioa, Iec104Command? Command, int? CommandIoa, bool? SelectBeforeOperate)
{
    [GeneratedRegex(@"^M_(SP|DP|ME_NA|ME_NB|ME_NC|IT|ST|BO):(\d{1,8})$", RegexOptions.IgnoreCase)]
    private static partial Regex MonitorPattern();
    [GeneratedRegex(@"^C_(SC|DC|SE_NA|SE_NB|SE_NC):(\d{1,8})$", RegexOptions.IgnoreCase)]
    private static partial Regex CommandPattern();

    public static bool TryParse(string text, out Iec104Address address, out string? error)
    {
        address = null!; error = null;
        var parts = text.Split(';', StringSplitOptions.TrimEntries);
        var m = MonitorPattern().Match(parts[0]);
        if (!m.Success) { error = "104 地址应为 M_SP|M_DP|M_ME_NA|M_ME_NB|M_ME_NC|M_IT|M_ST|M_BO:信息对象地址"; return false; }
        var ioa = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (ioa is < 1 or > 0xFFFFFF) { error = "信息对象地址须为 1～16777215"; return false; }
        Iec104Command? cmd = null; int? cmdIoa = null; bool? sbo = null;
        foreach (var part in parts.Skip(1))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                var c = CommandPattern().Match(kv[1]);
                if (!c.Success) { error = "命令应为 C_SC|C_DC|C_SE_NA|C_SE_NB|C_SE_NC:信息对象地址"; return false; }
                cmd = Enum.Parse<Iec104Command>(c.Groups[1].Value, true);
                cmdIoa = int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture);
                if (cmdIoa is < 1 or > 0xFFFFFF) { error = "命令信息对象地址须为 1～16777215"; return false; }
            }
            else if (kv.Length == 2 && kv[0].Equals("sbo", StringComparison.OrdinalIgnoreCase) && kv[1] is "0" or "1") sbo = kv[1] == "1";
            else { error = $"未知地址参数“{part}”（支持 cmd=、sbo=0|1）"; return false; }
        }
        if (sbo is not null && cmd is null) { error = "sbo 须与 cmd 一起使用"; return false; }
        address = new(Enum.Parse<Iec104Monitor>(m.Groups[1].Value, true), ioa, cmd, cmdIoa, sbo);
        return true;
    }
}

public sealed class Iec104Spec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.Iec104;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;
    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<Iec104DeviceOptions>(device, out var o) is string error) return error;
        if (o.CommonAddress is < 1 or > 65534) return "公共地址须为 1～65534（65535 为广播，不用于采集）";
        if (o.OriginatorAddress is < 0 or > 255) return "源发地址须为 0～255";
        if (o.T1Sec is < 1 or > 255 || o.T2Sec is < 1 or > 255 || o.T3Sec is < 1 or > 172800) return "t1/t2 须为 1～255 秒，t3 须为 1～172800 秒";
        if (o.T2Sec >= o.T1Sec) return "t2 必须小于 t1";
        if (o.K is < 1 or > 32767 || o.W is < 1 or > 32767 || o.W > o.K * 2 / 3) return "k、w 须为正数且 w 不超过 k 的 2/3";
        if (o.GiIntervalSec is < 0 or > 86400 || o.CounterIntervalSec is < 0 or > 86400) return "召唤周期须为 0～86400 秒";
        if (!Enum.IsDefined(o.CommandMode)) return "命令方式无效";
        return null;
    }
    public string? ValidateTag(Tag tag, Device? device)
    {
        if (!Iec104Address.TryParse(tag.NodeId, out var a, out var error)) return error;
        if (tag.Writable && a.Command is null) return "可写 104 测点须用 ;cmd= 指定命令类型与信息对象地址";
        if (!tag.Writable && a.Command is not null) return "配置了 cmd 的测点须设为可写";
        if (a.Monitor == Iec104Monitor.SP && tag.DataType != TagDataType.Bool) return "单点信息（M_SP）须为 Bool 类型";
        if (a.Command is Iec104Command.SC or Iec104Command.DC && tag.DataType != TagDataType.Bool) return "单/双点命令须配合 Bool 测点";
        if (a.Command is Iec104Command.SE_NA or Iec104Command.SE_NB or Iec104Command.SE_NC && tag.DataType == TagDataType.Bool) return "设定值命令不能用于 Bool 测点";
        if (a.Command == Iec104Command.SE_NA && tag.DataType is not (TagDataType.Float or TagDataType.Double)) return "归一化设定值（C_SE_NA，-1～1）须使用 Float/Double";
        return null;
    }
}
