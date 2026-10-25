using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

/// <summary>MC 3E 帧 PLC 模拟器（二进制/ASCII，TCP/UDP），实现批量读写 0401/1401。</summary>
internal sealed class McSimulator : IAsyncDisposable
{
    private readonly TcpListener? _tcp;
    private readonly UdpClient? _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly McFormat _format;
    public Dictionary<string, ushort[]> Words { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool[]> Bits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Requests;
    public int Port { get; }
    public int DropNext; // 不应答的请求数（模拟丢包/超时）

    public McSimulator(McFormat format, bool udp)
    {
        _format = format;
        foreach (var kind in McAddress.Devices.Values) { if (kind.IsBit) Bits[kind.Name] = new bool[8192]; else Words[kind.Name] = new ushort[8192]; }
        if (udp) { _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port; _ = UdpLoop(); }
        else { _tcp = new TcpListener(IPAddress.Loopback, 0); _tcp.Start(); Port = ((IPEndPoint)_tcp.LocalEndpoint).Port; _ = AcceptLoop(); }
    }

    private async Task UdpLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(_stop.Token);
                var response = Handle(r.Buffer);
                if (response is not null) await _udp.SendAsync(response, r.RemoteEndPoint, _stop.Token);
            }
            catch { if (_stop.IsCancellationRequested) return; }
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp!.AcceptTcpClientAsync(_stop.Token); } catch { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client; var stream = client.GetStream(); var buffer = new byte[65536]; var count = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(count), _stop.Token); if (n == 0) return; count += n;
                while (true)
                {
                    var length = _format == McFormat.Binary ? (count < 9 ? -1 : 9 + BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(7)))
                        : (count < 18 ? -1 : 18 + int.Parse(Encoding.ASCII.GetString(buffer, 14, 4), NumberStyles.HexNumber));
                    if (length < 0 || count < length) break;
                    var response = Handle(buffer[..length]);
                    Buffer.BlockCopy(buffer, length, buffer, 0, count - length); count -= length;
                    if (response is not null) await stream.WriteAsync(response, _stop.Token);
                }
            }
        }
        catch { }
    }

    private byte[]? Handle(byte[] frame)
    {
        Interlocked.Increment(ref Requests);
        if (DropNext > 0) { DropNext--; return null; }
        ushort command, sub; McDeviceKind kind; int head, points; byte[] payload;
        byte network, pc;
        if (_format == McFormat.Binary)
        {
            network = frame[2]; pc = frame[3];
            command = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)); sub = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(13));
            head = frame[15] | frame[16] << 8 | frame[17] << 16;
            kind = McAddress.Devices.Values.Single(x => x.BinaryCode == frame[18]);
            points = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(19));
            payload = frame[21..];
        }
        else
        {
            var t = Encoding.ASCII.GetString(frame);
            network = byte.Parse(t.AsSpan(4, 2), NumberStyles.HexNumber); pc = byte.Parse(t.AsSpan(6, 2), NumberStyles.HexNumber);
            command = ushort.Parse(t.AsSpan(22, 4), NumberStyles.HexNumber); sub = ushort.Parse(t.AsSpan(26, 4), NumberStyles.HexNumber);
            var code = t.Substring(30, 2);
            kind = McAddress.Devices.Values.Single(x => x.AsciiCode == code);
            head = int.Parse(t.AsSpan(32, 6), kind.HexNumber ? NumberStyles.HexNumber : NumberStyles.None);
            points = int.Parse(t.AsSpan(38, 4), NumberStyles.HexNumber);
            payload = frame[42..];
        }
        if (Rejected.Contains(kind.Name)) return Reply(network, pc, 0xC056, []);
        var bit = sub == 1;
        if (command == 0x0401)
        {
            if (bit)
            {
                var bits = Bits.TryGetValue(kind.Name, out var b) ? b : Words[kind.Name].SelectMany(w => Enumerable.Range(0, 16).Select(i => (w >> i & 1) != 0)).ToArray();
                if (_format == McFormat.Binary)
                {
                    var data = new byte[(points + 1) / 2];
                    for (var i = 0; i < points; i++) if (bits[head + i]) data[i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
                    return Reply(network, pc, 0, data);
                }
                return Reply(network, pc, 0, Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, points).Select(i => bits[head + i] ? '1' : '0'))));
            }
            var words = Words.TryGetValue(kind.Name, out var w0) ? w0 : null;
            if (words is null) return Reply(network, pc, 0xC05B, []);
            var slice = words.AsSpan(head, points).ToArray();
            return Reply(network, pc, 0, _format == McFormat.Binary ? slice.SelectMany(x => new[] { (byte)x, (byte)(x >> 8) }).ToArray()
                : Encoding.ASCII.GetBytes(string.Concat(slice.Select(x => x.ToString("X4")))));
        }
        if (command == 0x1401)
        {
            if (bit)
            {
                for (var i = 0; i < points; i++)
                    Bits[kind.Name][head + i] = _format == McFormat.Binary ? ((i % 2 == 0 ? payload[i / 2] >> 4 : payload[i / 2]) & 1) != 0 : payload[i] == (byte)'1';
            }
            else
                for (var i = 0; i < points; i++)
                    Words[kind.Name][head + i] = _format == McFormat.Binary ? BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(i * 2))
                        : ushort.Parse(Encoding.ASCII.GetString(payload, i * 4, 4), NumberStyles.HexNumber);
            return Reply(network, pc, 0, []);
        }
        return Reply(network, pc, 0xC059, []);
    }

    private byte[] Reply(byte network, byte pc, ushort end, byte[] data)
    {
        if (_format == McFormat.Binary)
        {
            var r = new byte[11 + data.Length];
            r[0] = 0xD0; r[2] = network; r[3] = pc; r[4] = 0xFF; r[5] = 0x03;
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(7), (ushort)(2 + data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(9), end);
            data.CopyTo(r, 11); return r;
        }
        var body = $"{end:X4}" + Encoding.ASCII.GetString(data);
        return Encoding.ASCII.GetBytes($"D000{network:X2}{pc:X2}03FF00{body.Length:X4}{body}");
    }

    public ValueTask DisposeAsync() { _stop.Cancel(); _tcp?.Stop(); _udp?.Dispose(); return ValueTask.CompletedTask; }
}
