using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;
using static Scada.Collector.Iec104.Iec104Types;

namespace Scada.Collector.Iec104;

/// <summary>
/// IEC 60870-5-104 主站（客户端）。STARTDT/TESTFR、k/w 窗口与 t1/t2/t3 定时器、总召唤与电度量召唤、对时，
/// 缓存自发/召唤上送的遥信遥测（含品质与时标）；遥控/遥调按选择-执行（或直接执行）并等待 ACTCON/ACTTERM。
/// </summary>
public sealed class Iec104Driver : IDeviceDriver
{
    private sealed record Point(double Value, int RawDpi, TagQuality Quality, long Ts, string? Error);
    private sealed record Confirmation(byte Cause, bool Negative);

    private Iec104DeviceOptions _o = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _life;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly ConcurrentDictionary<(Iec104Monitor, int), Point> _points = new();
    private readonly ConcurrentDictionary<(byte Type, int Ioa), Channel> _waiters = new();
    private volatile bool _open;
    private int _vs, _vr, _peerAck, _unackedRecv;
    private long _firstUnackedRecvTs, _lastRxTs, _testSentTs, _oldestUnackedSendTs, _lastGiTs, _lastCiTs;
    private TaskCompletionSource? _startDt;
    private TaskCompletionSource _ackSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _giComplete;
    private Device? _device;

    private sealed class Channel { public readonly System.Threading.Channels.Channel<Confirmation> Queue = System.Threading.Channels.Channel.CreateUnbounded<Confirmation>(); }

    public bool IsConnected => _open;
    public string? LastError { get; private set; }
    public bool GeneralInterrogationComplete => _giComplete;
    public long ReceivedIFrames { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        _o = ProtocolCatalog.Options<Iec104DeviceOptions>(device);
        _device = device;
        _points.Clear(); _giComplete = false; _vs = _vr = _peerAck = _unackedRecv = 0; _testSentTs = 0; _oldestUnackedSendTs = 0;
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(device.TimeoutMs);
            try { await tcp.ConnectAsync(device.Host, device.Port, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException($"连接 {device.Host}:{device.Port} 超时（t0）"); }
            _tcp = tcp; _stream = tcp.GetStream(); _open = true; _lastRxTs = Environment.TickCount64;
            _life = new CancellationTokenSource();
            _startDt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ReceiveLoop(_life.Token);
            await SendRaw(Iec104Codec.U(U_StartDtAct), cancellationToken);
            try { await _startDt.Task.WaitAsync(TimeSpan.FromSeconds(_o.T1Sec), cancellationToken); }
            catch (TimeoutException) { throw new TimeoutException("STARTDT 未在 t1 内确认"); }
            if (_o.ClockSync) await SendAsdu(C_CS_NA, CotAct, 0, Iec104Codec.Cp56(_o.DeviceTimeUtc ? DateTime.UtcNow : DateTime.Now), cancellationToken);
            await GeneralInterrogation(cancellationToken);
            if (_o.CounterIntervalSec > 0) await CounterInterrogation(cancellationToken);
            _ = TimerLoop(_life.Token);
            LastError = null;
        }
        catch (Exception ex) { LastError = ex.Message; Close(ex.Message); tcp.Dispose(); throw; }
    }

    private Task GeneralInterrogation(CancellationToken ct) { _lastGiTs = Environment.TickCount64; return SendAsdu(C_IC_NA, CotAct, 0, [20], ct); }
    private Task CounterInterrogation(CancellationToken ct) { _lastCiTs = Environment.TickCount64; return SendAsdu(C_CI_NA, CotAct, 0, [5], ct); }

