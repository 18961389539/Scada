using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Options;

namespace Scada.Server.External.Slave;

/// <summary>
/// 对外从站的地址映射表：把 Modbus 地址 / IEC 104 信息对象地址解析到测点，并把测点编码成协议元素。
/// 映射随配置版本重建；查表在会话线程内完成，构建在后台线程完成，因此用不可变快照整体替换。
/// </summary>
public sealed class ExportPointMap
{
    private readonly IConfigSnapshot _config;
    private readonly IRealtimeCache _cache;
    private readonly object _gate = new();
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private int _builtVersion = -1;
    private ModbusSlaveOptions? _lastModbus;
    private Iec104ServerOptions? _lastIec;

    public ExportPointMap(IConfigSnapshot config, IRealtimeCache cache)
    {
        _config = config;
        _cache = cache;
    }

    /// <summary>
    /// 配置版本变化、或 options 实例被热加载替换时重建。高频路径上只有两次比较，不做字符串拼接。
    /// appsettings 热加载会创建新的 options 实例，因此引用比较足以发现点表变化。
    /// </summary>
    public void Ensure(ModbusSlaveOptions modbus, Iec104ServerOptions iec)
    {
        var version = _config.Version;
        if (version == _builtVersion && ReferenceEquals(modbus, _lastModbus) && ReferenceEquals(iec, _lastIec)) return;
        lock (_gate)
        {
            if (version == _builtVersion && ReferenceEquals(modbus, _lastModbus) && ReferenceEquals(iec, _lastIec)) return;
            _snapshot = Build(modbus, iec);
            _builtVersion = version;
            _lastModbus = modbus;
            _lastIec = iec;
        }
    }

    public ModbusMap Modbus => _snapshot.Modbus;
    public Iec104Map Iec => _snapshot.Iec;
    public IReadOnlyList<string> Warnings => _snapshot.Warnings;
    public long BuiltAt => _snapshot.BuiltAt;

    private Snapshot Build(ModbusSlaveOptions modbus, Iec104ServerOptions iec)
    {
        var warnings = new List<string>();
        var tags = _config.Tags;
        var byId = new Dictionary<int, Tag>(tags.Count);
        foreach (var t in tags) byId[t.Id] = t;

        // ---- Modbus：四张独立地址表，每张表都是 address → 测点 ----
        var coils = new Dictionary<int, ExportSlot>();
        var discrete = new Dictionary<int, ExportSlot>();
        var holding = new Dictionary<int, ExportSlot>();
        var input = new Dictionary<int, ExportSlot>();

        void AddModbus(int unitId, TagArea area, int address, Tag tag)
        {
            var table = area switch
            {
                TagArea.Coil => coils,
                TagArea.DiscreteInput => discrete,
                TagArea.HoldingRegister => holding,
                TagArea.InputRegister => input,
                _ => null
            };
            if (table is null)
            {
                warnings.Add($"测点 {tag.Name}(#{tag.Id}) 的区 {area} 不是 Modbus 地址区，已跳过");
                return;
            }
            if (address is < 0 or > 65535)
            {
                warnings.Add($"测点 {tag.Name}(#{tag.Id}) 地址 {address} 超出 0–65535，已跳过");
                return;
            }
            var slot = new ExportSlot(tag.Id, tag.Name, tag.DataType, tag.Writable, address, unitId);
            if (table.TryGetValue(address, out var existing))
            {
                warnings.Add($"Modbus {area} 地址 {address} 被 {existing.TagName}(#{existing.TagId}) 与 {tag.Name}(#{tag.Id}) 重复占用，保留前者");
                return;
            }
            // 多寄存器类型要占住后续地址，避免后续测点把它的低字覆盖掉。
            table[address] = slot;
            for (var i = 1; i < slot.RegisterCount; i++)
            {
                if (!table.ContainsKey(address + i)) table[address + i] = slot;
            }
        }

        if (modbus.AutoAddressing)
        {
            var unit = modbus.UnitIds.Length > 0 ? modbus.UnitIds[0] : 1;
            foreach (var tag in tags)
            {
                // 计算点没有现场地址，直通模式下不暴露。
                if (tag.Kind != TagKind.Physical) continue;
                AddModbus(unit, tag.Area, tag.Address, tag);
            }
        }
        foreach (var p in modbus.Points)
        {
            if (!byId.TryGetValue(p.TagId, out var tag))
            {
                warnings.Add($"Modbus 点表引用了不存在的测点 #{p.TagId}，已跳过");
                continue;
            }
            AddModbus(p.UnitId, p.Area, p.Address, tag);
        }

        // ---- IEC 104：IOA → 测点，必须显式配置 ----
        var ioa = new Dictionary<int, IecSlot>();
        foreach (var p in iec.Points)
        {
            if (!byId.TryGetValue(p.TagId, out var tag))
            {
                warnings.Add($"IEC 104 点表引用了不存在的测点 #{p.TagId}，已跳过");
                continue;
            }
            if (p.Ioa is < 1 or > 16777215)
            {
                warnings.Add($"IEC 104 测点 {tag.Name}(#{tag.Id}) 的 IOA {p.Ioa} 超出 1–16777215，已跳过");
                continue;
            }
            if (ioa.ContainsKey(p.Ioa))
            {
                warnings.Add($"IEC 104 IOA {p.Ioa} 重复，保留先配置的点");
                continue;
            }
            ioa[p.Ioa] = new IecSlot(tag.Id, tag.Name, tag.DataType, tag.Writable, p.Ioa, ResolveKind(p.Kind, tag));
        }

        return new Snapshot(
            new ModbusMap(modbus.UnitIds, coils, discrete, holding, input),
            new Iec104Map(ioa),
            warnings,
            Environment.TickCount64);
    }

