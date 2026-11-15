using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public enum InovanceSeries { H3U = 0, H5U = 1, Easy = 2, AM = 3, AC = 4 }
public enum InovanceTransport { Tcp = 0, Rtu = 1 }

public sealed class InovanceDeviceOptions
{
    public InovanceSeries Series { get; set; } = InovanceSeries.H3U;
    public InovanceTransport Transport { get; set; } = InovanceTransport.Tcp;
    /// <summary>批量读合并时允许的最大空隙（线圈点或寄存器字）。</summary>
    public int MaxGap { get; set; } = 16;
}

/// <summary>换算后的 Modbus 点。字的位只占一个保持寄存器，<see cref="Bit"/> 为 0～15。</summary>
public readonly record struct InovancePoint(TagArea Area, int Address, int Length, int? Bit)
{
    public bool WordBit => Bit is not null;
}

/// <summary>
/// 汇川软元件到 Modbus 从站地址。H3U 依据《H3U 系列通信与轴控应用手册》编址节；
/// H5U 依据《H5U 系列控制器内置 Modbus 协议说明》；Easy 与 H5U 同一张表。
/// 定时器当前值按编址节取 T0–T511（手册后一节写成 T0–T255，与线圈表不一致）。
/// AM 与 AC 共用中型机 Modbus 区：QX 线圈、IX 离散输入、MW 保持寄存器。SM/SD 不在这张表里。
/// </summary>
public static partial class InovanceAddress
{
    private enum Width { Coil, Word, Int16, Int32 }

    private readonly record struct Row(string Name, int First, int Last, int Base, bool Octal, Width Width, bool ReadOnly = false);

    // H3U 线圈。M8000 段的首地址等于元件号，所以地址就是元件号本身；中间 M7680–M7999 是空档。
    private static readonly Row[] H3uCoils =
    [
        new("M", 0, 7679, 0, false, Width.Coil),
        new("M", 8000, 8511, 8000, false, Width.Coil),
        new("SM", 0, 1023, 9216, false, Width.Coil),
        new("S", 0, 4095, 57344, false, Width.Coil),
        new("T", 0, 511, 61440, false, Width.Coil),
        new("C", 0, 255, 62464, false, Width.Coil),
        new("X", 0, 255, 63488, true, Width.Coil, ReadOnly: true),
        new("Y", 0, 255, 64512, true, Width.Coil),
    ];

    private static readonly Row[] H3uWords =
    [
        new("D", 0, 8511, 0, false, Width.Word),
        new("SD", 0, 1023, 9216, false, Width.Word),
        new("R", 0, 32767, 12288, false, Width.Word),
        new("T", 0, 511, 61440, false, Width.Int16),
        new("C", 0, 199, 62464, false, Width.Int16),
        new("C", 200, 255, 63232, false, Width.Int32),
    ];

    private static readonly Row[] H5uCoils =
    [
        new("M", 0, 7999, 0, false, Width.Coil),
        new("B", 0, 32767, 12288, false, Width.Coil),
        new("S", 0, 4095, 57344, false, Width.Coil),
        new("X", 0, 1023, 63488, true, Width.Coil),
        new("Y", 0, 1023, 64512, true, Width.Coil),
    ];

    private static readonly Row[] H5uWords =
    [
        new("D", 0, 7999, 0, false, Width.Word),
        new("R", 0, 32767, 12288, false, Width.Word),
    ];