    private async Task SendRaw(byte[] frame, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("104 尚未连接");
        await _send.WaitAsync(ct);
        try { await stream.WriteAsync(frame, ct); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { Close(ex.Message); throw new IOException($"104 发送失败：{ex.Message}", ex); }
        finally { _send.Release(); }
    }

    private async Task SendAsdu(byte type, byte cause, int ioa, byte[] element, CancellationToken ct)
    {
        // k 窗口：未确认的已发 I 帧达到 k 时等待对端确认，超过 t1 则断开。
        var until = Environment.TickCount64 + _o.T1Sec * 1000L;
        while (Outstanding() >= _o.K)
        {
            if (Environment.TickCount64 > until) { Close("k 窗口确认超时（t1）"); throw new IOException("104 发送窗口已满且 t1 内未确认"); }
            var signal = Volatile.Read(ref _ackSignal).Task;
            await Task.WhenAny(signal, Task.Delay(100, ct));
        }
        await _send.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new IOException("104 尚未连接");
            var asdu = new Asdu(type, false, cause, false, false, (byte)_o.OriginatorAddress, _o.CommonAddress, [new InfoObject(ioa, element)]);
            var frame = Iec104Codec.I(_vs, _vr, asdu);
            if (Outstanding() == 0) _oldestUnackedSendTs = Environment.TickCount64;
            _vs = (_vs + 1) % 32768;
            _unackedRecv = 0; // I 帧携带 N(R)，同时确认了已收帧
            await stream.WriteAsync(frame, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { Close(ex.Message); throw new IOException($"104 发送失败：{ex.Message}", ex); }
        finally { _send.Release(); }
    }

    private int Outstanding() => (_vs - Volatile.Read(ref _peerAck) + 32768) % 32768;

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[512]; var count = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _stream!.ReadAsync(buffer.AsMemory(count), ct);
                if (n == 0) { Close("子站关闭了连接"); return; }
                count += n;
                while (Iec104Codec.FrameLength(buffer.AsSpan(0, count)) is int len && count >= len)
                {
                    var apdu = Iec104Codec.Decode(buffer.AsSpan(0, len));
                    Buffer.BlockCopy(buffer, len, buffer, 0, count - len); count -= len;
                    _lastRxTs = Environment.TickCount64; _testSentTs = 0;
                    await Handle(apdu, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Close($"104 接收错误：{ex.Message}"); }
    }

    private void PeerAcknowledged(int recv)
    {
        var outstanding = Outstanding();
        var acked = (recv - Volatile.Read(ref _peerAck) + 32768) % 32768;
        if (acked > outstanding) throw new IOException($"对端确认序号 {recv} 超出已发送范围");
        Volatile.Write(ref _peerAck, recv);
        _oldestUnackedSendTs = Outstanding() == 0 ? 0 : Environment.TickCount64;
        Interlocked.Exchange(ref _ackSignal, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private async Task Handle(Apdu apdu, CancellationToken ct)
    {
        switch (apdu.Kind)
        {
            case ApciKind.U:
                if (apdu.UFunction == U_TestFrAct) await SendRaw(Iec104Codec.U(U_TestFrCon), ct);
                else if (apdu.UFunction == U_StartDtCon) _startDt?.TrySetResult();
                return;
            case ApciKind.S:
                PeerAcknowledged(apdu.RecvSeq);
                return;
        }
        if (apdu.SendSeq != _vr) throw new IOException($"I 帧发送序号错误（期望 {_vr}，收到 {apdu.SendSeq}）");
        _vr = (_vr + 1) % 32768; ReceivedIFrames++;
        PeerAcknowledged(apdu.RecvSeq);
        if (_unackedRecv++ == 0) _firstUnackedRecvTs = Environment.TickCount64;
        if (_unackedRecv >= _o.W) await AckReceived(ct);
        var asdu = apdu.Asdu!;
        if (asdu.CommonAddress != _o.CommonAddress && asdu.CommonAddress != 0xFFFF) return;
        if (Iec104Codec.ElementLength(asdu.TypeId) < 0) { LastError = $"子站上送了不支持的类型标识 {asdu.TypeId}"; return; }
        switch (asdu.TypeId)
        {
            case C_IC_NA:
                if (asdu.Cause == CotActTerm) _giComplete = true;
                if (asdu.Negative) LastError = "子站拒绝总召唤";
                return;
            case C_CI_NA: case C_CS_NA:
                if (asdu.Negative) LastError = asdu.TypeId == C_CS_NA ? "子站拒绝对时" : "子站拒绝电度量召唤";
                return;
            case M_EI_NA:
                // 子站初始化结束：重新总召唤以获取完整状态。
                _ = GeneralInterrogation(ct).ContinueWith(t => LastError = t.Exception?.GetBaseException().Message, TaskContinuationOptions.OnlyOnFaulted);
                return;
            case C_SC_NA or C_DC_NA or C_SE_NA or C_SE_NB or C_SE_NC:
                foreach (var o in asdu.Objects)
                    if (_waiters.TryGetValue((asdu.TypeId, o.Ioa), out var w)) w.Queue.Writer.TryWrite(new(asdu.Cause, asdu.Negative));
                return;
        }
        var receivedTs = ScadaTime.NowMs();
        foreach (var o in asdu.Objects)
        {
            if (Decode(asdu.TypeId, o.Element, receivedTs) is { } decoded) _points[(decoded.Monitor, o.Ioa)] = decoded.Point;
        }
    }

    private async Task AckReceived(CancellationToken ct) { _unackedRecv = 0; await SendRaw(Iec104Codec.S(_vr), ct); }

    private (Iec104Monitor Monitor, Point Point)? Decode(byte type, byte[] e, long receivedTs)
    {
        long Ts(int offset) => e.Length >= offset + 7 ? Iec104Codec.Cp56Ms(e.AsSpan(offset), _o.DeviceTimeUtc) ?? receivedTs : receivedTs;
        (TagQuality, string?) Q(byte q)
        {
            if ((q & 0x80) != 0) return (TagQuality.Bad, "无效（IV）");
            if ((q & 0x40) != 0) return (TagQuality.Uncertain, "非当前值（NT）");
            if ((q & 0x20) != 0) return (TagQuality.Uncertain, "被取代（SB）");
            if ((q & 0x10) != 0) return (TagQuality.Uncertain, "被闭锁（BL）");
            if ((q & 0x01) != 0 && type is not (M_SP_NA or M_SP_TB or M_DP_NA or M_DP_TB)) return (TagQuality.Uncertain, "溢出（OV）");
            return (TagQuality.Good, null);
        }
        Point P(double v, byte q, long ts, int dpi = -1) { var (quality, err) = Q(q); return new(v, dpi, quality, ts, err); }
        switch (type)
        {
            case M_SP_NA or M_SP_TB: return (Iec104Monitor.SP, P(e[0] & 1, e[0], type == M_SP_TB ? Ts(1) : receivedTs));
            case M_DP_NA or M_DP_TB: return (Iec104Monitor.DP, P(e[0] & 3, e[0], type == M_DP_TB ? Ts(1) : receivedTs, e[0] & 3));
            case M_ST_NA or M_ST_TB: { var v = (sbyte)(e[0] << 1) >> 1; return (Iec104Monitor.ST, P(v, e[1], type == M_ST_TB ? Ts(2) : receivedTs)); }
            case M_BO_NA or M_BO_TB: return (Iec104Monitor.BO, P(BinaryPrimitives.ReadUInt32LittleEndian(e), e[4], type == M_BO_TB ? Ts(5) : receivedTs));
            case M_ME_NA or M_ME_TD: return (Iec104Monitor.ME_NA, P(BinaryPrimitives.ReadInt16LittleEndian(e) / 32768.0, e[2], type == M_ME_TD ? Ts(3) : receivedTs));
            case M_ME_ND: return (Iec104Monitor.ME_NA, new(BinaryPrimitives.ReadInt16LittleEndian(e) / 32768.0, -1, TagQuality.Good, receivedTs, null));
            case M_ME_NB or M_ME_TE: return (Iec104Monitor.ME_NB, P(BinaryPrimitives.ReadInt16LittleEndian(e), e[2], type == M_ME_TE ? Ts(3) : receivedTs));
            case M_ME_NC or M_ME_TF:
            {
                var f = BinaryPrimitives.ReadSingleLittleEndian(e);
                return (Iec104Monitor.ME_NC, double.IsFinite(f) ? P(f, e[4], type == M_ME_TF ? Ts(5) : receivedTs) : new(0, -1, TagQuality.Bad, receivedTs, "非有限浮点值"));
            }
            case M_IT_NA or M_IT_TB:
            {
                var seq = e[4];
                var quality = (seq & 0x80) != 0 ? TagQuality.Bad : (seq & 0x40) != 0 ? TagQuality.Uncertain : TagQuality.Good;
                var error = (seq & 0x80) != 0 ? "无效（IV）" : (seq & 0x40) != 0 ? "计数器被调整（CA）" : null;
                return (Iec104Monitor.IT, new(BinaryPrimitives.ReadInt32LittleEndian(e), -1, quality, type == M_IT_TB ? Ts(5) : receivedTs, error));
            }
        }
        return null;
    }

    private async Task TimerLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _open)
            {
                await Task.Delay(200, ct);
                var now = Environment.TickCount64;
                if (_unackedRecv > 0 && now - _firstUnackedRecvTs >= _o.T2Sec * 1000L) await AckReceived(ct);
                if (_oldestUnackedSendTs != 0 && Outstanding() > 0 && now - _oldestUnackedSendTs > _o.T1Sec * 1000L) { Close("已发 I 帧在 t1 内未被确认"); return; }
                if (_testSentTs != 0 && now - _testSentTs > _o.T1Sec * 1000L) { Close("TESTFR 在 t1 内未确认，链路中断"); return; }
                if (_testSentTs == 0 && now - _lastRxTs > _o.T3Sec * 1000L) { _testSentTs = now; await SendRaw(Iec104Codec.U(U_TestFrAct), ct); }
                if (_o.GiIntervalSec > 0 && now - _lastGiTs > _o.GiIntervalSec * 1000L) await GeneralInterrogation(ct);
                if (_o.CounterIntervalSec > 0 && now - _lastCiTs > _o.CounterIntervalSec * 1000L) await CounterInterrogation(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Close(ex.Message); }
    }

    public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_open) throw new IOException(LastError ?? "104 连接已断开");
        var values = new List<RawTagValue>(plan.Tags.Count);
        foreach (var tag in plan.Tags)
        {
            if (!Iec104Address.TryParse(tag.NodeId, out var a, out var error)) { values.Add(new(tag.Id, 0, TagQuality.Bad, null, error)); continue; }
            if (!_points.TryGetValue((a.Monitor, a.Ioa), out var p))
            {
                values.Add(new(tag.Id, 0, TagQuality.Bad, null, _giComplete ? "子站未上送该信息对象（检查类型与信息对象地址）" : "等待总召唤完成"));
                continue;
            }
            var value = p.Value; var quality = p.Quality; var err = p.Error;
            if (a.Monitor == Iec104Monitor.DP && tag.DataType == TagDataType.Bool)
            {
                if (p.RawDpi is 0 or 3) { quality = TagQuality.Bad; err = p.RawDpi == 0 ? "双点处于中间状态（DPI=0）" : "双点状态不确定（DPI=3）"; value = 0; }
                else value = p.RawDpi == 2 ? 1 : 0;
            }
            values.Add(new(tag.Id, value, quality, p.Ts, err));
        }
        return Task.FromResult<IReadOnlyList<RawTagValue>>(values);
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!Iec104Address.TryParse(tag.NodeId, out var a, out var error)) throw new InvalidOperationException(error);
        if (a.Command is not Iec104Command command || a.CommandIoa is not int ioa) throw new InvalidOperationException("测点未配置命令");
        if (!_open) throw new IOException("104 连接已断开，命令未下发");
        var (type, element) = Encode(command, value);
        var sbo = a.SelectBeforeOperate ?? _o.CommandMode == Iec104CommandMode.SelectExecute;
        await _commands.WaitAsync(cancellationToken);
        var waiter = new Channel();
        if (!_waiters.TryAdd((type, ioa), waiter)) { _commands.Release(); throw new InvalidOperationException("该信息对象已有命令在执行"); }
        try
        {
            if (sbo)
            {
                await SendAsdu(type, CotAct, ioa, Select(element, true), cancellationToken);
                var selected = await Await(waiter, CotActCon, device.TimeoutMs, cancellationToken);
                if (selected is null)
                {
                    try { await SendAsdu(type, CotDeact, ioa, Select(element, true), cancellationToken); } catch { }
                    throw new IOException("选择（SELECT）未在超时内确认，已发送撤销，命令未执行");
                }
                if (selected.Negative) throw new InvalidOperationException("子站否定确认选择（SELECT），命令未执行");
            }
            await SendAsdu(type, CotAct, ioa, Select(element, false), cancellationToken);
            var confirmed = await Await(waiter, CotActCon, device.TimeoutMs, cancellationToken);
            if (confirmed is null) throw new TimeoutException("执行命令已发送，但未在超时内收到激活确认（ACTCON），结果未知");
            if (confirmed.Negative) throw new InvalidOperationException("子站否定确认执行命令（ACTCON-），命令未执行");
            if (_o.WaitActTerm && await Await(waiter, CotActTerm, device.TimeoutMs, cancellationToken) is null)
                throw new IOException("子站已确认执行（ACTCON），但未在超时内收到激活终止（ACTTERM），结果未知");
            LastError = null;
        }
        finally { _waiters.TryRemove((type, ioa), out _); _commands.Release(); }
    }

