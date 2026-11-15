using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public enum XinjeSeries { XC = 0, XD = 1, XL = 2 }
public enum XinjeTransport { Tcp = 0, Rtu = 1 }

public sealed class XinjeDeviceOptions
{
    public XinjeSeries Series { get; set; } = XinjeSeries.XC;
    public XinjeTransport Transport { get; set; } = XinjeTransport.Tcp;
    /// <summary>批量读合并时允许的最大空隙（线圈点或寄存器字）。</summary>
    public int MaxGap { get; set; } = 16;
}

/// <summary>换算后的 Modbus 点。字的位只占一个保持寄存器，<see cref="Bit"/> 为 0～15。</summary>
public readonly record struct XinjePoint(TagArea Area, int Address, int Length, int? Bit)
{
    public bool WordBit => Bit is not null;
}

/// <summary>
/// 信捷软元件到 Modbus 从站地址。XC 用 XC 表；XD 与 XL 共用一张表，区首地址与 XD1/XL1 相同，
/// 点数按 XD5/XL5 的扩展范围，小机型超出自身容量时由 PLC 拒绝。
/// X/Y 本体是八进制。XD/XL 的扩展端点写成 5 位数：X10000 的后四位仍是八进制，ID10000 的后四位是十进制。
/// 触点是 T/C，当前值是 TD/CD。
/// </summary>
public static partial class XinjeAddress
{
    private enum Width { Coil, Word, Int16 }

    private readonly record struct Row(string Name, int First, int Last, int Base, bool Octal, Width Width);
    private readonly record struct Bank(int Digit, int Base, int Last, bool Octal);

    private static readonly Row[] XcCoils =
    [
        new("M", 0, 7999, 0, false, Width.Coil),
        new("M", 8000, 8511, 24576, false, Width.Coil),
        new("X", 0, 543, 16384, true, Width.Coil),
        new("Y", 0, 543, 18432, true, Width.Coil),
        new("S", 0, 1023, 20480, false, Width.Coil),
        new("T", 0, 618, 25600, false, Width.Coil),
        new("C", 0, 634, 27648, false, Width.Coil),
    ];

    private static readonly Row[] XcWords =
    [
        new("D", 0, 7999, 0, false, Width.Word),
        new("D", 8000, 8511, 16384, false, Width.Word),
        new("TD", 0, 618, 12288, false, Width.Int16),
        new("CD", 0, 634, 14336, false, Width.Int16),
        new("FD", 0, 5000, 18432, false, Width.Word),
        new("FD", 8000, 8511, 26624, false, Width.Word),
        new("ED", 0, 36863, 28672, false, Width.Word),
    ];

    private static readonly Row[] XdCoils =
    [
        new("M", 0, 20479, 0, false, Width.Coil),
        new("X", 0, 63, 20480, true, Width.Coil),
        new("Y", 0, 63, 24576, true, Width.Coil),
        new("S", 0, 7999, 28672, false, Width.Coil),
        new("SM", 0, 4095, 36864, false, Width.Coil),
        new("T", 0, 4095, 40960, false, Width.Coil),
        new("C", 0, 4095, 45056, false, Width.Coil),
        new("ET", 0, 39, 49152, false, Width.Coil),
        new("SEM", 0, 127, 49280, false, Width.Coil),
        new("HM", 0, 6143, 49408, false, Width.Coil),
        new("HS", 0, 999, 55552, false, Width.Coil),
        new("HT", 0, 1023, 57600, false, Width.Coil),
        new("HC", 0, 1023, 58624, false, Width.Coil),
        new("HSC", 0, 39, 59648, false, Width.Coil),
    ];

    private static readonly Row[] XdWords =
    [
        new("D", 0, 20479, 0, false, Width.Word),
        new("ID", 0, 99, 20480, false, Width.Word),
        new("QD", 0, 99, 24576, false, Width.Word),
        new("SD", 0, 4095, 28672, false, Width.Word),
        new("TD", 0, 4095, 32768, false, Width.Int16),
        new("CD", 0, 4095, 36864, false, Width.Int16),
        new("ETD", 0, 39, 40960, false, Width.Int16),
        new("HD", 0, 6143, 41088, false, Width.Word),
        new("HSD", 0, 1023, 47232, false, Width.Word),
        new("HTD", 0, 1023, 48256, false, Width.Int16),
        new("HCD", 0, 1023, 49280, false, Width.Int16),
        new("HSCD", 0, 39, 50304, false, Width.Int16),
        new("FD", 0, 8191, 50368, false, Width.Word),
        new("SFD", 0, 4095, 58560, false, Width.Word),
        new("FS", 0, 47, 62656, false, Width.Word),
    ];

