using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Mqtt;

/// <summary>
/// 通用 MQTT 驱动：按测点地址订阅状态主题，缓存最近一条消息；读取时按 JSON 路径解码。
/// 写入向写入主题发布（QoS 按设备配置），默认等待状态主题回报一致值后才算成功，否则报告结果未知。
/// </summary>
public sealed class MqttDriver(IDeviceCredentials credentials) : IDeviceDriver
{
    private sealed record Last(byte[] Payload, long ReceivedTs);
    private MqttSession? _session;
    private MqttSession? _connecting;
    private MqttDeviceOptions _options = new();
    private readonly Dictionary<string, Last> _last = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly UpdateSignal _updated = new();
    public bool IsConnected => _session?.IsConnected == true;
    public string? LastError { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        _options = ProtocolCatalog.Options<MqttDeviceOptions>(device);
        var session = new MqttSession();
        _connecting = session;
        // 旧连接在异步释放期间的迟到消息不得写入新连接的缓存。
        session.Message += (topic, payload) => { if (!ReferenceEquals(Volatile.Read(ref _connecting), session)) return; lock (_gate) _last[topic] = new(payload, ScadaTime.NowMs()); _updated.Pulse(); };
        session.Disconnected += reason => LastError = $"MQTT 连接断开：{reason}";
        try
        {
            await session.ConnectAsync(device, new(_options.ClientId, _options.Version, _options.UseTls, _options.AllowUntrustedCertificate, _options.KeepAliveSec, _options.CleanSession), credentials, cancellationToken);
            _session = session; LastError = null;
        }
        catch (Exception ex) { await session.DisposeAsync(); LastError = ex.Message; throw; }
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new IOException("MQTT 尚未连接");
        if (!session.IsConnected) throw new IOException(LastError ?? "MQTT 连接已断开");
        var addresses = plan.Tags.Select(t => (Tag: t, Ok: MqttAddress.TryParse(t.NodeId, out var a, out var e), Address: a, Error: e)).ToArray();
        var topics = addresses.Where(x => x.Ok).Select(x => x.Address.Topic).ToArray();
        var added = await session.EnsureSubscribedAsync(topics, _options.Qos, device.TimeoutMs, cancellationToken);
        // 新订阅后短暂等待保留消息到达，避免首轮读取全部为“尚未收到消息”。
        if (added > 0) await _updated.WaitUntilAsync(() => { lock (_gate) return topics.All(_last.ContainsKey); }, Math.Min(device.TimeoutMs, 1000), cancellationToken);
        var now = ScadaTime.NowMs();
        var values = new List<RawTagValue>(addresses.Length);
        foreach (var (tag, ok, address, error) in addresses)
        {
            if (!ok) { values.Add(new(tag.Id, 0, TagQuality.Bad, null, error)); continue; }
            Last? last; lock (_gate) _last.TryGetValue(address.Topic, out last);
            if (last is null) { values.Add(new(tag.Id, 0, TagQuality.Bad, null, "尚未收到该主题的消息")); continue; }
            if (!MqttPayload.TryValue(last.Payload, address.Path, out var value, out var decodeError)) { values.Add(new(tag.Id, 0, TagQuality.Bad, last.ReceivedTs, decodeError)); continue; }
            var sourceTs = MqttPayload.Timestamp(last.Payload, address.TimestampPath) ?? last.ReceivedTs;
            var stale = _options.StaleAfterMs > 0 && now - last.ReceivedTs > _options.StaleAfterMs;
            values.Add(new(tag.Id, value, stale ? TagQuality.Uncertain : TagQuality.Good, sourceTs, stale ? $"超过 {_options.StaleAfterMs} ms 未收到新消息" : null));
        }
        LastError = values.FirstOrDefault(x => x.Error is not null)?.Error;
        return values;
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new IOException("MQTT 尚未连接");
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!MqttAddress.TryParse(tag.NodeId, out var address, out var error)) throw new InvalidOperationException(error);
        if (address.WriteTopic is null) throw new InvalidOperationException("测点未配置写入主题");
        await session.EnsureSubscribedAsync([address.Topic], _options.Qos, device.TimeoutMs, cancellationToken);
        var startTs = ScadaTime.NowMs();
        await session.PublishAsync(address.WriteTopic, MqttPayload.Encode(value, tag.DataType, address.WritePath), _options.Qos, _options.RetainWrites, device.TimeoutMs, cancellationToken);
        if (!_options.ConfirmWrites) { LastError = null; return; }
        var confirmed = await _updated.WaitUntilAsync(() =>
        {
            lock (_gate)
                return _last.TryGetValue(address.Topic, out var last) && last.ReceivedTs >= startTs
                    && MqttPayload.TryValue(last.Payload, address.Path, out var v, out _) && MqttPayload.SameValue(v, value);
        }, device.TimeoutMs, cancellationToken);
        if (!confirmed) throw new IOException($"已发布到 {address.WriteTopic}，但 {device.TimeoutMs} ms 内状态主题未回报一致值，结果未知");
        LastError = null;
    }

    public void Disconnect()
    {
        var session = _session; _session = null; Volatile.Write(ref _connecting, null);
        lock (_gate) _last.Clear();
        if (session is not null) _ = session.DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync() { var s = _session; _session = null; if (s is not null) await s.DisposeAsync(); }
}
