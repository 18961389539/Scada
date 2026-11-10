using System.Collections.Concurrent;
using System.Threading.Channels;
using Scada.Protocols.Iec104;
using Scada.Core.Enums;
using Scada.Core.Options;

namespace Scada.Server.External.Slave;

/// <summary>
/// 单个 IEC 60870-5-104 从站会话。协议状态机在这里闭环：
/// STARTDT/STOPDT/TESTFR、k 窗口与 w 确认、t1/t2/t3 定时器、总召唤、对时、单点/双点遥控、变化上送。
/// 所有 I 帧只由发送循环写出，读循环只入队——序号递增必须单线程，否则总召唤与变化上送会撞序号。
/// </summary>
internal sealed class Iec104ServerSession : IAsyncDisposable
{
    private const int SeqModulo = 32768;
    // 单帧信息对象上限：M_ME_TF 每个 12 字节信息元素 + 3 字节 IOA，ASDU 上限 249 字节，取保守值。
    private const int MaxObjectsPerFrame = 12;

    private readonly Stream _stream;
    private readonly Iec104ServerOptions _options;
    private readonly ExportPointMap _map;
    private readonly ExportWriteRouter _router;
    private readonly ILogger _logger;
    private readonly string _remoteIp;
    private readonly int _commonAddress;
    private readonly long _t1Ms, _t2Ms, _t3Ms;