    [GeneratedRegex(@"^(SM|SD|[A-Z])(\d+)(?:\.(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^%?(QX|IX|MX|MW|MD|MB|QB|IB|QW|IW|SM|SD)(\d+)(?:\.(\d+))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AmPattern();

    public static bool UsesSerial(Device device) => device.Protocol == ProtocolType.Inovance && UsesSerial(device.ProtocolOptions);

    public static bool UsesSerial(string? protocolOptions)
    {
        if (string.IsNullOrWhiteSpace(protocolOptions)) return false;
        try
        {
            var options = JsonSerializer.Deserialize<InovanceDeviceOptions>(protocolOptions, ProtocolCatalog.OptionsJson);
            return options?.Transport == InovanceTransport.Rtu;
        }
        catch (JsonException) { return false; }
    }

    public static bool TryMap(InovanceSeries series, string text, TagDataType dataType, bool writable, out InovancePoint point, out string? error)
    {
        point = default;
        error = null;
        if (dataType is TagDataType.Double or TagDataType.String)
        { error = "汇川软元件不支持 Double 或字符串"; return false; }
        if (series is InovanceSeries.AM or InovanceSeries.AC) return MapAm(text, dataType, writable, out point, out error);
        var raw = text.Trim();
        var match = Pattern().Match(raw.ToUpperInvariant());
        if (!match.Success) { error = "地址格式应为软元件+编号，如 D10、D10.3、M100、X10、T5"; return false; }
        var name = match.Groups[1].Value;
        var digits = match.Groups[2].Value;
        int? bit = null;
        if (match.Groups[3].Success)
        {
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBit) || parsedBit > 15)
            { error = "位号须为 0～15，如 D10.3"; return false; }
            bit = parsedBit;
        }
        if (name == "W")
        { error = series == InovanceSeries.H3U ? "H3U 没有 W 元件" : "W 元件不支持 Modbus 访问"; return false; }

        var table = series == InovanceSeries.H3U ? (Coils: H3uCoils, Words: H3uWords) : (Coils: H5uCoils, Words: H5uWords);
        var known = table.Coils.Concat(table.Words).Any(row => row.Name == name);
        if (!known)
        {
            error = series == InovanceSeries.H3U ? $"H3U 没有 {name} 元件" : $"H5U / Easy 没有 {name} 元件";
            return false;
        }
        var octal = table.Coils.Concat(table.Words).First(row => row.Name == name).Octal;
        if (octal && digits.Any(ch => ch is < '0' or > '7'))
        { error = "X/Y 编号为八进制，不能包含 8 或 9"; return false; }
        if (!int.TryParse(digits, octal ? NumberStyles.None : NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        { error = "元件编号无效"; return false; }
        var number = octal ? Convert.ToInt32(digits, 8) : parsed;

        var coil = table.Coils.FirstOrDefault(row => row.Name == name && number >= row.First && number <= row.Last);
        var word = table.Words.FirstOrDefault(row => row.Name == name && number >= row.First && number <= row.Last);
        var inCoil = coil.Name == name;
        var inWord = word.Name == name;
        if (!inCoil && !inWord)
        {
            var rows = table.Coils.Concat(table.Words).Where(item => item.Name == name).Select(Format).Distinct();
            error = $"{name}{digits} 超出范围（{string.Join("、", rows)}）";
            return false;
        }
        if (bit is not null)
        {
            if (!inWord || word.Width != Width.Word) { error = inCoil ? "位软元件不能再指定位号" : "定时器和计数器当前值不能按位访问"; return false; }
            if (dataType != TagDataType.Bool) { error = "字的位地址（如 D10.3）须为 Bool 类型"; return false; }
            if (writable) { error = "字的位地址只读：写入需读-改-写整字，可能覆盖 PLC 同时修改的其他位"; return false; }
            point = new InovancePoint(TagArea.HoldingRegister, word.Base + (number - word.First), 1, bit);
            return true;
        }
        if (dataType == TagDataType.Bool)
        {
            if (!inCoil) { error = "字软元件的 Bool 测点须指定位号，如 D10.3"; return false; }
            if (writable && coil.ReadOnly) { error = "H3U 的 X 只读"; return false; }
            point = new InovancePoint(TagArea.Coil, coil.Base + (number - coil.First), 1, null);
            return true;
        }
        if (!inWord) { error = "位软元件只能使用 Bool 类型"; return false; }
        if (word.Width == Width.Int32)
        {
            if (dataType is not (TagDataType.Int32 or TagDataType.UInt32)) { error = "C200–C255 是 32 位计数器，请使用 Int32 或 UInt32"; return false; }
            point = new InovancePoint(TagArea.HoldingRegister, word.Base + (number - word.First) * 2, 2, null);
            return true;
        }
        var length = dataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float ? 2 : 1;
        if (word.Width == Width.Int16)
        {
            if (length != 1 || dataType is not (TagDataType.Int16 or TagDataType.UInt16))
            { error = "T、C0–C199 的当前值是 16 位，请使用 Int16 或 UInt16"; return false; }
        }
        if (number + length - 1 > word.Last) { error = $"{name}{digits} 加上数据长度超出 {Format(word)}"; return false; }
        point = new InovancePoint(TagArea.HoldingRegister, word.Base + (number - word.First), length, null);
        return true;
    }

    private static bool MapAm(string text, TagDataType dataType, bool writable, out InovancePoint point, out string? error)
    {
        point = default;
        error = null;
        var match = AmPattern().Match(text.Trim());
        if (!match.Success) { error = "AM / AC 地址写成 MW0、MW0.3、MD0、QX0.0、IX0.0"; return false; }
        var name = match.Groups[1].Value.ToUpperInvariant();
        if (name is "MB" or "QB" or "IB" or "QW" or "IW") { error = "字节和 Q/I 字请改用 MW、QX、IX"; return false; }
        if (name is "SM" or "SD") { error = "SM、SD 不在 AM / AC 的标准 Modbus 区"; return false; }
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        { error = "元件编号无效"; return false; }
        int? bit = null;
        if (match.Groups[3].Success)
        {
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBit))
            { error = "元件编号无效"; return false; }
            bit = parsedBit;
        }
        if (name is "QX" or "IX" or "MX")
        {
            if (bit is not int ioBit) { error = $"{name} 须写位号，如 {name}0.0"; return false; }
            if (ioBit > 7) { error = "QX、IX、MX 的位号须为 0～7"; return false; }
            if (name == "MX")
            {
                if (dataType != TagDataType.Bool) { error = "字的位地址（如 D10.3）须为 Bool 类型"; return false; }
                if (number > 131071) { error = $"MX{number}.{ioBit} 超出范围（MX0.0–MX131071.7）"; return false; }
                if (writable) { error = "MX 只读"; return false; }
                point = new InovancePoint(TagArea.HoldingRegister, number / 2, 1, (number % 2) * 8 + ioBit);
                return true;
            }
            if (dataType != TagDataType.Bool) { error = "位软元件只能使用 Bool 类型"; return false; }
            if (number > 8191) { error = $"{name}{number}.{ioBit} 超出范围（{name}0.0–{name}8191.7）"; return false; }
            if (name == "IX" && writable) { error = "IX 是输入，只读"; return false; }
            point = new InovancePoint(name == "IX" ? TagArea.DiscreteInput : TagArea.Coil, number * 8 + ioBit, 1, null);
            return true;
        }
        if (name == "MD")
        {
            if (bit is not null) { error = "MD 不能按位访问"; return false; }
            if (dataType is not (TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float))
            { error = "MD 是 32 位，请使用 Int32、UInt32 或 Float"; return false; }
            if (number > 32767) { error = "MD32768 超出范围（MD0–MD32767）"; return false; }
            point = new InovancePoint(TagArea.HoldingRegister, number * 2, 2, null);
            return true;
        }
        if (bit is int wordBit)
        {
            if (wordBit > 15) { error = "位号须为 0～15，如 D10.3"; return false; }
            if (dataType != TagDataType.Bool) { error = "字的位地址（如 D10.3）须为 Bool 类型"; return false; }
            if (writable) { error = "字的位地址只读：写入需读-改-写整字，可能覆盖 PLC 同时修改的其他位"; return false; }
            if (number > 65535) { error = "MW65536 超出范围（MW0–MW65535）"; return false; }
            point = new InovancePoint(TagArea.HoldingRegister, number, 1, wordBit);
            return true;
        }
        if (dataType == TagDataType.Bool) { error = "字软元件的 Bool 测点须指定位号，如 D10.3"; return false; }
        var length = dataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float ? 2 : 1;
        if (number > 65535 || number + length - 1 > 65535) { error = $"MW{number} 加上数据长度超出 MW0–MW65535"; return false; }
        point = new InovancePoint(TagArea.HoldingRegister, number, length, null);
        return true;
    }

