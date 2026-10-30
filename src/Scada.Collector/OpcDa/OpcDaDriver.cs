using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.OpcDa;

public sealed record OpcDaSample(object? Value, short Quality, DateTime Timestamp, int Error);

/// <summary>OPC DA 客户端适配接口：Windows 上由 <see cref="OpcDaAutomationClient"/>（OPC 自动化包装器，后期绑定 COM）实现，测试用假实现。</summary>
public interface IOpcDaClient : IDisposable
{
    bool Connected { get; }
    void Connect(string progId, string? node, int updateRateMs);
    /// <summary>注册测点，返回每个 ItemID 的错误码（0 = 成功）。已注册的不重复添加。</summary>
    IReadOnlyList<int> AddItems(IReadOnlyList<string> itemIds);
    IReadOnlyList<OpcDaSample> Read(IReadOnlyList<string> itemIds, bool fromDevice);
    /// <summary>同步写，返回 OPC 错误码（0 = 成功）。COM/RPC 故障直接抛出。</summary>
    int Write(string itemId, object value);
}

/// <summary>
/// OPC DA 2.0/3.0 客户端（仅 Windows）。依赖 COM/DCOM，Linux 等系统上连接时报告清晰错误；建议优先通过 DA→UA 包装器以 OPC UA 驱动接入。
/// 值/品质/时间戳映射与写入类型转换为纯逻辑，已用假客户端测试；真实 Windows 环境未验证。
/// </summary>
public sealed class OpcDaDriver(Func<IOpcDaClient>? clientFactory = null, Func<bool>? isWindows = null) : IDeviceDriver
{
    public const string NotSupportedMessage = "OPC DA 依赖 Windows COM/DCOM，当前操作系统不支持。请在 Windows 网关上运行，或使用 DA→UA 包装器后以 OPC UA 协议接入。";

    private OpcDaDeviceOptions _o = new();
    private IOpcDaClient? _client;
    private readonly Dictionary<string, int> _itemErrors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _io = new(1, 1);

