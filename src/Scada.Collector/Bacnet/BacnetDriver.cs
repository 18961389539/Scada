using System.Collections.Concurrent;
using System.IO.BACnet;
using System.Net;
using System.Net.Sockets;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Bacnet;

/// <summary>
/// BACnet/IP 客户端（基于 MIT 许可的 BACnet 4.0.0 库）：单播 Who-Is/I-Am 发现，ReadPropertyMultiple（不支持时回退 ReadProperty）
/// 读取 Present_Value 与 Status_Flags，带优先级的 WriteProperty，可选 SubscribeCOV（非确认通知）。
/// </summary>
public sealed class BacnetDriver : IDeviceDriver
{
    private sealed record Sample(double Value, TagQuality Quality, string? Error, long Ts);
    private sealed class CovState { public long RenewAtTick; public bool Unsupported; }

    private BacnetDeviceOptions _o = new();
    private BacnetClient? _client;
    private System.IO.BACnet.BacnetAddress? _peer;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly ConcurrentDictionary<BacnetObjectId, Sample> _cov = new();
    private readonly Dictionary<BacnetObjectId, CovState> _subscriptions = new();
    private volatile bool _connected;
    private bool _rpmUnsupported;
    private uint _subscriberId;
    private long _covNotifications;

    public bool IsConnected => _connected;
    public string? LastError { get; private set; }
    public uint? PeerMaxApdu { get; private set; }
    public long CovNotifications => Interlocked.Read(ref _covNotifications);
    public bool ReadMultipleActive => _o.ReadMultiple && !_rpmUnsupported;

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        _o = ProtocolCatalog.Options<BacnetDeviceOptions>(device);
        _rpmUnsupported = false; _cov.Clear(); _subscriptions.Clear();
        _subscriberId = (uint)Random.Shared.Next(1, int.MaxValue);
        try
        {
            var ip = IPAddress.TryParse(device.Host, out var parsed) ? parsed
                : (await Dns.GetHostAddressesAsync(device.Host, cancellationToken)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                  ?? throw new IOException($"无法解析主机 {device.Host} 的 IPv4 地址");
            var transport = new BacnetIpUdpProtocolTransport(_o.LocalPort, true);
            var client = new BacnetClient(transport, device.TimeoutMs, _o.ReadRetries);
            var iam = new TaskCompletionSource<(System.IO.BACnet.BacnetAddress, uint)>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnIam += (sender, adr, id, maxApdu, _, _) => { if (id == (uint)_o.DeviceInstance) iam.TrySetResult((adr, maxApdu)); };
            client.OnCOVNotification += OnCov;
            try { client.Start(); }
            catch (SocketException ex) { client.Dispose(); throw new IOException($"无法绑定本机 UDP 端口 {_o.LocalPort}：{ex.Message}", ex); }
            _client = client;
            _peer = new System.IO.BACnet.BacnetAddress(BacnetAddressTypes.IP, $"{ip}:{device.Port}");
            var discovered = false;
            if (_o.WhoIs)
            {
                client.WhoIs(_o.DeviceInstance, _o.DeviceInstance, _peer);
                try
                {
                    var (adr, maxApdu) = await iam.Task.WaitAsync(TimeSpan.FromMilliseconds(device.TimeoutMs), cancellationToken);
                    _peer = adr; PeerMaxApdu = maxApdu; discovered = true;
                }
                catch (TimeoutException) { /* 部分设备只广播 I-Am：回退为直接读取设备对象 */ }
            }
            if (!discovered)
            {
                try { await client.ReadPropertyAsync(_peer, new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, (uint)_o.DeviceInstance), BacnetPropertyIds.PROP_OBJECT_IDENTIFIER, cancellationToken: cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException(IsDeviceResponse(ex)
                        ? $"设备应答但没有实例号为 {_o.DeviceInstance} 的设备对象：{ex.Message}"
                        : $"BACnet 设备 {ip}:{device.Port}（实例 {_o.DeviceInstance}）无应答：未收到 I-Am，读取设备对象超时", ex);
                }
            }
            _connected = true; LastError = null;
        }
        catch (Exception ex) { LastError = ex.Message; Disconnect(); LastError = ex.Message; throw; }
    }

