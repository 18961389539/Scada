using System.Buffers.Binary;
using Scada.Collector.Common;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Omron;

/// <summary>欧姆龙 FINS 客户端（CS/CJ/CP/NJ/NX 的 FINS 端口），FINS/UDP 或 FINS/TCP（含节点号握手）。</summary>
public sealed class OmronFinsDriver : IDeviceDriver
{
    private const int MaxWords = 499;
    private IpChannel? _channel;
    private FinsDeviceOptions _o = new();
    private FinsFrame.Route _route = new(0, 0, 0, 0, 0);
    private byte _sid;
    public bool IsConnected => _channel?.IsOpen == true;
    public string? LastError { get; private set; }
    public int LastRequestCount { get; private set; }
    public byte SourceNode => _route.SourceNode;
    public byte DestNode => _route.DestNode;

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        _o = ProtocolCatalog.Options<FinsDeviceOptions>(device);
        var tcp = _o.Transport == IpTransport.Tcp;
        IpChannel? channel = null;
        try
        {
            channel = await IpChannel.OpenAsync(device.Host, device.Port, !tcp, device.TimeoutMs, cancellationToken);
            byte source = (byte)_o.SourceNode, dest = (byte)_o.DestNode;
            if (tcp)
            {
                var reply = await channel.RequestAsync(FinsFrame.TcpHandshake(source), FinsFrame.TcpLength, device.TimeoutMs, cancellationToken);
                var (command, _, payload) = FinsFrame.TcpUnwrap(reply);
                if (command != 1 || payload.Length < 8) throw new IOException("FINS/TCP 节点握手应答无效");
                source = (byte)BinaryPrimitives.ReadUInt32BigEndian(payload);
                var server = (byte)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
                if (dest == 0) dest = server;
            }
            _route = new((byte)_o.DestNetwork, dest, (byte)_o.DestUnit, (byte)_o.SourceNetwork, source);
            _channel = channel; LastError = null;
        }
        catch (Exception ex) { channel?.Close(); LastError = ex.Message; throw; }
    }

    private async Task<byte[]> SendAsync(Device device, ushort command, byte area, int word, int bit, int count, byte[]? data, CancellationToken ct)
    {
        var channel = _channel ?? throw new IOException("FINS 尚未连接");
        var sid = ++_sid;
        var fins = FinsFrame.Command(_route, sid, command, area, word, bit, count, data);
        byte[] response;
        if (_o.Transport == IpTransport.Tcp)
        {
            var frame = await channel.RequestAsync(FinsFrame.TcpWrap(fins), FinsFrame.TcpLength, device.TimeoutMs, ct);
            var (cmd, _, payload) = FinsFrame.TcpUnwrap(frame);
            if (cmd != 2) throw new IOException($"FINS/TCP 意外的命令 {cmd}");
            response = payload;
        }
        else response = await channel.RequestAsync(fins, _ => null, device.TimeoutMs, ct);
        return FinsFrame.Response(_route, sid, command, response).ToArray();
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var values = new List<RawTagValue>();
        var parsed = new List<(Tag Tag, FinsAddress Address)>();
        foreach (var tag in plan.Tags)
            if (FinsAddress.TryParse(tag.NodeId, out var a, out var e)) parsed.Add((tag, a)); else values.Add(new(tag.Id, 0, TagQuality.Bad, null, e));
        var requests = 0;
        try
        {
            // 位测点读取所在字，与字测点一起按区域合并。
            foreach (var group in parsed.GroupBy(x => x.Address.Area.WordCode))
            {
                foreach (var span in SpanMerger.Merge(group.Select(x => (x, x.Address.Word, x.Address.Bit is null ? WordCodec.WordCount(x.Tag.DataType) : 1)), _o.MaxGap, MaxWords))
                {
                    requests++;
                    byte[] data;
                    try { data = await SendAsync(device, FinsFrame.MemoryAreaRead, group.Key, span.Start, 0, span.Length, null, cancellationToken); }
                    catch (FinsEndCodeException ex)
                    {
                        foreach (var (item, _, _) in span.Items) values.Add(new(item.Tag.Id, 0, TagQuality.Bad, null, $"{item.Address.Area.Name}{span.Start}～：{ex.Message}"));
                        continue;
                    }
                    if (data.Length != span.Length * 2) throw new IOException("FINS 应答字数不符");
                    var words = new ushort[span.Length];
                    for (var i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i * 2));
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
        catch (Exception ex) when (ex is not OperationCanceledException) { LastError = ex.Message; throw new IOException(ex.Message, ex); }
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!FinsAddress.TryParse(tag.NodeId, out var a, out var error)) throw new InvalidOperationException(error);
        try
        {
            if (a.Bit is int bit)
                // 位区域码写单个位，PLC 原子执行，不影响同字其他位。
                await SendAsync(device, FinsFrame.MemoryAreaWrite, a.Area.BitCode!.Value, a.Word, bit, 1, [(byte)(value != 0 ? 1 : 0)], cancellationToken);
            else
            {
                var words = WordCodec.Encode(value, tag.DataType, !_o.HighWordFirst);
                var data = new byte[words.Length * 2];
                for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * 2), words[i]);
                await SendAsync(device, FinsFrame.MemoryAreaWrite, a.Area.WordCode, a.Word, 0, words.Length, data, cancellationToken);
            }
            LastError = null;
        }
        catch (TimeoutException ex) { LastError = "FINS 写入超时，结果未知"; throw new TimeoutException(LastError, ex); }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException and not OverflowException) { LastError = ex.Message; throw; }
    }

    public void Disconnect() { _channel?.Close(); _channel = null; }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
