using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Iec61850;

public sealed record Iec61850Sample(double? Value, ushort? Quality, long? TimestampMs, string? Error);

/// <summary>
/// IEC 61850 MMS 客户端适配接口。本产品未内置实现：libiec61850 为 GPLv3/商业双许可不能直接引入，
/// 自研 ISO 栈（TPKT/COTP/会话/表示/ACSE）+ MMS 超出本期范围。接入方可提供商业许可库或自研实现并注册到 <see cref="Iec61850Driver"/>。
/// </summary>
public interface IIec61850Client : IDisposable
{
    bool Connected { get; }
    Task ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct);
    /// <summary>读取数据属性，连同所属数据对象的 q / t（不存在时为 null）。</summary>
    Task<IReadOnlyList<Iec61850Sample>> ReadAsync(IReadOnlyList<Iec61850Reference> references, CancellationToken ct);
    /// <summary>CO：按控制模型（直接/SBO，普通/增强安全）执行 Oper；其他 FC：写属性。</summary>
    Task WriteAsync(Iec61850Reference reference, double value, CancellationToken ct);
}

public sealed class Iec61850Driver(Func<IIec61850Client>? clientFactory = null) : IDeviceDriver
{
    public const string NotAvailableMessage =
        "IEC 61850 MMS 客户端尚未提供实现：libiec61850 为 GPLv3 许可不能内置，需提供商业许可库或自研实现（见 docs/protocols/iec61850.md）。";

    private IIec61850Client? _client;
    public bool IsConnected => _client?.Connected == true;
    public string? LastError { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        if (clientFactory is null) { LastError = NotAvailableMessage; throw new NotSupportedException(NotAvailableMessage); }
        var client = clientFactory();
        try { await client.ConnectAsync(device.Host, device.Port, device.TimeoutMs, cancellationToken); _client = client; LastError = null; }
        catch (Exception ex) { client.Dispose(); LastError = ex.Message; throw; }
    }

    /// <summary>IEC 61850 品质（validity 2 位 + detailQual + source/test/operatorBlocked）。</summary>
    public static (TagQuality Quality, string? Detail) MapQuality(ushort? q)
    {
        if (q is not ushort quality) return (TagQuality.Good, null);
        // 适配器约定：低 2 位为 validity 枚举（0 good、1 invalid、2 reserved、3 questionable），bit2～9 detailQual，bit10 source=substituted，bit11 test，bit12 operatorBlocked
        var validity = quality & 0x3;
        if (validity == 1) return (TagQuality.Bad, "无效（invalid）");
        if (validity == 2) return (TagQuality.Bad, "保留的 validity 值");
        if (validity == 3) return (TagQuality.Uncertain, "可疑（questionable）");
        if ((quality & (1 << 11)) != 0) return (TagQuality.Uncertain, "测试（test）");
        if ((quality & (1 << 12)) != 0) return (TagQuality.Uncertain, "操作员闭锁（operatorBlocked）");
        if ((quality & (1 << 10)) != 0) return (TagQuality.Uncertain, "被取代（substituted）");
        return (TagQuality.Good, null);
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true } client) throw new IOException(LastError ?? "IEC 61850 未连接");
        var refs = new List<(Tag Tag, Iec61850Reference Ref)>();
        var result = new List<RawTagValue>();
        foreach (var tag in plan.Tags)
        {
            if (Iec61850Reference.TryParse(tag.NodeId, out var r, out var error)) refs.Add((tag, r));
            else result.Add(new(tag.Id, 0, TagQuality.Bad, null, error));
        }
        IReadOnlyList<Iec61850Sample> samples;
        try { samples = await client.ReadAsync(refs.Select(x => x.Ref).ToList(), cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LastError = ex.Message; Disconnect(); LastError = ex.Message; throw new IOException(ex.Message, ex); }
        for (var i = 0; i < refs.Count; i++)
        {
            var (tag, _) = refs[i]; var s = samples[i];
            if (s.Error is not null || s.Value is null) { result.Add(new(tag.Id, 0, TagQuality.Bad, s.TimestampMs, s.Error ?? "无值")); continue; }
            var (quality, detail) = MapQuality(s.Quality);
            result.Add(new(tag.Id, tag.DataType == TagDataType.Bool ? (s.Value != 0 ? 1 : 0) : s.Value.Value, quality, s.TimestampMs, detail));
        }
        return result;
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!Iec61850Reference.TryParse(tag.NodeId, out var r, out var error)) throw new InvalidOperationException(error);
        if (_client is not { Connected: true } client) throw new IOException("IEC 61850 未连接，命令未下发");
        await client.WriteAsync(r, value, cancellationToken);
    }

    public void Disconnect() { var c = _client; _client = null; LastError ??= "已断开"; try { c?.Dispose(); } catch { } }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