    private static async Task<Confirmation?> Await(Channel waiter, byte cause, int timeoutMs, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
        try
        {
            while (true)
            {
                var c = await waiter.Queue.Reader.ReadAsync(deadline.Token);
                if (c.Cause == cause) return c;
                // 未知原因/类型/地址等否定应答（COT 44～47）同样结束等待
                if (c.Negative && c.Cause >= 44) return c;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (System.Threading.Channels.ChannelClosedException) { throw new IOException("104 连接在等待命令确认时中断，结果未知"); }
    }

    private static byte[] Select(byte[] element, bool select)
    {
        var copy = (byte[])element.Clone();
        // SCO/DCO 与 QOS 的 S/E 位都在最后一个字节的 bit7
        if (select) copy[^1] |= 0x80; else copy[^1] &= 0x7F;
        return copy;
    }

    public static (byte Type, byte[] Element) Encode(Iec104Command command, double value)
    {
        switch (command)
        {
            case Iec104Command.SC: return (C_SC_NA, [(byte)(value != 0 ? 1 : 0)]);
            case Iec104Command.DC: return (C_DC_NA, [(byte)(value != 0 ? 2 : 1)]);
            case Iec104Command.SE_NA:
            {
                if (value < -1 || value > 32767.0 / 32768) throw new InvalidOperationException("归一化设定值须在 -1～0.99997 之间");
                var e = new byte[3]; BinaryPrimitives.WriteInt16LittleEndian(e, (short)Math.Round(value * 32768)); return (C_SE_NA, e);
            }
            case Iec104Command.SE_NB:
            {
                var e = new byte[3]; BinaryPrimitives.WriteInt16LittleEndian(e, checked((short)Math.Round(value))); return (C_SE_NB, e);
            }
            case Iec104Command.SE_NC:
            {
                var e = new byte[5]; BinaryPrimitives.WriteSingleLittleEndian(e, (float)value); return (C_SE_NC, e);
            }
        }
        throw new InvalidOperationException("不支持的命令类型");
    }

    private void Close(string reason)
    {
        if (!_open && _tcp is null) return;
        _open = false; LastError = reason;
        try { _life?.Cancel(); } catch { }
        _stream?.Dispose(); _tcp?.Dispose(); _stream = null; _tcp = null;
        _startDt?.TrySetException(new IOException(reason));
        foreach (var w in _waiters.Values) w.Queue.Writer.TryComplete();
    }

    public void Disconnect() => Close("已断开");
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