    private readonly Channel<Asdu> _outbox = Channel.CreateBounded<Asdu>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    });
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // 总召唤在读循环里写基线，变化扫描在另一个循环里写，二者必须并发安全。
    private readonly ConcurrentDictionary<int, Iec104SampleState> _lastSample = new();
    private readonly int[] _commonAddresses;

    private readonly object _seqLock = new();
    private int _sendSeq, _recvSeq, _ackSeq, _outstanding, _recvUnacked;
    private long _lastSend, _lastRecv;
    private volatile bool _transferEnabled;
    private volatile bool _testPending;
    private volatile bool _baselineDone;
    private CancellationTokenSource? _cts;

    public Iec104ServerSession(Stream stream, Iec104ServerOptions options, ExportPointMap map, ExportWriteRouter router,
        ILogger logger, string remoteIp, int commonAddress)
    {
        _stream = stream;
        _options = options;
        _map = map;
        _router = router;
        _logger = logger;
        _remoteIp = remoteIp;
        _commonAddress = commonAddress;
        _commonAddresses = options.CommonAddresses is { Length: > 0 } ? options.CommonAddresses : [commonAddress];
        _t1Ms = Math.Clamp(options.T1Sec, 1, 255) * 1000L;
        _t2Ms = Math.Clamp(options.T2Sec, 1, 255) * 1000L;
        _t3Ms = Math.Clamp(options.T3Sec, 1, 255) * 1000L;
    }

    public string RemoteIp => _remoteIp;
    public long FramesReceived => _framesReceived;
    private long _framesReceived;

    public async Task RunAsync(CancellationToken hostCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        _cts = linked;
        _lastRecv = _lastSend = Environment.TickCount64;
        var token = linked.Token;
        var tasks = new[]
        {
            ReadLoopAsync(token),
            SendLoopAsync(token),
            TimerLoopAsync(token),
            ChangeLoopAsync(token)
        };
        try
        {
            await Task.WhenAny(tasks);
        }
        finally
        {
            _outbox.Writer.TryComplete();
            try { linked.Cancel(); } catch { }
            try { await Task.WhenAll(tasks); } catch { }
        }
    }

    // ---- 读 ----

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var header = new byte[2];
        while (!ct.IsCancellationRequested)
        {
            await _stream.ReadExactlyAsync(header, ct);
            if (header[0] != 0x68) throw new IOException($"104 帧起始字节无效：0x{header[0]:X2}");
            var apduLength = header[1];
            if (apduLength < 4) throw new IOException($"104 APDU 长度无效：{apduLength}");
            var frame = new byte[2 + apduLength];
            header.CopyTo(frame, 0);
            await _stream.ReadExactlyAsync(frame.AsMemory(2, apduLength), ct);
            Volatile.Write(ref _lastRecv, Environment.TickCount64);
            Interlocked.Increment(ref _framesReceived);

            var apdu = Iec104Codec.Decode(frame);
            switch (apdu.Kind)
            {
                case ApciKind.I:
                    Acknowledge(apdu.RecvSeq);
                    _recvSeq = (_recvSeq + 1) % SeqModulo;
                    Interlocked.Increment(ref _recvUnacked);
                    if (Volatile.Read(ref _recvUnacked) >= Math.Clamp(_options.W, 1, 32767))
                        await SendSFrameAsync(ct);
                    if (apdu.Asdu is { } asdu) await HandleAsduAsync(asdu, ct);
                    break;
                case ApciKind.S:
                    Acknowledge(apdu.RecvSeq);
                    break;
                case ApciKind.U:
                    await HandleUFrameAsync(apdu.UFunction, ct);
                    break;
            }
        }
    }

    private async Task HandleUFrameAsync(byte function, CancellationToken ct)
    {
        switch (function)
        {
            case Iec104Types.U_StartDtAct:
                _transferEnabled = true;
                _baselineDone = false;
                await SendRawAsync(Iec104Codec.U(Iec104Types.U_StartDtCon), ct);
                _logger.LogInformation("IEC 104 从站数据传输已启动：{Ip}", _remoteIp);
                break;
            case Iec104Types.U_StopDtAct:
                _transferEnabled = false;
                await SendRawAsync(Iec104Codec.U(Iec104Types.U_StopDtCon), ct);
                _logger.LogInformation("IEC 104 从站数据传输已停止：{Ip}", _remoteIp);
                break;
            case Iec104Types.U_TestFrAct:
                await SendRawAsync(Iec104Codec.U(Iec104Types.U_TestFrCon), ct);
                break;
            case Iec104Types.U_TestFrCon:
                _testPending = false;
                break;
        }
    }

    private void Acknowledge(int recvSeq)
    {
        lock (_seqLock)
        {
            if (recvSeq == _ackSeq) return;
            var acked = (recvSeq - _ackSeq + SeqModulo) % SeqModulo;
            // 异常报文可能给出超过已发帧数的确认，按已发上限截断，避免序号状态被一次坏包带偏。
            if (acked > _outstanding) acked = _outstanding;
            _ackSeq = recvSeq;
            _outstanding -= acked;
        }
        _testPending = false;
    }

    private int Outstanding
    {
        get { lock (_seqLock) return _outstanding; }
    }

    // ---- 发送 ----

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var asdu in _outbox.Reader.ReadAllAsync(ct))
        {
            while (Outstanding >= Math.Clamp(_options.K, 1, 32767))
                await Task.Delay(10, ct);
            var frame = Iec104Codec.I(_sendSeq, Volatile.Read(ref _recvSeq), asdu);
            await SendRawAsync(frame, ct);
            _sendSeq = (_sendSeq + 1) % SeqModulo;
            lock (_seqLock) _outstanding++;
        }
    }

    private Task SendSFrameAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _recvUnacked, 0);
        return SendRawAsync(Iec104Codec.S(Volatile.Read(ref _recvSeq)), ct);
    }

    private async Task SendRawAsync(byte[] frame, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
            Volatile.Write(ref _lastSend, Environment.TickCount64);
        }
        finally { _writeGate.Release(); }
    }

    // ---- 定时器 ----

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct);
            var now = Environment.TickCount64;
            if (Volatile.Read(ref _recvUnacked) > 0 && now - Volatile.Read(ref _lastSend) >= _t2Ms)
                await SendSFrameAsync(ct);
            if (_transferEnabled && !_testPending && now - Volatile.Read(ref _lastSend) >= _t3Ms)
            {
                _testPending = true;
                await SendRawAsync(Iec104Codec.U(Iec104Types.U_TestFrAct), ct);
            }
            // t1 只在"有未确认帧"时生效：空闲链路由 t3 维持，不应该被 t1 误判断开。
            if ((Outstanding > 0 || _testPending) && now - Volatile.Read(ref _lastRecv) >= _t1Ms)
            {
                _logger.LogWarning("IEC 104 从站 t1 超时，断开 {Ip}", _remoteIp);
                _cts?.Cancel();
                return;
            }
        }
    }

    // ---- 变化上送 ----

    private async Task ChangeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Math.Clamp(_options.ChangeScanMs, 50, 5000), ct);
            if (!_transferEnabled || _map.Iec.Count == 0) continue;

            // 首轮只建立基线不发报文：主站连上后应先发总召唤取全量，否则会收到一屏"变化"。
            if (!_baselineDone)
            {
                foreach (var (ioa, slot) in _map.Iec.Points)
                {
                    var sample = _map.ReadIec(slot);
                    _lastSample[ioa] = new Iec104SampleState(sample.Value, sample.Quality);
                }
                _baselineDone = true;
                continue;
            }

            foreach (var (ioa, slot) in _map.Iec.Points)
            {
                var sample = _map.ReadIec(slot);
                var state = new Iec104SampleState(sample.Value, sample.Quality);
                if (_lastSample.TryGetValue(ioa, out var previous) && !state.Differs(previous)) continue;
                _lastSample[ioa] = state;
                var (typeId, element) = Iec104ServerCodec.Encode(slot, sample, _options.UseTimeTag);
                await _outbox.Writer.WriteAsync(new Asdu(typeId, false, Iec104Types.CotSpont, false, false, 0,
                    _commonAddress, [new InfoObject(ioa, element)]), ct);
            }
        }
    }

    // ---- ASDU ----

    private async Task HandleAsduAsync(Asdu asdu, CancellationToken ct)
    {
        if (!_commonAddresses.Contains(asdu.CommonAddress)) return;
        switch (asdu.TypeId)
        {
            case Iec104Types.C_IC_NA when asdu.Cause == Iec104Types.CotAct:
                await InterrogationAsync(asdu, ct);
                break;
            case Iec104Types.C_CS_NA when asdu.Cause == Iec104Types.CotAct:
                await ClockSyncAsync(asdu, ct);
                break;
            case Iec104Types.C_SC_NA when asdu.Cause == Iec104Types.CotAct:
                await SingleCommandAsync(asdu, ct);
                break;
            case Iec104Types.C_DC_NA when asdu.Cause == Iec104Types.CotAct:
                await DoubleCommandAsync(asdu, ct);
                break;
        }
    }

    private async Task InterrogationAsync(Asdu request, CancellationToken ct)
    {
        var qoi = request.Objects.Count > 0 && request.Objects[0].Element.Length > 0 ? request.Objects[0].Element[0] : (byte)20;
        await EnqueueAsync(new Asdu(Iec104Types.C_IC_NA, false, Iec104Types.CotActCon, false, false, 0,
            request.CommonAddress, [new InfoObject(0, [qoi])]), ct);

        // 单点与遥测必须分成不同 ASDU：一个 ASDU 只能有一个类型标识。
        foreach (var group in _map.Iec.Points.Values.GroupBy(s => s.IsSinglePoint))
        {
            var buffer = new List<InfoObject>(MaxObjectsPerFrame);
            foreach (var slot in group.OrderBy(s => s.Ioa))
            {
                var sample = _map.ReadIec(slot);
                var (_, element) = Iec104ServerCodec.Encode(slot, sample, _options.UseTimeTag);
                buffer.Add(new InfoObject(slot.Ioa, element));
                _lastSample[slot.Ioa] = new Iec104SampleState(sample.Value, sample.Quality);
                if (buffer.Count < MaxObjectsPerFrame) continue;
                await EnqueueAsync(BuildDataAsdu(group.Key, buffer, Iec104Types.CotInrogen, request.CommonAddress), ct);
                buffer = new List<InfoObject>(MaxObjectsPerFrame);
            }
            if (buffer.Count > 0) await EnqueueAsync(BuildDataAsdu(group.Key, buffer, Iec104Types.CotInrogen, request.CommonAddress), ct);
        }
        _baselineDone = true;

        await EnqueueAsync(new Asdu(Iec104Types.C_IC_NA, false, Iec104Types.CotActTerm, false, false, 0,
            request.CommonAddress, [new InfoObject(0, [qoi])]), ct);
    }

    private Asdu BuildDataAsdu(bool singlePoint, IReadOnlyList<InfoObject> objects, byte cause, int commonAddress)
    {
        var typeId = singlePoint
            ? _options.UseTimeTag ? Iec104Types.M_SP_TB : Iec104Types.M_SP_NA
            : _options.UseTimeTag ? Iec104Types.M_ME_TF : Iec104Types.M_ME_NC;
        return new Asdu(typeId, false, cause, false, false, 0, commonAddress, objects);
    }

    // 对时只回确认，不改系统时钟：服务器时间会影响历史库分区、报表班次与调度事件顺序，
    // 必须由运维显式决定，不能由一条网络报文触发。
    private Task ClockSyncAsync(Asdu request, CancellationToken ct) =>
        EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, false), ct).AsTask();

    private async Task SingleCommandAsync(Asdu request, CancellationToken ct)
    {
        if (!_options.AllowControl || request.Objects.Count == 0)
        {
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, true), ct);
            return;
        }
        var obj = request.Objects[0];
        if (obj.Element.Length < 1)
        {
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, true), ct);
            return;
        }
        var sco = obj.Element[0];
        var select = (sco & 0x80) != 0;
        var on = (sco & 0x01) != 0;
        if (select)
        {
            // 选择阶段只回确认；执行阶段才真正下发，避免"选择即动作"的误动。
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, false), ct);
            return;
        }
        await ExecuteCommandAsync(request, obj, on ? 1d : 0d, ct);
    }

    private async Task DoubleCommandAsync(Asdu request, CancellationToken ct)
    {
        if (!_options.AllowControl || request.Objects.Count == 0)
        {
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, true), ct);
            return;
        }
        var obj = request.Objects[0];
        if (obj.Element.Length < 1)
        {
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, true), ct);
            return;
        }
        var dco = obj.Element[0];
        if ((dco & 0x80) != 0)
        {
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, false), ct);
            return;
        }
        var state = (byte)(dco & 0x03);
        // DCS：1=ON，2=OFF，0=中间态，3=不确定。后两者不是有效命令，拒绝而不是猜一个。
        if (state is not (1 or 2))
        {
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, true), ct);
            return;
        }
        await ExecuteCommandAsync(request, obj, state == 1 ? 1d : 0d, ct);
    }

    private async Task ExecuteCommandAsync(Asdu request, InfoObject obj, double value, CancellationToken ct)
    {
        if (!_map.Iec.Points.TryGetValue(obj.Ioa, out var slot) || !slot.Writable)
        {
            await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, true), ct);
            return;
        }
        var result = await _router.WriteAsync(slot.TagId, value, "外部 IEC 104 主站", _remoteIp, ct);
        await EnqueueAsync(CommandResponse(request, Iec104Types.CotActCon, !result.Success), ct);
        await EnqueueAsync(CommandResponse(request, Iec104Types.CotActTerm, !result.Success), ct);
    }

    private static Asdu CommandResponse(Asdu request, byte cause, bool negative) =>
        new(request.TypeId, false, cause, negative, false, request.Originator, request.CommonAddress, request.Objects);

    private ValueTask EnqueueAsync(Asdu asdu, CancellationToken ct) => _outbox.Writer.WriteAsync(asdu, ct);

    public async ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        _writeGate.Dispose();
        await _stream.DisposeAsync();
    }
}
