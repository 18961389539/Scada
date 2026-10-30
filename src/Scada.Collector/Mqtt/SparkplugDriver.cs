using System.Text;
using System.Text.Json;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Mqtt;

/// <summary>
/// Sparkplug B 主机应用（消费端）驱动：订阅一个边缘节点（可选其下一个设备）的 BIRTH/DATA/DEATH，
/// 以出生证书建立 度量名↔别名/类型 映射，跟踪 seq 序号；缺口或无出生证书时请求 Rebirth；
/// NDEATH/DDEATH 使测点变为 Bad；写入发送 DCMD/NCMD，默认等待 DATA 回报一致值。
/// </summary>
public sealed class SparkplugDriver(IDeviceCredentials credentials) : IDeviceDriver
{
    private const string Ns = "spBv1.0";
    private sealed class MetricState
    {
        public required string Name;
        public ulong? Alias;
        public SparkplugDataType DataType;
        public double? Value;
        public bool IsNull;
        public long Ts;
        public long ReceivedTs;
    }

    private readonly object _gate = new();
    private readonly UpdateSignal _updated = new();
    private MqttSession? _session;
    private MqttSession? _connecting;
    private SparkplugDeviceOptions _o = new();
    private readonly Dictionary<string, MetricState> _metrics = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, MetricState> _byAlias = [];
    private bool _born;              // 目标范围（节点或设备）已收到出生证书且在线
    private string? _offlineReason = "尚未收到出生证书";
    private int? _lastSeq;
    private ulong? _bdSeq;
    private long _lastRebirthRequest;
    private bool _everBorn;           // 本次连接内是否见过出生证书；ReadAsync 据此避免 DEATH 后空等，但收到 NDATA/DDATA 仍会请求 Rebirth
    private bool _uncertain;          // 序号缺口后到重新出生前，值标记为 Uncertain
    public bool IsConnected => _session?.IsConnected == true;
    public string? LastError { get; private set; }
    public long SeqGaps { get; private set; }

    private string NodeTopic(string type) => $"{Ns}/{_o.GroupId}/{type}/{_o.EdgeNodeId}";
    private string DeviceTopic(string type) => $"{NodeTopic(type)}/{_o.DeviceId}";
    private bool DeviceScope => _o.DeviceId.Length > 0;