    private static string Format(Row row)
    {
        if (!row.Octal) return $"{row.Name}{row.First}–{row.Name}{row.Last}";
        return $"{row.Name}{Convert.ToString(row.First, 8)}–{row.Name}{Convert.ToString(row.Last, 8)}";
    }
}

public sealed class InovanceSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.Inovance;
    // TCP 走主机端口；RTU 在 ValidateDevice 里改查串口。调用方对 RTU 跳过主机校验。
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;
    public ProtocolFormSpec Form => ProtocolForms.Inovance;

    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<InovanceDeviceOptions>(device, out var options) is string error) return error;
        if (!Enum.IsDefined(options.Series) || !Enum.IsDefined(options.Transport)) return "汇川系列或链路无效";
        if (options.MaxGap is < 0 or > 256) return "合并空隙须为 0～256";
        if (options.Transport == InovanceTransport.Rtu)
        {
            if (SerialSettings.Validate(device.SerialPort, device.BaudRate, device.DataBits, device.Parity, device.StopBits) is string serial) return serial;
            if (SerialSettings.ValidateStation(device.StationNumber) is string station) return station;
            return null;
        }
        return null;
    }

    public string? ValidateTag(Tag tag, Device? device)
    {
        var series = InovanceSeries.H3U;
        if (device is not null)
        {
            if (ProtocolCatalog.TryOptions<InovanceDeviceOptions>(device, out var options) is string error) return error;
            if (!Enum.IsDefined(options.Series)) return "汇川系列无效";
            series = options.Series;
        }
        return InovanceAddress.TryMap(series, tag.NodeId, tag.DataType, tag.Writable, out _, out var message) ? null : message;
    }
}