    private static readonly Bank[] XBanks = [new(1, 20736, 1023, true), new(2, 22736, 127, true), new(3, 23536, 63, true)];
    private static readonly Bank[] YBanks = [new(1, 24832, 1023, true), new(2, 26832, 127, true), new(3, 27632, 63, true)];
    private static readonly Bank[] IdBanks = [new(1, 20736, 1599, false), new(2, 22736, 199, false), new(3, 23536, 99, false)];
    private static readonly Bank[] QdBanks = [new(1, 24832, 1599, false), new(2, 26832, 199, false), new(3, 27632, 99, false)];

    [GeneratedRegex(@"^(HSCD|HTD|HCD|ETD|HSC|HSD|SEM|SFD|SM|SD|TD|CD|FD|ED|ET|HM|HS|HT|HC|HD|ID|QD|FS|M|X|Y|S|T|C|D)(\d+)(?:\.(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool UsesSerial(Device device) => device.Protocol == ProtocolType.Xinje && UsesSerial(device.ProtocolOptions);

    public static bool UsesSerial(string? protocolOptions)
    {
        if (string.IsNullOrWhiteSpace(protocolOptions)) return false;
        try
        {
            var options = JsonSerializer.Deserialize<XinjeDeviceOptions>(protocolOptions, ProtocolCatalog.OptionsJson);
            return options?.Transport == XinjeTransport.Rtu;
        }
        catch (JsonException) { return false; }
    }

    public static bool TryMap(XinjeSeries series, string text, TagDataType dataType, bool writable, out XinjePoint point, out string? error)
    {
        point = default;
        error = null;
        if (dataType is TagDataType.Double or TagDataType.String)
        { error = "信捷软元件不支持 Double 或字符串"; return false; }
        var match = Pattern().Match(text.Trim().ToUpperInvariant());
        if (!match.Success) { error = "地址格式应为软元件+编号，如 D10、D10.3、M100、X10、TD0"; return false; }
        var name = match.Groups[1].Value;
        var digits = match.Groups[2].Value;
        int? bit = null;
        if (match.Groups[3].Success)
        {
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBit) || parsedBit > 15)
            { error = "位号须为 0～15，如 D10.3"; return false; }
            bit = parsedBit;
        }
        var xd = series != XinjeSeries.XC;
        var coils = xd ? XdCoils : XcCoils;
        var words = xd ? XdWords : XcWords;
        if (!coils.Concat(words).Any(row => row.Name == name) && !(xd && name is "X" or "Y" or "ID" or "QD"))
        {
            error = xd ? $"XD / XL 没有 {name} 元件" : $"XC 没有 {name} 元件";
            return false;
        }
        if (xd && digits.Length == 5 && digits[0] is '1' or '2' or '3' && name is "X" or "Y" or "ID" or "QD")
            return MapBank(name, digits, bit, dataType, writable, out point, out error);

        var octal = coils.Concat(words).First(row => row.Name == name).Octal;
        if (octal && digits.Any(ch => ch is < '0' or > '7'))
        { error = "X/Y 编号为八进制，不能包含 8 或 9"; return false; }
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        { error = "元件编号无效"; return false; }
        var number = octal ? Convert.ToInt32(digits, 8) : parsed;
        var coil = coils.FirstOrDefault(row => row.Name == name && number >= row.First && number <= row.Last);
        var word = words.FirstOrDefault(row => row.Name == name && number >= row.First && number <= row.Last);
        var inCoil = coil.Name == name;
        var inWord = word.Name == name;
        if (!inCoil && !inWord)
        {
            if (xd && name is "X" or "Y") { error = "XD / XL 本体 X/Y 只到 77（八进制）；扩展端点写成 X10000 这种 5 位数"; return false; }
            var rows = coils.Concat(words).Where(item => item.Name == name).Select(Format).Distinct();
            error = $"{name}{digits} 超出范围（{string.Join("、", rows)}）";
            return false;
        }
        return Finish(name, digits, number, bit, dataType, writable, inCoil, inWord, coil, word, out point, out error);
    }