    private static Iec104PointKind ResolveKind(Iec104PointKind kind, Tag tag) =>
        kind != Iec104PointKind.Auto
            ? kind
            : tag.DataType == TagDataType.Bool ? Iec104PointKind.SinglePoint : Iec104PointKind.MeasuredFloat;

    /// <summary>读取测点当前值；质量不合格时按配置替换。</summary>
    public ExportSample Read(ExportSlot slot, double? badQualityValue) => ReadCore(slot.TagId, badQualityValue);

    /// <summary>IEC 104 侧没有"保持最后值"的语义要求，坏质量由品质位表达，因此不做替代。</summary>
    public ExportSample ReadIec(IecSlot slot) => ReadCore(slot.TagId, null);

    private ExportSample ReadCore(int tagId, double? badQualityValue)
    {
        var v = _cache.Get(tagId);
        // 没有缓存值时用当前 Unix 毫秒，不能用 TickCount64：后者是开机以来的毫秒数，
        // 直接喂给 CP56Time2a 会得到 1970 年附近的时标，主站侧看起来像"时间同步坏了"。
        if (v is null) return new ExportSample(0, TagQuality.CommFailure, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (v.Quality == TagQuality.Good) return new ExportSample(v.Value, v.Quality, v.SourceTs ?? v.Ts);
        if (badQualityValue is double fallback) return new ExportSample(fallback, v.Quality, v.Ts);
        return new ExportSample(v.Value, v.Quality, v.SourceTs ?? v.Ts);
    }

    public sealed record ExportSample(double Value, TagQuality Quality, long Ts);

    public sealed record ExportSlot(int TagId, string TagName, TagDataType DataType, bool Writable, int Address, int UnitId)
    {
        public int RegisterCount => DataType switch
        {
            TagDataType.Bool or TagDataType.Int16 or TagDataType.UInt16 => 1,
            TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
            TagDataType.Double => 4,
            _ => 1
        };
        public bool IsBit => DataType == TagDataType.Bool;
    }

    public sealed record IecSlot(int TagId, string TagName, TagDataType DataType, bool Writable, int Ioa, Iec104PointKind Kind)
    {
        public bool IsSinglePoint => Kind == Iec104PointKind.SinglePoint;
    }

    public sealed record ModbusMap(int[] UnitIds, Dictionary<int, ExportSlot> Coils, Dictionary<int, ExportSlot> DiscreteInputs,
        Dictionary<int, ExportSlot> HoldingRegisters, Dictionary<int, ExportSlot> InputRegisters)
    {
        public static ModbusMap Empty { get; } = new([1], [], [], [], []);

        public Dictionary<int, ExportSlot> Table(TagArea area) => area switch
        {
            TagArea.Coil => Coils,
            TagArea.DiscreteInput => DiscreteInputs,
            TagArea.HoldingRegister => HoldingRegisters,
            TagArea.InputRegister => InputRegisters,
            _ => []
        };

        public int Count => Coils.Count + DiscreteInputs.Count + HoldingRegisters.Count + InputRegisters.Count;
    }

    public sealed record Iec104Map(Dictionary<int, IecSlot> Points)
    {
        public static Iec104Map Empty { get; } = new([]);
        public int Count => Points.Count;
    }

    private sealed record Snapshot(ModbusMap Modbus, Iec104Map Iec, IReadOnlyList<string> Warnings, long BuiltAt)
    {
        public static Snapshot Empty { get; } = new(ModbusMap.Empty, Iec104Map.Empty, [], 0);
    }
}
