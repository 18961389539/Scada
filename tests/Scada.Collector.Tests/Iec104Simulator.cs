using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Scada.Collector.Iec104;
using static Scada.Collector.Iec104.Iec104Types;

namespace Scada.Collector.Tests;

/// <summary>IEC 104 子站模拟器：STARTDT/TESTFR、总召唤/电度量召唤/对时应答、遥控遥调选择-执行，并可主动发送自发数据。</summary>
internal sealed class Iec104Simulator : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _vs, _vr;
    public int Port { get; }
    public int CommonAddress { get; init; } = 7;
    public ConcurrentQueue<Asdu> Received { get; } = new();
    public ConcurrentQueue<byte> UFrames { get; } = new();
    public int SFrames;
    /// <summary>总召唤时上送的点：(类型, 信息对象地址, 元素)。</summary>
    public List<(byte Type, int Ioa, byte[] Element)> Image { get; } = [];
    public bool RejectSelect, DropActTerm, DropExecuteCon, IgnoreTestFr;
    public Action<byte, int, byte[]>? OnExecute;
    public TaskCompletionSource Connected { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Iec104Simulator() { _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _ = Accept(); }

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c; try { c = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
            _vs = _vr = 0; _stream = c.GetStream(); Connected.TrySetResult();
            _ = Serve(c);
        }
    }

    public void Drop() { _stream?.Dispose(); Connected = new(TaskCreationOptions.RunContinuationsAsynchronously); }

    private async Task Serve(TcpClient client)
    {
        using var _ = client; var s = client.GetStream();
        try
        {
            while (true)
            {
                var head = new byte[2]; await s.ReadExactlyAsync(head, _stop.Token);
                var body = new byte[head[1]]; await s.ReadExactlyAsync(body, _stop.Token);
                var apdu = Iec104Codec.Decode([.. head, .. body]);
                if (apdu.Kind == ApciKind.U)
                {
                    UFrames.Enqueue(apdu.UFunction);
                    if (apdu.UFunction == U_StartDtAct) await Raw(s, Iec104Codec.U(U_StartDtCon));
                    if (apdu.UFunction == U_TestFrAct && !IgnoreTestFr) await Raw(s, Iec104Codec.U(U_TestFrCon));
                    continue;
                }
                if (apdu.Kind == ApciKind.S) { Interlocked.Increment(ref SFrames); continue; }
                _vr = (_vr + 1) % 32768;
                var a = apdu.Asdu!; Received.Enqueue(a);
                await Respond(a);
            }
        }
        catch { }
    }

    private async Task Raw(NetworkStream s, byte[] frame) { await _lock.WaitAsync(); try { await s.WriteAsync(frame); } finally { _lock.Release(); } }

    public async Task Send(byte type, byte cause, int ioa, byte[] element, bool negative = false, int? ca = null)
    {
        var s = _stream ?? throw new InvalidOperationException("未连接");
        await _lock.WaitAsync();
        try
        {
            var frame = Iec104Codec.I(_vs, _vr, new Asdu(type, false, cause, negative, false, 0, ca ?? CommonAddress, [new InfoObject(ioa, element)]));
            _vs = (_vs + 1) % 32768;
            await s.WriteAsync(frame);
        }
        finally { _lock.Release(); }
    }

    /// <summary>发送带顺序（SQ=1）的多元素 ASDU。</summary>
    public async Task SendSequence(byte type, byte cause, int firstIoa, params byte[][] elements)
    {
        var s = _stream!;
        await _lock.WaitAsync();
        try
        {
            var objects = elements.Select((e, i) => new InfoObject(firstIoa + i, e)).ToArray();
            var frame = Iec104Codec.I(_vs, _vr, new Asdu(type, true, cause, false, false, 0, CommonAddress, objects));
            _vs = (_vs + 1) % 32768; await s.WriteAsync(frame);
        }
        finally { _lock.Release(); }
    }

    public void SkipSendSequence() => _vs = (_vs + 5) % 32768;

    private async Task Respond(Asdu a)
    {
        var o = a.Objects[0];
        switch (a.TypeId)
        {
            case C_IC_NA:
                await Send(C_IC_NA, CotActCon, 0, o.Element);
                foreach (var (type, ioa, element) in Image.ToArray()) await Send(type, CotInrogen, ioa, element);
                await Send(C_IC_NA, CotActTerm, 0, o.Element);
                break;
            case C_CI_NA:
                await Send(C_CI_NA, CotActCon, 0, o.Element);
                foreach (var (type, ioa, element) in Image.Where(x => x.Type is M_IT_NA or M_IT_TB).ToArray()) await Send(type, CotReqcogen, ioa, element);
                await Send(C_CI_NA, CotActTerm, 0, o.Element);
                break;
            case C_CS_NA:
                await Send(C_CS_NA, CotActCon, 0, o.Element);
                break;
            case C_SC_NA or C_DC_NA or C_SE_NA or C_SE_NB or C_SE_NC:
                var select = (o.Element[^1] & 0x80) != 0;
                if (a.Cause == CotDeact) { await Send(a.TypeId, 9, o.Ioa, o.Element); break; }
                if (select) { await Send(a.TypeId, CotActCon, o.Ioa, o.Element, negative: RejectSelect); break; }
                if (DropExecuteCon) break;
                await Send(a.TypeId, CotActCon, o.Ioa, o.Element);
                OnExecute?.Invoke(a.TypeId, o.Ioa, o.Element);
                if (!DropActTerm) await Send(a.TypeId, CotActTerm, o.Ioa, o.Element);
                break;
        }
    }

    public static byte[] Float(float v, byte qds = 0) { var b = new byte[5]; BinaryPrimitives.WriteSingleLittleEndian(b, v); b[4] = qds; return b; }
    public static byte[] Scaled(short v, byte qds = 0) { var b = new byte[3]; BinaryPrimitives.WriteInt16LittleEndian(b, v); b[2] = qds; return b; }
    public static byte[] Counter(int v, byte seq = 0) { var b = new byte[5]; BinaryPrimitives.WriteInt32LittleEndian(b, v); b[4] = seq; return b; }

    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); _stream?.Dispose(); await Task.CompletedTask; }
}