    public bool IsConnected => _client?.Connected == true;
    public string? LastError { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        if (!(isWindows ?? OperatingSystem.IsWindows)()) { LastError = NotSupportedMessage; throw new PlatformNotSupportedException(NotSupportedMessage); }
        _o = ProtocolCatalog.Options<OpcDaDeviceOptions>(device);
        var client = clientFactory?.Invoke() ?? CreateDefault();
        try
        {
            // COM 调用可能长时间阻塞（DCOM 超时可达数十秒），放到线程池；等待上限取 max(TimeoutMs, 5000) 毫秒。
            await Task.Run(() => client.Connect(_o.ProgId, string.IsNullOrEmpty(_o.Node) ? null : _o.Node, _o.UpdateRateMs), cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(device.TimeoutMs, 5000)), cancellationToken);
            _client = client; _itemErrors.Clear(); LastError = null;
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }
            LastError = ex is TimeoutException ? $"连接 OPC DA 服务器 {_o.ProgId} 超时" : $"连接 OPC DA 服务器 {_o.ProgId} 失败：{ex.Message}";
            throw new IOException(LastError, ex);
        }
    }

    private static IOpcDaClient CreateDefault()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(NotSupportedMessage);
        return new OpcDaAutomationClient();
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true } client) throw new IOException(LastError ?? "OPC DA 未连接");
        await _io.WaitAsync(cancellationToken);
        try
        {
            var ids = plan.Tags.Select(t => t.NodeId).Distinct(StringComparer.Ordinal).ToList();
            var fresh = ids.Where(i => !_itemErrors.ContainsKey(i)).ToList();
            List<OpcDaSample> samples;
            try
            {
                if (fresh.Count > 0)
                {
                    var errors = await Task.Run(() => client.AddItems(fresh), cancellationToken);
                    for (var i = 0; i < fresh.Count; i++) _itemErrors[fresh[i]] = errors[i];
                }
                var valid = ids.Where(i => _itemErrors[i] == 0).ToList();
                samples = valid.Count == 0 ? [] : [.. await Task.Run(() => client.Read(valid, _o.ReadFromDevice), cancellationToken)];
                var byId = valid.Select((id, i) => (id, s: samples[i])).ToDictionary(x => x.id, x => x.s, StringComparer.Ordinal);
                LastError = null;
                return plan.Tags.Select(t => _itemErrors[t.NodeId] != 0
                    ? new RawTagValue(t.Id, 0, TagQuality.Bad, null, $"服务器拒绝该 ItemID（{Hr(_itemErrors[t.NodeId])}）")
                    : Map(t, byId[t.NodeId], _o.TimestampsUtc)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Close($"OPC DA 读取失败：{ex.Message}");
                throw new IOException(LastError, ex);
            }
        }
        finally { _io.Release(); }
    }

    public static string Hr(int code) => $"0x{code:X8}";

    /// <summary>OPC DA 品质字（QQSSSSLL）：QQ=11 Good，01 Uncertain，00 Bad；子状态写入错误文本。</summary>
    public static (TagQuality Quality, string? Detail) MapQuality(short quality)
    {
        var q = (quality & 0xC0) >> 6; var sub = (quality & 0x3C) >> 2;
        return q switch
        {
            3 => sub == 6 ? (TagQuality.Uncertain, "本地超驰（Local Override）") : (TagQuality.Good, null),
            1 => (TagQuality.Uncertain, sub switch { 1 => "最后可用值", 4 => "传感器不准确", 5 => "超出工程单位", 6 => "子正常", _ => "不确定" }),
            _ => (TagQuality.Bad, sub switch { 1 => "配置错误", 2 => "未连接", 3 => "设备故障", 4 => "传感器故障", 5 => "最后已知值", 6 => "通信故障", 7 => "停用（Out of Service）", _ => "坏值" }),
        };
    }

    public static RawTagValue Map(Tag tag, OpcDaSample s, bool timestampsUtc)
    {
        long? ts = s.Timestamp.Year < 1980 ? null
            : new DateTimeOffset(timestampsUtc || s.Timestamp.Kind == DateTimeKind.Utc ? DateTime.SpecifyKind(s.Timestamp, DateTimeKind.Utc) : DateTime.SpecifyKind(s.Timestamp, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        if (s.Error != 0) return new(tag.Id, 0, TagQuality.Bad, ts, $"读取错误（{Hr(s.Error)}）");
        var (quality, detail) = MapQuality(s.Quality);
        if (ToDouble(s.Value) is not double v) return new(tag.Id, 0, TagQuality.Bad, ts, s.Value is null ? "值为空" : $"值类型不受支持（{s.Value.GetType().Name}）");
        if (tag.DataType == TagDataType.Bool) v = v != 0 ? 1 : 0;
        return new(tag.Id, v, quality, ts, detail);
    }

    public static double? ToDouble(object? value) => value switch
    {
        bool b => b ? 1 : 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) is var d && double.IsFinite(d) ? d : null,
        _ => null,
    };

    /// <summary>按测点类型把 double 转成 VARIANT 规范类型；越界直接拒绝。</summary>
    public static object ToVariant(TagDataType type, double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("写入值无效");
        double Whole() => value == Math.Floor(value) ? value : throw new InvalidOperationException("整数类型测点不能写入小数");
        try
        {
            return type switch
            {
                TagDataType.Bool => value != 0,
                TagDataType.Int16 => checked((short)Whole()),
                TagDataType.UInt16 => checked((ushort)Whole()),
                TagDataType.Int32 => checked((int)Whole()),
                TagDataType.UInt32 => checked((uint)Whole()),
                TagDataType.Float => Math.Abs(value) <= float.MaxValue ? (float)value : throw new OverflowException(),
                _ => value,
            };
        }
        catch (OverflowException) { throw new InvalidOperationException($"写入值超出 {type} 范围"); }
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (_client is not { Connected: true } client) throw new IOException("OPC DA 未连接，命令未下发");
        var variant = ToVariant(tag.DataType, value);
        await _io.WaitAsync(cancellationToken);
        try
        {
            if (!_itemErrors.TryGetValue(tag.NodeId, out var addError)) { addError = client.AddItems([tag.NodeId])[0]; _itemErrors[tag.NodeId] = addError; }
            if (addError != 0) throw new InvalidOperationException($"服务器拒绝该 ItemID（{Hr(addError)}），命令未执行");
            int code;
            try { code = await Task.Run(() => client.Write(tag.NodeId, variant), cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Close($"OPC DA 写入通信异常：{ex.Message}");
                throw new TimeoutException($"OPC DA 写入通信异常，结果未知：{ex.Message}", ex);
            }
            if (code != 0) throw new InvalidOperationException($"OPC DA 服务器拒绝写入（{Hr(code)}），命令未执行");
            LastError = null;
        }
        finally { _io.Release(); }
    }

    private void Close(string reason)
    {
        LastError = reason;
        var c = _client; _client = null;
        try { c?.Dispose(); } catch { }
    }

    public void Disconnect() => Close("已断开");
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