    private static bool MapBank(string name, string digits, int? bit, TagDataType dataType, bool writable, out XinjePoint point, out string? error)
    {
        point = default;
        error = null;
        var banks = name switch { "X" => XBanks, "Y" => YBanks, "ID" => IdBanks, _ => QdBanks };
        var bank = banks.First(item => item.Digit == digits[0] - '0');
        var rest = digits[1..];
        if (bank.Octal && rest.Any(ch => ch is < '0' or > '7'))
        { error = "X/Y 扩展地址的后四位是八进制，不能包含 8 或 9"; return false; }
        var offset = bank.Octal ? Convert.ToInt32(rest, 8) : int.Parse(rest, CultureInfo.InvariantCulture);
        if (offset > bank.Last) { error = $"{name}{digits} 超出扩展范围"; return false; }
        var coil = name is "X" or "Y";
        var row = new Row(name, 0, bank.Last, bank.Base, bank.Octal, coil ? Width.Coil : Width.Word);
        return Finish(name, digits, offset, bit, dataType, writable, coil, !coil, coil ? row : default, coil ? default : row, out point, out error);
    }

    private static bool Finish(string name, string digits, int number, int? bit, TagDataType dataType, bool writable, bool inCoil, bool inWord, Row coil, Row word, out XinjePoint point, out string? error)
    {
        point = default;
        error = null;
        if (bit is not null)
        {
            if (!inWord || word.Width != Width.Word) { error = inCoil ? "位软元件不能再指定位号" : "定时器和计数器当前值不能按位访问"; return false; }
            if (dataType != TagDataType.Bool) { error = "字的位地址（如 D10.3）须为 Bool 类型"; return false; }
            if (writable) { error = "字的位地址只读：写入需读-改-写整字，可能覆盖 PLC 同时修改的其他位"; return false; }
            point = new XinjePoint(TagArea.HoldingRegister, word.Base + (number - word.First), 1, bit);
            return true;
        }
        if (dataType == TagDataType.Bool)
        {
            if (!inCoil) { error = "字软元件的 Bool 测点须指定位号，如 D10.3"; return false; }
            point = new XinjePoint(TagArea.Coil, coil.Base + (number - coil.First), 1, null);
            return true;
        }
        if (!inWord)
        {
            error = name is "T" or "C" or "ET" or "HT" or "HC" or "HSC"
                ? $"当前值请使用 {name}D，例如 {name}D{digits}"
                : "位软元件只能使用 Bool 类型";
            return false;
        }
        var length = dataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float ? 2 : 1;
        if (word.Width == Width.Int16 && (length != 1 || dataType is not (TagDataType.Int16 or TagDataType.UInt16)))
        { error = $"{name} 当前值是 16 位，请使用 Int16 或 UInt16"; return false; }
        if (number + length - 1 > word.Last) { error = $"{name}{digits} 加上数据长度超出 {Format(word)}"; return false; }
        point = new XinjePoint(TagArea.HoldingRegister, word.Base + (number - word.First), length, null);
        return true;
    }

    private static string Format(Row row)
    {
        if (!row.Octal) return $"{row.Name}{row.First}–{row.Name}{row.Last}";
        return $"{row.Name}{Convert.ToString(row.First, 8)}–{row.Name}{Convert.ToString(row.Last, 8)}";
    }
}

public sealed class XinjeSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.Xinje;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;
    public ProtocolFormSpec Form => ProtocolForms.Xinje;

    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<XinjeDeviceOptions>(device, out var options) is string error) return error;
        if (!Enum.IsDefined(options.Series) || !Enum.IsDefined(options.Transport)) return "信捷系列或链路无效";
        if (options.MaxGap is < 0 or > 256) return "合并空隙须为 0～256";
        if (options.Transport == XinjeTransport.Rtu)
        {
            if (SerialSettings.Validate(device.SerialPort, device.BaudRate, device.DataBits, device.Parity, device.StopBits) is string serial) return serial;
            if (SerialSettings.ValidateStation(device.StationNumber) is string station) return station;
        }
        return null;
    }

    public string? ValidateTag(Tag tag, Device? device)
    {
        var series = XinjeSeries.XC;
        if (device is not null)
        {
            if (ProtocolCatalog.TryOptions<XinjeDeviceOptions>(device, out var options) is string error) return error;
            if (!Enum.IsDefined(options.Series)) return "信捷系列无效";
            series = options.Series;
        }
        return XinjeAddress.TryMap(series, tag.NodeId, tag.DataType, tag.Writable, out _, out var message) ? null : message;
    }
}