    public static byte[] StatePayload(bool online, long ts) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { online, timestamp = ts }));

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        _o = ProtocolCatalog.Options<SparkplugDeviceOptions>(device);
        lock (_gate) { _metrics.Clear(); _byAlias.Clear(); _born = false; _offlineReason = "尚未收到出生证书"; _lastSeq = null; _bdSeq = null; _uncertain = false; _everBorn = false; }
        var session = new MqttSession();
        Volatile.Write(ref _connecting, session);
        session.Message += (topic, data) => { if (ReferenceEquals(Volatile.Read(ref _connecting), session)) OnMessage(topic, data); };
        session.Disconnected += reason => LastError = $"MQTT 连接断开：{reason}";
        var stateTopic = $"{Ns}/STATE/{_o.PrimaryHostId}";
        try
        {
            await session.ConnectAsync(device, new(_o.ClientId, _o.Version, _o.UseTls, _o.AllowUntrustedCertificate, _o.KeepAliveSec, true,
                _o.PrimaryHostId.Length > 0 ? (stateTopic, StatePayload(false, ScadaTime.NowMs())) : null), credentials, cancellationToken);
            var topics = new List<string> { NodeTopic("NBIRTH"), NodeTopic("NDEATH"), NodeTopic("NDATA") };
            if (DeviceScope) topics.AddRange([DeviceTopic("DBIRTH"), DeviceTopic("DDEATH"), DeviceTopic("DDATA")]);
            await session.EnsureSubscribedAsync(topics, 1, device.TimeoutMs, cancellationToken);
            // Primary Host：先订阅再上线，边缘节点据此（重新）发出生证书。
            if (_o.PrimaryHostId.Length > 0) await session.PublishAsync(stateTopic, StatePayload(true, ScadaTime.NowMs()), 1, true, device.TimeoutMs, cancellationToken);
            _session = session; LastError = null;
        }
        catch (Exception ex) { await session.DisposeAsync(); LastError = ex.Message; throw; }
    }

    private void OnMessage(string topic, byte[] data)
    {
        var parts = topic.Split('/');
        if (parts.Length < 4 || parts[0] != Ns || parts[1] != _o.GroupId || parts[3] != _o.EdgeNodeId) return;
        var type = parts[2];
        var device = parts.Length > 4 ? parts[4] : null;
        SparkplugPayload payload;
        try { payload = SparkplugPayload.Decode(data); }
        catch (FormatException ex) { LastError = $"{topic} 载荷无法解码：{ex.Message}"; return; }
        var requestRebirth = false;
        lock (_gate)
        {
            if (type == "NDEATH")
            {
                var bd = payload.Metrics.FirstOrDefault(m => m.Name == "bdSeq")?.Value;
                // bdSeq 与当前出生不匹配的 NDEATH 来自旧会话（例如遗嘱延迟投递），忽略。
                if (_bdSeq is ulong current && bd is double d && (ulong)d != current) return;
                Offline("边缘节点离线（NDEATH）");
                _lastSeq = null;
            }
            else
            {
                // 除 NDEATH 外，节点发出的所有消息共享同一 seq 序列（0～255 循环）。
                if (payload.Seq is ulong seq)
                {
                    if (type == "NBIRTH") { _lastSeq = (int)seq; }
                    else if (_lastSeq is int last && (int)seq != (last + 1) % 256)
                    {
                        SeqGaps++; _uncertain = true; requestRebirth = true; _lastSeq = (int)seq;
                        LastError = $"Sparkplug 序号缺口：期望 {(last + 1) % 256}，收到 {seq}";
                    }
                    else _lastSeq = (int)seq;
                }
                var scoped = DeviceScope ? device == _o.DeviceId : device is null;
                if (type == "NBIRTH")
                {
                    _bdSeq = payload.Metrics.FirstOrDefault(m => m.Name == "bdSeq")?.Value is double b ? (ulong)b : null;
                    if (DeviceScope) { Offline("边缘节点重新出生，等待设备出生证书（DBIRTH）"); }
                    else Birth(payload);
                }
                else if (type == "DBIRTH" && scoped) Birth(payload);
                else if (type == "DDEATH" && scoped) Offline("设备离线（DDEATH）");
                else if (type is "NDATA" or "DDATA" && scoped)
                {
                    if (!_born) requestRebirth = true; // 收到数据却没有有效出生证书（漏收 BIRTH），按规范请求重新出生
                    else Data(payload);
                }
            }
        }
        _updated.Pulse();
        if (requestRebirth) _ = RequestRebirthAsync();
    }

    private void Birth(SparkplugPayload payload)
    {
        _metrics.Clear(); _byAlias.Clear();
        var now = ScadaTime.NowMs();
        foreach (var m in payload.Metrics.Where(m => m.Name is not null))
        {
            var state = new MetricState { Name = m.Name!, Alias = m.Alias, DataType = m.DataType, Value = m.Value, IsNull = m.IsNull, Ts = (long)(m.Timestamp ?? payload.Timestamp ?? (ulong)now), ReceivedTs = now };
            _metrics[state.Name] = state;
            if (m.Alias is ulong alias) _byAlias[alias] = state;
        }
        _born = true; _everBorn = true; _offlineReason = null; _uncertain = false; _lastRebirthRequest = 0; // 已出生，允许下一次缺口立即请求
    }

    private void Data(SparkplugPayload payload)
    {
        var now = ScadaTime.NowMs();
        foreach (var m in payload.Metrics.Where(m => !m.IsHistorical))
        {
            MetricState? state = null;
            if (m.Alias is ulong alias) _byAlias.TryGetValue(alias, out state);
            if (state is null && m.Name is not null) _metrics.TryGetValue(m.Name, out state);
            if (state is null) continue; // 出生证书中未声明的度量不接受
            state.Value = m.ValueAs(state.DataType); state.IsNull = m.IsNull; state.ReceivedTs = now;
            state.Ts = (long)(m.Timestamp ?? payload.Timestamp ?? (ulong)now);
        }
    }

    private void Offline(string reason) { _born = false; _offlineReason = reason; }

    private async Task RequestRebirthAsync()
    {
        var session = _session;
        if (!_o.RequestRebirth || session is null || !session.IsConnected) return;
        var now = Environment.TickCount64;
        lock (_gate) { if (now - _lastRebirthRequest < 10_000 && _lastRebirthRequest != 0) return; _lastRebirthRequest = now; }
        var payload = new SparkplugPayload { Timestamp = (ulong)ScadaTime.NowMs() };
        payload.Metrics.Add(new SparkplugMetric { Name = "Node Control/Rebirth", DataType = SparkplugDataType.Boolean, Value = 1 });
        try { await session.PublishAsync(NodeTopic("NCMD"), payload.Encode(), 0, false, 3000, CancellationToken.None); }
        catch (Exception ex) { LastError = $"请求 Rebirth 失败：{ex.Message}"; }
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new IOException("Sparkplug 尚未连接");
        if (!session.IsConnected) throw new IOException(LastError ?? "MQTT 连接已断开");
        bool born, everBorn;
        lock (_gate) { born = _born; everBorn = _everBorn; }
        if (!born && !everBorn)
        {
            _ = RequestRebirthAsync();
            await _updated.WaitUntilAsync(() => { lock (_gate) return _born; }, Math.Min(device.TimeoutMs, 1000), cancellationToken);
        }
        var values = new List<RawTagValue>(plan.Tags.Count);
        lock (_gate)
        {
            foreach (var tag in plan.Tags)
            {
                if (!_born) { values.Add(new(tag.Id, 0, TagQuality.CommFailure, null, _offlineReason)); continue; }
                if (!_metrics.TryGetValue(tag.NodeId, out var m)) { values.Add(new(tag.Id, 0, TagQuality.Bad, null, "出生证书中没有该度量")); continue; }
                if (m.IsNull || m.Value is not double v || !double.IsFinite(v)) { values.Add(new(tag.Id, 0, TagQuality.Bad, m.Ts, m.IsNull ? "度量值为空（is_null）" : $"度量类型 {m.DataType} 不能转换为数值")); continue; }
                values.Add(new(tag.Id, v, _uncertain ? TagQuality.Uncertain : TagQuality.Good, m.Ts, _uncertain ? "序号缺口，等待重新出生" : null));
            }
        }
        if (values.FirstOrDefault(x => x.Error is not null)?.Error is string e) LastError = e;
        else LastError = null;
        return values;
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new IOException("Sparkplug 尚未连接");
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        MetricState metric; long start;
        lock (_gate)
        {
            if (!_born) throw new IOException($"不能写入：{_offlineReason}");
            if (!_metrics.TryGetValue(tag.NodeId, out var found)) throw new IOException("出生证书中没有该度量，不能写入");
            if (found.DataType is SparkplugDataType.String or SparkplugDataType.Text or SparkplugDataType.Unknown or SparkplugDataType.DateTime)
                throw new InvalidOperationException($"不支持写入 {found.DataType} 类型的度量");
            metric = found; start = ScadaTime.NowMs();
        }
        var payload = new SparkplugPayload { Timestamp = (ulong)start };
        payload.Metrics.Add(new SparkplugMetric { Name = metric.Name, Alias = metric.Alias, DataType = metric.DataType, Value = value, Timestamp = (ulong)start });
        await session.PublishAsync(DeviceScope ? DeviceTopic("DCMD") : NodeTopic("NCMD"), payload.Encode(), 0, false, device.TimeoutMs, cancellationToken);
        if (!_o.ConfirmWrites) { LastError = null; return; }
        var ok = await _updated.WaitUntilAsync(() => { lock (_gate) return _born && metric.ReceivedTs >= start && metric.Value is double v && MqttPayload.SameValue(v, value); }, device.TimeoutMs, cancellationToken);
        if (!ok) throw new IOException($"已发送 {(DeviceScope ? "DCMD" : "NCMD")}，但 {device.TimeoutMs} ms 内未收到一致的 DATA 回报，结果未知");
        LastError = null;
    }

    public void Disconnect()
    {
        var s = _session; _session = null; Volatile.Write(ref _connecting, null);
        if (s is not null) _ = CloseAsync(s);
    }

    private async Task CloseAsync(MqttSession s)
    {
        // 正常断开不会触发遗嘱，Primary Host 须主动发布离线 STATE。
        if (_o.PrimaryHostId.Length > 0 && s.IsConnected)
            try { await s.PublishAsync($"{Ns}/STATE/{_o.PrimaryHostId}", StatePayload(false, ScadaTime.NowMs()), 1, true, 2000, CancellationToken.None); } catch { }
        await s.DisposeAsync();
    }

    public async ValueTask DisposeAsync() { var s = _session; _session = null; if (s is not null) await CloseAsync(s); }
}
