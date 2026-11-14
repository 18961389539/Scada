using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public enum DeltaSeries { DVP = 0, AS = 1 }
public enum DeltaTransport { Tcp = 0, Rtu = 1 }

public sealed class DeltaDeviceOptions
{
    public DeltaSeries Series { get; set; } = DeltaSeries.DVP;
    public DeltaTransport Transport { get; set; } = DeltaTransport.Tcp;
    /// <summary>批量读合并时允许的最大空隙（线圈点或寄存器字）。</summary>
    public int MaxGap { get; set; } = 16;
}

/// <summary>换算后的 Modbus 点。字的位只占一个保持寄存器，<see cref="Bit"/> 为 0～15。</summary>
public readonly record struct DeltaPoint(TagArea Area, int Address, int Length, int? Bit)
{
    public bool WordBit => Bit is not null;
}

/// <summary>
/// 台达软元件到 Modbus 从站地址。DVP 依据编程手册通信地址表：X 为离散输入，
/// M1536 起跳到 0xB000，D4096 起跳到 0x9000，C200 起为 32 位但通信地址仍连续。
/// AS 依据 AS 系列标准 Modbus 地址表：M/D 从 0 起，X/Y 是字.位。两张表不能混用。
/// </summary>
public static partial class DeltaAddress
{
    private enum Kind { Coil, Discrete, Holding, Input, Int16, Int32 }

    private readonly record struct Row(string Name, int First, int Last, int Base, bool Octal, Kind Kind, bool ReadOnly = false);

    private static readonly Row[] Dvp =
    [
        new("S", 0, 1023, 0, false, Kind.Coil),
        new("X", 0, 255, 1024, true, Kind.Discrete, ReadOnly: true),
        new("Y", 0, 255, 1280, true, Kind.Coil),
        new("T", 0, 255, 1536, false, Kind.Coil),
        new("T", 0, 255, 1536, false, Kind.Int16),
        new("M", 0, 1535, 2048, false, Kind.Coil),
        new("M", 1536, 4095, 45056, false, Kind.Coil),
        new("C", 0, 199, 3584, false, Kind.Coil),
        new("C", 0, 199, 3584, false, Kind.Int16),
        new("C", 200, 255, 3784, false, Kind.Coil),
        new("C", 200, 255, 3784, false, Kind.Int32),
        new("D", 0, 4095, 4096, false, Kind.Holding),
        new("D", 4096, 9999, 36864, false, Kind.Holding),
    ];

    private static readonly Row[] As =
    [
        new("M", 0, 8191, 0, false, Kind.Coil),
        new("SM", 0, 4095, 16384, false, Kind.Coil),
        new("S", 0, 2047, 20480, false, Kind.Coil),
        new("T", 0, 511, 57344, false, Kind.Coil),
        new("T", 0, 511, 57344, false, Kind.Int16),
        new("C", 0, 511, 61440, false, Kind.Coil),
        new("C", 0, 511, 61440, false, Kind.Int16),
        new("HC", 0, 255, 64512, false, Kind.Coil),
        new("HC", 0, 255, 64512, false, Kind.Int32),
        new("D", 0, 29999, 0, false, Kind.Holding),
        new("SR", 0, 2047, 49152, false, Kind.Holding),
        new("E", 0, 9, 65024, false, Kind.Holding),
    ];