    private void OnCov(BacnetClient sender, System.IO.BACnet.BacnetAddress adr, byte invokeId, uint subscriberId, BacnetObjectId initiating, BacnetObjectId monitored,
        uint timeRemaining, bool needConfirm, ICollection<BacnetPropertyValue> values, BacnetMaxSegments maxSegments)
    {
        if (!ReferenceEquals(sender, _client) || subscriberId != _subscriberId) return;
        if (needConfirm) sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_COV_NOTIFICATION, invokeId);
        Interlocked.Increment(ref _covNotifications);
        IList<BacnetValue>? pv = null, flags = null;
        foreach (var v in values)
        {
            if (v.property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE) pv = v.value;
            else if (v.property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_STATUS_FLAGS) flags = v.value;
        }
        if (pv is null) return;
        _cov[monitored] = ToSample(pv, flags, ScadaTime.NowMs());
    }

    public static BacnetObjectTypes ObjectType(BacnetObjectKind kind) => kind switch
    {
        BacnetObjectKind.AI => BacnetObjectTypes.OBJECT_ANALOG_INPUT,
        BacnetObjectKind.AO => BacnetObjectTypes.OBJECT_ANALOG_OUTPUT,
        BacnetObjectKind.AV => BacnetObjectTypes.OBJECT_ANALOG_VALUE,
        BacnetObjectKind.BI => BacnetObjectTypes.OBJECT_BINARY_INPUT,
        BacnetObjectKind.BO => BacnetObjectTypes.OBJECT_BINARY_OUTPUT,
        BacnetObjectKind.BV => BacnetObjectTypes.OBJECT_BINARY_VALUE,
        _ => BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE,
    };

    private static BacnetObjectId Id(BacnetPointAddress a) => new(ObjectType(a.Kind), a.Instance);

    /// <summary>Present_Value + Status_Flags → 数值/品质。FAULT 为 Bad，OUT_OF_SERVICE/OVERRIDDEN 为 Uncertain。</summary>
    public static (double Value, TagQuality Quality, string? Error) Map(IList<BacnetValue>? pv, IList<BacnetValue>? flags)
    {
        if (pv is null || pv.Count == 0) return (0, TagQuality.Bad, "设备未返回 Present_Value");
        var v = pv[0];
        if (v.Value is BacnetError e) return (0, TagQuality.Bad, $"设备错误：{e.error_class}/{e.error_code}");
        double? number = v.Value switch
        {
            float f => f, double d => d, uint u => u, int i => i, bool b => b ? 1 : 0, ulong ul => ul, long l => l, byte by => by, ushort us => us, short s => s,
            _ => null
        };
        if (number is null || !double.IsFinite(number.Value)) return (0, TagQuality.Bad, $"Present_Value 类型不受支持（{v.Tag}）");
        if (flags is { Count: > 0 } && flags[0].Value is BacnetBitString bits)
        {
            if (bits.bits_used > 1 && bits.GetBit(1)) return (number.Value, TagQuality.Bad, "对象故障（FAULT）");
            if (bits.bits_used > 3 && bits.GetBit(3)) return (number.Value, TagQuality.Uncertain, "对象停用（OUT_OF_SERVICE）");
            if (bits.bits_used > 2 && bits.GetBit(2)) return (number.Value, TagQuality.Uncertain, "本地超驰（OVERRIDDEN）");
        }
        return (number.Value, TagQuality.Good, null);
    }

    private static Sample ToSample(IList<BacnetValue>? pv, IList<BacnetValue>? flags, long ts)
    {
        var (value, quality, error) = Map(pv, flags);
        return new(value, quality, error, ts);
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_connected || _client is not { } client || _peer is not { } peer) throw new IOException(LastError ?? "BACnet 未连接");
        var result = new List<RawTagValue>(plan.Tags.Count);
        var byObject = new Dictionary<BacnetObjectId, List<Tag>>();
        foreach (var tag in plan.Tags)
        {
            if (!BacnetPointAddress.TryParse(tag.NodeId, out var a, out var error)) { result.Add(new(tag.Id, 0, TagQuality.Bad, null, error)); continue; }
            var id = Id(a);
            if (!byObject.TryGetValue(id, out var list)) byObject[id] = list = [];
            list.Add(tag);
        }
        var samples = new Dictionary<BacnetObjectId, Sample>();
        await _io.WaitAsync(cancellationToken);
        try
        {
            client.Retries = _o.ReadRetries;
            if (_o.UseCov) await RenewSubscriptions(client, peer, byObject.Keys, cancellationToken);
            var poll = new List<BacnetObjectId>();
            foreach (var id in byObject.Keys)
            {
                if (_o.UseCov && _cov.TryGetValue(id, out var s) && _subscriptions.TryGetValue(id, out var sub) && !sub.Unsupported) samples[id] = s;
                else poll.Add(id);
            }
            var failures = 0; var attempts = 0; string? lastFailure = null;
            foreach (var chunk in poll.Chunk(ReadMultipleActive ? _o.MaxObjectsPerRequest : 1))
            {
                attempts++;
                try { await ReadChunk(client, peer, chunk, samples, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++; lastFailure = ex.Message;
                    foreach (var id in chunk) samples[id] = new(0, TagQuality.Bad, $"读取失败：{ex.Message}", ScadaTime.NowMs());
                }
            }
            if (attempts > 0 && failures == attempts && samples.Values.All(s => s.Quality == TagQuality.Bad && s.Error?.StartsWith("读取失败") == true))
            {
                Close($"BACnet 设备无应答：{lastFailure}");
                throw new IOException(LastError);
            }
        }
        finally { _io.Release(); }
        foreach (var (id, tags) in byObject)
        foreach (var tag in tags)
        {
            var s = samples[id];
            result.Add(new(tag.Id, tag.DataType == TagDataType.Bool ? (s.Value != 0 ? 1 : 0) : s.Value, s.Quality, null, s.Error));
        }
        LastError = null;
        return result;
    }

    private async Task ReadChunk(BacnetClient client, System.IO.BACnet.BacnetAddress peer, BacnetObjectId[] chunk, Dictionary<BacnetObjectId, Sample> samples, CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        if (ReadMultipleActive)
        {
            var specs = chunk.Select(id => new BacnetReadAccessSpecification(id, new List<BacnetPropertyReference>
            {
                new(BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue), new(BacnetPropertyIds.PROP_STATUS_FLAGS, uint.MaxValue),
            })).ToList();
            try
            {
                var results = await client.ReadPropertyMultipleAsync(peer, specs, cancellationToken: ct);
                foreach (var r in results)
                {
                    var pv = r.values.FirstOrDefault(v => v.property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE).value;
                    var flags = r.values.FirstOrDefault(v => v.property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_STATUS_FLAGS).value;
                    samples[r.objectIdentifier] = ToSample(pv, flags, now);
                }
                foreach (var id in chunk) samples.TryAdd(id, new(0, TagQuality.Bad, "设备应答中缺少该对象", now));
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && IsDeviceResponse(ex))
            {
                // 设备不支持 RPM：之后改为逐个 ReadProperty；整包错误（如某个对象不存在）：本次逐个读取以定位到对象
                if (IsUnsupportedService(ex)) _rpmUnsupported = true;
            }
        }
        foreach (var id in chunk)
        {
            try
            {
                var pv = await client.ReadPropertyAsync(peer, id, BacnetPropertyIds.PROP_PRESENT_VALUE, cancellationToken: ct);
                IList<BacnetValue>? flags = null;
                try { flags = await client.ReadPropertyAsync(peer, id, BacnetPropertyIds.PROP_STATUS_FLAGS, cancellationToken: ct); }
                catch (Exception ex) when (ex is not OperationCanceledException && IsDeviceResponse(ex)) { }
                samples[id] = ToSample(pv, flags, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && IsDeviceResponse(ex))
            {
                samples[id] = new(0, TagQuality.Bad, $"设备错误：{ex.Message}", now);
            }
        }
    }

    private async Task RenewSubscriptions(BacnetClient client, System.IO.BACnet.BacnetAddress peer, IEnumerable<BacnetObjectId> ids, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        foreach (var id in ids)
        {
            if (!_subscriptions.TryGetValue(id, out var state)) _subscriptions[id] = state = new();
            if (state.Unsupported || now < state.RenewAtTick) continue;
            try
            {
                await client.SubscribeCOVAsync(peer, id, _subscriberId, false, false, (uint)_o.CovLifetimeSec, cancellationToken: ct);
                state.RenewAtTick = now + _o.CovLifetimeSec * 500L; // 在订阅时长过半时续订
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _cov.TryRemove(id, out _);
                if (IsDeviceResponse(ex)) state.Unsupported = true; // 对象不支持 COV：轮询
                else state.RenewAtTick = now + 10_000;          // 超时：先轮询，10 秒后重试
            }
        }
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!BacnetPointAddress.TryParse(tag.NodeId, out var a, out var error)) throw new InvalidOperationException(error);
        if (!a.IsCommandable) throw new InvalidOperationException("该对象类型不可写");
        if (!_connected || _client is not { } client || _peer is not { } peer) throw new IOException("BACnet 未连接，命令未下发");
        var encoded = Encode(a.Kind, value);
        var priority = (byte)(a.Priority ?? _o.WritePriority);
        var id = Id(a);
        await _io.WaitAsync(cancellationToken);
        try
        {
            client.Retries = 0; // 写命令不重发
            _cov.TryRemove(id, out _); // 回读走轮询，避免读到旧的 COV 缓存
            await client.WritePropertyAsync(peer, id, BacnetPropertyIds.PROP_PRESENT_VALUE, [encoded], priority: priority, cancellationToken: cancellationToken);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsDeviceResponse(ex)) throw new InvalidOperationException($"设备拒绝写入，命令未执行：{ex.Message}", ex);
            throw new TimeoutException($"写请求已发送但未收到确认，结果未知：{ex.Message}", ex);
        }
        finally { _io.Release(); }
    }

    public static BacnetValue Encode(BacnetObjectKind kind, double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("写入值无效");
        switch (kind)
        {
            case BacnetObjectKind.BO or BacnetObjectKind.BV or BacnetObjectKind.BI:
                return new(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, (uint)(value != 0 ? 1 : 0));
            case BacnetObjectKind.MSV:
                if (value < 1 || value > uint.MaxValue || value != Math.Floor(value)) throw new InvalidOperationException("多态值状态号须为不小于 1 的整数");
                return new(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, (uint)value);
            default:
                if (Math.Abs(value) > float.MaxValue) throw new InvalidOperationException("模拟量超出 REAL 范围");
                return new(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, (float)value);
        }
    }

    // 库对设备 Error/Reject/Abort 应答与超时均抛出 System.Exception，只能按消息区分。
    private static bool IsDeviceResponse(Exception ex) =>
        ex.Message.StartsWith("Error from device", StringComparison.Ordinal) || ex.Message.StartsWith("Reject", StringComparison.Ordinal) || ex.Message.StartsWith("Abort", StringComparison.Ordinal);
    private static bool IsUnsupportedService(Exception ex) =>
        ex.Message.StartsWith("Reject", StringComparison.Ordinal) || ex.Message.StartsWith("Abort", StringComparison.Ordinal) || ex.Message.Contains("SERVICE_REQUEST_DENIED") || ex.Message.Contains("UNRECOGNIZED_SERVICE");

    private void Close(string reason)
    {
        _connected = false; LastError = reason;
        var client = _client; _client = null;
        if (client is not null) { client.OnCOVNotification -= OnCov; try { client.Dispose(); } catch { } }
    }

    public void Disconnect() => Close("已断开");
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
