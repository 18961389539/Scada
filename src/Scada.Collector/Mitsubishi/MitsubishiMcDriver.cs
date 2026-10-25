using Scada.Collector.Common;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Mitsubishi;

/// <summary>三菱 MC 协议 3E 帧客户端（Q/L/iQ-R/iQ-F 以太网口），TCP 或 UDP，二进制或 ASCII。</summary>
public sealed class MitsubishiMcDriver : IDeviceDriver
{
    private const int MaxWords = 480, MaxBits = 1792; // 3E 单次读取上限 960 字/7168 点，取保守值兼容 FX5/L
    private IpChannel? _channel;
    private McDeviceOptions _o = new();
    public bool IsConnected => _channel?.IsOpen == true;
    public string? LastError { get; private set; }
    public int LastRequestCount { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        _o = ProtocolCatalog.Options<McDeviceOptions>(device);
        try { _channel = await IpChannel.OpenAsync(device.Host, device.Port, _o.Transport == IpTransport.Udp, device.TimeoutMs, cancellationToken); LastError = null; }
        catch (Exception ex) { LastError = ex.Message; throw; }
    }

    private async Task<ReadOnlyMemory<byte>> SendAsync(Device device, byte[] request, CancellationToken ct)
    {
        var channel = _channel ?? throw new IOException("MC 尚未连接");
        var response = await channel.RequestAsync(request, span => McFrame.ResponseLength(_o.Format, span), device.TimeoutMs, ct);
        return McFrame.ResponseData(_o, response).ToArray();
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var values = new List<RawTagValue>();
        var parsed = new List<(Tag Tag, McAddress Address)>();
        foreach (var tag in plan.Tags)
        {
            if (McAddress.TryParse(tag.NodeId, out var a, out var e)) parsed.Add((tag, a));
            else values.Add(new(tag.Id, 0, TagQuality.Bad, null, e));
        }
        var requests = 0;
        try
        {
            foreach (var group in parsed.GroupBy(x => x.Address.Device.Name))
            {
                var kind = group.First().Address.Device;
                var spans = SpanMerger.Merge(group.Select(x => (x, x.Address.Number, kind.IsBit ? 1 : x.Address.Words(x.Tag.DataType))), _o.MaxGap, kind.IsBit ? MaxBits : MaxWords);
                foreach (var span in spans)
                {
                    requests++;
                    ReadOnlyMemory<byte> data;
                    try { data = await SendAsync(device, McFrame.Request(_o, McFrame.CmdBatchRead, kind.IsBit ? McFrame.SubBit : McFrame.SubWord, kind, span.Start, span.Length), cancellationToken); }
                    catch (McEndCodeException ex)
                    {
                        // PLC 拒绝了该区段（地址越界、软元件不存在等）：只标记该区段测点坏质量，连接与其他区段不受影响。
                        foreach (var (item, _, _) in span.Items) values.Add(new(item.Tag.Id, 0, TagQuality.Bad, null, $"{kind.Name}{span.Start}～：{ex.Message}"));
                        continue;
                    }
                    if (kind.IsBit)
                    {
                        var bits = McFrame.Bits(_o.Format, data.Span, span.Length);
                        foreach (var (item, offset, _) in span.Items) values.Add(new(item.Tag.Id, bits[offset] ? 1 : 0));
                        continue;
                    }
                    var words = McFrame.Words(_o.Format, data.Span, span.Length);
                    foreach (var (item, offset, length) in span.Items)
                    {
                        var slice = words.AsSpan(offset, length);
                        var value = item.Address.Bit is int bit ? (slice[0] >> bit) & 1 : WordCodec.Decode(slice, item.Tag.DataType, !_o.HighWordFirst);
                        values.Add(double.IsFinite(value) ? new(item.Tag.Id, value) : new(item.Tag.Id, 0, TagQuality.Bad, null, "PLC 返回非有限浮点值"));
                    }
                }
            }
            LastRequestCount = requests; LastError = null;
            return values;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            // 结束代码错误属于点位/地址问题，连接仍可用；通信错误时 IpChannel 已关闭，由引擎重连。
            throw new IOException(ex.Message, ex);
        }
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!McAddress.TryParse(tag.NodeId, out var a, out var error)) throw new InvalidOperationException(error);
        if (a.Bit is not null) throw new InvalidOperationException("字的位地址只读");
        try
        {
            byte[] request;
            if (a.Device.IsBit)
            {
                var (bin, ascii) = McFrame.BitData(value != 0);
                request = McFrame.Request(_o, McFrame.CmdBatchWrite, McFrame.SubBit, a.Device, a.Number, 1, bin, ascii);
            }
            else
            {
                var words = WordCodec.Encode(value, tag.DataType, !_o.HighWordFirst);
                var (bin, ascii) = McFrame.WordData(words);
                request = McFrame.Request(_o, McFrame.CmdBatchWrite, McFrame.SubWord, a.Device, a.Number, words.Length, bin, ascii);
            }
            await SendAsync(device, request, cancellationToken);
            LastError = null;
        }
        catch (TimeoutException ex) { LastError = "MC 写入超时，结果未知"; throw new TimeoutException(LastError, ex); }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException and not OverflowException) { LastError = ex.Message; throw; }
    }

    public void Disconnect() { _channel?.Close(); _channel = null; }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