    [GeneratedRegex(@"^(SM|SR|HC|X|Y|M|S|T|C|D|E)(\d+)(?:\.(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool UsesSerial(Device device) => device.Protocol == ProtocolType.Delta && UsesSerial(device.ProtocolOptions);

    public static bool UsesSerial(string? protocolOptions)
    {
        if (string.IsNullOrWhiteSpace(protocolOptions)) return false;
        try
        {
            var options = JsonSerializer.Deserialize<DeltaDeviceOptions>(protocolOptions, ProtocolCatalog.OptionsJson);
            return options?.Transport == DeltaTransport.Rtu;
        }
        catch (JsonException) { return false; }
    }

    public static bool TryMap(DeltaSeries series, string text, TagDataType dataType, bool writable, out DeltaPoint point, out string? error)
    {
        point = default;
        error = null;
        if (dataType is TagDataType.Double or TagDataType.String)
        { error = "台达软元件不支持 Double 或字符串"; return false; }
        var match = Pattern().Match(text.Trim().ToUpperInvariant());
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
        if (series == DeltaSeries.AS && name is "X" or "Y")
        {
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var word))
            { error = "元件编号无效"; return false; }
            return MapAsIo(name, word, bit, dataType, writable, out point, out error);
        }
        var table = series == DeltaSeries.DVP ? Dvp : As;
        if (!table.Any(row => row.Name == name))
        { error = series == DeltaSeries.DVP ? $"DVP 没有 {name} 元件" : $"AS 没有 {name} 元件"; return false; }
        if (series == DeltaSeries.DVP && name is "X" or "Y" && bit is not null)
        { error = "DVP 的 X/Y 不能再写位号"; return false; }
        var octal = table.First(row => row.Name == name).Octal;
        if (octal && digits.Any(ch => ch is < '0' or > '7'))
        { error = "X/Y 编号为八进制，不能包含 8 或 9"; return false; }
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        { error = "元件编号无效"; return false; }
        var number = octal ? Convert.ToInt32(digits, 8) : parsed;
        var matches = table.Where(row => row.Name == name && number >= row.First && number <= row.Last).ToArray();
        if (matches.Length == 0)
        {
            var rows = table.Where(item => item.Name == name).Select(Format).Distinct();
            error = $"{name}{digits} 超出范围（{string.Join("、", rows)}）";
            return false;
        }
        return Finish(name, digits, number, bit, dataType, writable, matches, out point, out error);
    }

    private static bool MapAsIo(string name, int word, int? bit, TagDataType dataType, bool writable, out DeltaPoint point, out string? error)
    {
        point = default;
        error = null;
        if (word > 63) { error = $"{name}{word} 超出范围（{name}0–{name}63）"; return false; }
        var input = name == "X";
        if (bit is int ioBit)
        {
            if (dataType != TagDataType.Bool) { error = "X/Y 的位地址须为 Bool 类型"; return false; }
            if (input && writable) { error = "X 是输入，只读"; return false; }
            point = new DeltaPoint(input ? TagArea.DiscreteInput : TagArea.Coil, (input ? 24576 : 40960) + word * 16 + ioBit, 1, null);
            return true;
        }
        if (dataType == TagDataType.Bool) { error = "AS 的 X/Y 位地址写成 X0.0、Y0.0"; return false; }
        var length = dataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float ? 2 : 1;
        if (word + length - 1 > 63) { error = $"{name}{word} 加上数据长度超出 {name}0–{name}63"; return false; }
        if (input && writable) { error = "X 是输入，只读"; return false; }
        point = new DeltaPoint(input ? TagArea.InputRegister : TagArea.HoldingRegister, (input ? 32768 : 40960) + word, length, null);
        return true;
    }

