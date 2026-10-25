using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Scada.Collector.Tests;

/// <summary>FINS PLC 模拟器：FINS/UDP 与 FINS/TCP（节点握手），实现 0101/0102 内存区读写（字与位区域码）。</summary>
internal sealed class FinsSimulator : IAsyncDisposable
{
    private readonly TcpListener? _tcp;
    private readonly UdpClient? _udp;
    private readonly CancellationTokenSource _stop = new();
    public Dictionary<byte, ushort[]> Memory { get; } = new() { [0xB0] = new ushort[8192], [0xB1] = new ushort[8192], [0xB2] = new ushort[8192], [0xB3] = new ushort[8192], [0x82] = new ushort[32768], [0x89] = new ushort[65536] };
    public byte Node { get; init; } = 10;
    public byte AssignedClientNode { get; init; } = 33;
    public int Port { get; }
    public int Requests;
    public bool CorruptSid;

    public FinsSimulator(bool tcp)
    {
        if (tcp) { _tcp = new TcpListener(IPAddress.Loopback, 0); _tcp.Start(); Port = ((IPEndPoint)_tcp.LocalEndpoint).Port; _ = Accept(); }
        else { _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port; _ = Udp(); }
    }

    private static ushort Area(byte code) => code switch { 0x30 => 0xB0, 0x31 => 0xB1, 0x32 => 0xB2, 0x33 => 0xB3, 0x02 => 0x82, _ => code };

    private byte[] Handle(byte[] f)
    {
        Interlocked.Increment(ref Requests);
        var command = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(10));
        var code = f[12]; var word = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(13)); var bit = f[15]; var count = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(16));
        var bitArea = code is 0x30 or 0x31 or 0x32 or 0x33 or 0x02;
        ushort end = 0; byte[] data = [];
        if (!Memory.TryGetValue((byte)Area(code), out var mem)) end = 0x1101;
        else if (command == 0x0101)
        {
            data = new byte[count * 2];
            for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * 2), mem[word + i]);
        }
        else if (command == 0x0102)
        {
            if (bitArea) { for (var i = 0; i < count; i++) { var b = bit + i; ref var w = ref mem[word + b / 16]; w = (ushort)(f[18 + i] != 0 ? w | 1 << (b % 16) : w & ~(1 << (b % 16))); } }
            else for (var i = 0; i < count; i++) mem[word + i] = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(18 + i * 2));
        }
        else end = 0x0401;
        var r = new byte[14 + data.Length];
        r[0] = 0xC0; r[2] = 0x02; r[3] = f[6]; r[4] = f[7]; r[5] = f[8]; r[6] = f[3]; r[7] = f[4]; r[8] = f[5]; r[9] = CorruptSid ? (byte)(f[9] + 1) : f[9];
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(10), command); BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(12), (ushort)(end | 0x0040)); // 附带非致命错误标志位，客户端应忽略
        data.CopyTo(r, 14);
        if (end != 0) BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(12), end);
        return r;
    }

    private async Task Udp()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { var r = await _udp!.ReceiveAsync(_stop.Token); await _udp.SendAsync(Handle(r.Buffer), r.RemoteEndPoint, _stop.Token); }
            catch { if (_stop.IsCancellationRequested) return; }
        }
    }

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c; try { c = await _tcp!.AcceptTcpClientAsync(_stop.Token); } catch { return; }
            _ = Serve(c);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client; var s = client.GetStream();
        try
        {
            while (true)
            {
                var head = new byte[8]; await s.ReadExactlyAsync(head, _stop.Token);
                var rest = new byte[BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4))]; await s.ReadExactlyAsync(rest, _stop.Token);
                var cmd = BinaryPrimitives.ReadUInt32BigEndian(rest);
                byte[] payload; uint outCmd;
                if (cmd == 0)
                {
                    var requested = BinaryPrimitives.ReadUInt32BigEndian(rest.AsSpan(8));
                    payload = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(payload, requested == 0 ? AssignedClientNode : requested); BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), Node); outCmd = 1;
                }
                else { payload = Handle(rest[8..]); outCmd = 2; }
                var frame = new byte[16 + payload.Length];
                "FINS"u8.CopyTo(frame); BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)(8 + payload.Length)); BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), outCmd);
                payload.CopyTo(frame, 16);
                await s.WriteAsync(frame, _stop.Token);
            }
        }
        catch { }
    }

    public ValueTask DisposeAsync() { _stop.Cancel(); _tcp?.Stop(); _udp?.Dispose(); return ValueTask.CompletedTask; }
}