    private static bool Finish(string name, string digits, int number, int? bit, TagDataType dataType, bool writable, Row[] matches, out DeltaPoint point, out string? error)
    {
        point = default;
        error = null;
        var word = matches.FirstOrDefault(row => row.Kind is Kind.Holding or Kind.Input or Kind.Int16 or Kind.Int32);
        var bitRow = matches.FirstOrDefault(row => row.Kind is Kind.Coil or Kind.Discrete);
        var hasWord = word.Name == name;
        var hasBit = bitRow.Name == name;
        if (bit is not null)
        {
            if (!hasWord || word.Kind != Kind.Holding) { error = hasBit ? "位软元件不能再指定位号" : "定时器和计数器当前值不能按位访问"; return false; }
            if (dataType != TagDataType.Bool) { error = "字的位地址（如 D10.3）须为 Bool 类型"; return false; }
            if (writable) { error = "字的位地址只读：写入需读-改-写整字，可能覆盖 PLC 同时修改的其他位"; return false; }
            point = new DeltaPoint(TagArea.HoldingRegister, word.Base + (number - word.First), 1, bit);
            return true;
        }
        if (dataType == TagDataType.Bool)
        {
            if (!hasBit) { error = "字软元件的 Bool 测点须指定位号，如 D10.3"; return false; }
            if (writable && bitRow.ReadOnly) { error = "X 是输入，只读"; return false; }
            point = new DeltaPoint(Area(bitRow.Kind), bitRow.Base + (number - bitRow.First), 1, null);
            return true;
        }
        if (!hasWord) { error = "位软元件只能使用 Bool 类型"; return false; }
        if (writable && word.ReadOnly) { error = "X 是输入，只读"; return false; }
        if (word.Kind == Kind.Int32)
        {
            if (dataType is not (TagDataType.Int32 or TagDataType.UInt32))
            { error = $"{name}{word.First}–{name}{word.Last} 是 32 位，请使用 Int32 或 UInt32"; return false; }
            point = new DeltaPoint(TagArea.HoldingRegister, word.Base + (number - word.First), 2, null);
            return true;
        }
        var length = dataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float ? 2 : 1;
        if (word.Kind == Kind.Int16 && (length != 1 || dataType is not (TagDataType.Int16 or TagDataType.UInt16)))
        { error = $"{name} 当前值是 16 位，请使用 Int16 或 UInt16"; return false; }
        if (number + length - 1 > word.Last) { error = $"{name}{digits} 加上数据长度超出 {Format(word)}"; return false; }
        point = new DeltaPoint(Area(word.Kind), word.Base + (number - word.First), length, null);
        return true;
    }

    private static TagArea Area(Kind kind) => kind switch
    {
        Kind.Coil => TagArea.Coil,
        Kind.Discrete => TagArea.DiscreteInput,
        Kind.Input => TagArea.InputRegister,
        _ => TagArea.HoldingRegister
    };

    private static string Format(Row row)
    {
        if (!row.Octal) return $"{row.Name}{row.First}–{row.Name}{row.Last}";
        return $"{row.Name}{Convert.ToString(row.First, 8)}–{row.Name}{Convert.ToString(row.Last, 8)}";
    }
}

public sealed class DeltaSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.Delta;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;
    public ProtocolFormSpec Form => ProtocolForms.Delta;

    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<DeltaDeviceOptions>(device, out var options) is string error) return error;
        if (!Enum.IsDefined(options.Series) || !Enum.IsDefined(options.Transport)) return "台达系列或链路无效";
        if (options.MaxGap is < 0 or > 256) return "合并空隙须为 0～256";
        if (options.Transport == DeltaTransport.Rtu)
        {
            if (string.IsNullOrWhiteSpace(device.SerialPort) || device.SerialPort.Length > 128) return "串口名称无效";
            if (device.BaudRate is < 300 or > 921600 || device.DataBits != 8 || !Enum.IsDefined(device.Parity) || !Enum.IsDefined(device.StopBits))
                return "串口通信参数无效，Modbus RTU 使用 8 个数据位";
            if (device.StationNumber is < 1 or > 247) return "RTU 采集站号须在 1～247 之间，不支持广播写入";
        }
        return null;
    }

    public string? ValidateTag(Tag tag, Device? device)
    {
        var series = DeltaSeries.DVP;
        if (device is not null)
        {
            if (ProtocolCatalog.TryOptions<DeltaDeviceOptions>(device, out var options) is string error) return error;
            if (!Enum.IsDefined(options.Series)) return "台达系列无效";
            series = options.Series;
        }
        return DeltaAddress.TryMap(series, tag.NodeId, tag.DataType, tag.Writable, out _, out var message) ? null : message;
    }
}
