using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector;

// One tracker belongs to one worker generation. Snapshots never expose device credentials.
internal sealed class DeviceDiagnostics(Device device, IReadOnlyList<Tag> tags, ReadPlan plan, int configVersion)
{
    private readonly object _gate = new();
    private readonly long _startedTs = device.Enabled && tags.Count > 0 ? ScadaTime.NowMs() : 0;
    private readonly Queue<CollectorDiagnosticEvent> _events = new();
    private readonly Queue<CollectorPollSample> _samples = new();
    private readonly Dictionary<int, (TagValue Value, double? Raw, long? LastGood)> _points = new();
    private long _eventId, _connectAttempts, _connectFailures, _pollAttempts, _pollSuccesses, _pollFailures, _degradedPolls;
    private long _commandsSucceeded, _commandsFailed, _commandsExpired, _commandsRejected;
    private long? _lastAttemptTs, _lastSuccessTs, _lastFailureTs, _nextRetryTs, _lastCommandTs;
    private int _consecutiveFailures;
    private string _state = !device.Enabled ? "Disabled" : tags.Count == 0 ? "NoTags" : "Waiting";
    private string _operation = "Idle";
    private int? _operationTagId;
    private string? _error, _lastFailure;

    public void Connecting()
    {
        lock (_gate) { _connectAttempts++; _operation = "Connecting"; _nextRetryTs = null; Event("Info", "Connecting", "开始连接"); }
    }
    public void Connected()
    {
        lock (_gate) { _operation = "Idle"; Event("Info", "Connected", "连接已建立，等待读取确认"); }
    }
    public void ConnectionFailed(string error, int retryMs)
    {
        lock (_gate) { _connectFailures++; Failure(error, retryMs); Event("Error", "ConnectFailed", error); }
    }
    public void Polling()
    {
        lock (_gate) { _pollAttempts++; _lastAttemptTs = ScadaTime.NowMs(); _operation = "Reading"; }
    }
    public void Point(TagValue value, double? raw)
    {
        lock (_gate)
        {
            var old = _points.GetValueOrDefault(value.TagId);
            _points[value.TagId] = (value, raw is double number && double.IsFinite(number) ? number : null,
                value.Quality == TagQuality.Good ? value.Ts : old.LastGood);
            if (old.Value?.Quality != value.Quality && value.Quality != TagQuality.Good)
                Event("Warning", "QualityChanged", value.Error ?? value.Quality.ToString(), value.TagId);
            else if (old.Value is not null && old.Value.Quality != TagQuality.Good && value.Quality == TagQuality.Good)
                Event("Info", "QualityRecovered", "测点质量已恢复", value.TagId);
        }
    }
    public void Polled(double durationMs, string? error)
    {
        lock (_gate)
        {
            _pollSuccesses++;
            var ts = ScadaTime.NowMs(); var good = _points.Values.Count(x => x.Value.Quality == TagQuality.Good);
            var bad = tags.Count - good; var nextState = bad == 0 ? "Online" : "Degraded";
            if (_state != nextState) Event(bad == 0 ? "Info" : "Warning", "StateChanged", bad == 0 ? "采集已恢复正常" : $"连接正常，{bad} 个测点质量异常");
            _state = nextState; _operation = "Idle"; _error = Trim(error); _consecutiveFailures = 0; _nextRetryTs = null; _lastSuccessTs = ts;
            if (bad > 0) _degradedPolls++;
            Sample(new(ts, durationMs, true, good, bad, Trim(error)));
        }
    }
    public void PollFailed(double durationMs, string error, int retryMs)
    {
        lock (_gate) { _pollFailures++; Failure(error, retryMs); Sample(new(ScadaTime.NowMs(), durationMs, false, 0, tags.Count, Trim(error))); Event("Error", "ReadFailed", error); }
    }
    public void ReconnectRequested()
    {
        lock (_gate) Event("Info", "ReconnectRequested", "管理员请求重连，将在当前采集及排队命令结束后执行");
    }
    public void Reconnecting()
    {
        lock (_gate) { _state = "Waiting"; _error = null; _nextRetryTs = null; Event("Info", "Reconnecting", "执行手动重连"); }
    }
    public void CommandStarted(int tagId)
    {
        lock (_gate) { _operation = "Writing"; _operationTagId = tagId; _lastCommandTs = ScadaTime.NowMs(); }
    }
    public void CommandCompleted(int tagId, bool success, string message)
    {
        lock (_gate)
        {
            if (success) _commandsSucceeded++; else _commandsFailed++;
            _operation = "Idle"; _operationTagId = null;
            Event(success ? "Info" : "Warning", "CommandResult", message, tagId);
        }
    }
    public void CommandNotSent(int tagId, string message, bool expired = false)
    {
        lock (_gate) { if (expired) _commandsExpired++; else _commandsRejected++; Event("Warning", expired ? "CommandExpired" : "CommandRejected", message, tagId); }
    }
    public void Stopped(string? error = null)
    {
        lock (_gate) { _state = error is null ? "Stopped" : "Faulted"; _operation = "Idle"; _operationTagId = null; _error = Trim(error); _nextRetryTs = null; Event(error is null ? "Info" : "Error", "WorkerStopped", error ?? "采集任务已停止"); }
    }

    public CollectorDeviceDiagnostics Snapshot(int pendingCommands, bool configurationPending)
    {
        lock (_gate)
        {
            var now = ScadaTime.NowMs(); var staleAfter = Math.Max(3000L, Math.Max(device.PollIntervalMs * 3L, device.PollIntervalMs + (long)device.TimeoutMs));
            var points = tags.Select(tag =>
            {
                var found = _points.TryGetValue(tag.Id, out var point); long? age = found ? Math.Max(0, now - point.Value.Ts) : null;
                return new CollectorTagDiagnostic(tag.Id, tag.Name, Address(device, tag), tag.DataType.ToString(), point.Raw,
                    found && point.LastGood is not null ? point.Value.Value : null, found ? point.Value.Quality : null, found ? point.Value.Ts : null,
                    found ? point.Value.SourceTs : null, point.LastGood, age, age > staleAfter, found ? point.Value.Error : null);
            }).ToList();
            var quality = new CollectorQualityCounts(points.Count(x => x.Quality == TagQuality.Good), points.Count(x => x.Quality == TagQuality.Bad),
                points.Count(x => x.Quality == TagQuality.Uncertain), points.Count(x => x.Quality == TagQuality.CommFailure), points.Count(x => x.Quality is null), points.Count(x => x.Stale));
            var durations = _samples.Select(x => x.DurationMs).Order().ToArray();
            var metrics = new CollectorDiagnosticMetrics(_connectAttempts, _connectFailures, _pollAttempts, _pollSuccesses, _pollFailures, _degradedPolls,
                _consecutiveFailures, _lastAttemptTs, _lastSuccessTs, _lastFailureTs, _nextRetryTs, _lastFailure,
                _commandsSucceeded, _commandsFailed, _commandsExpired, _commandsRejected, _lastCommandTs,
                _samples.LastOrDefault()?.DurationMs ?? 0, durations.Length == 0 ? 0 : durations.Average(), durations.LastOrDefault(),
                durations.Length == 0 ? 0 : durations[(int)Math.Ceiling(durations.Length * .95) - 1], durations.Length);
            IReadOnlyList<CollectorReadGroup> groups = device.Protocol == ProtocolType.OpcUa || Scada.Core.Protocols.ProtocolCatalog.IsTextAddress(device.Protocol)
                ? plan.Tags.Chunk(100).Select((batch, i) => new CollectorReadGroup(i + 1, device.Protocol == ProtocolType.OpcUa ? "OpcNode" : "Address", null, null, batch.Length, "节点", batch.Select(x => x.Id).ToArray())).ToArray()
                : plan.Blocks.Select((block, i) => new CollectorReadGroup(i + 1, block.Area.ToString(), device.Protocol == ProtocolType.SiemensS7 && block.Area == TagArea.DataBlock ? block.DbNumber : null,
                    block.StartAddress, block.Length, device.Protocol == ProtocolType.SiemensS7 ? "字节" : block.Area is TagArea.Coil or TagArea.DiscreteInput ? "位" : "寄存器", block.Tags.Select(x => x.TagId).ToArray())).ToArray();
            return new(device.Id, device.Name, device.Protocol.ToString(), Connection(device), _state, _operation, _operationTagId, _error,
                device.Enabled, configurationPending, configVersion, now, _startedTs, device.PollIntervalMs, device.TimeoutMs, staleAfter, pendingCommands,
                tags.Count, quality, metrics, groups, points, _events.Reverse().ToArray(), _samples.Reverse().ToArray());
        }
    }
    private void Failure(string error, int retryMs)
    {
        _state = "Offline"; _operation = "Idle"; _consecutiveFailures++; _error = _lastFailure = Trim(error);
        _lastFailureTs = ScadaTime.NowMs(); _nextRetryTs = _lastFailureTs + retryMs;
    }
    private void Event(string level, string kind, string message, int? tagId = null)
    {
        _events.Enqueue(new(++_eventId, ScadaTime.NowMs(), level, kind, Trim(message)!, tagId));
        while (_events.Count > 200) _events.Dequeue();
    }
    private void Sample(CollectorPollSample sample) { _samples.Enqueue(sample); while (_samples.Count > 120) _samples.Dequeue(); }
    private static string? Trim(string? text) => text?.Length > 1000 ? text[..1000] : text;
    private static string Connection(Device device) => device.Protocol switch
    {
        ProtocolType.OpcUa => $"{device.OpcEndpointUrl} · {device.OpcSecurityMode} · {device.OpcSecurityPolicy}",
        ProtocolType.ModbusRtu => $"{device.SerialPort} · {device.BaudRate}/{device.DataBits}/{device.Parity}/{device.StopBits} · 站号 {device.StationNumber}",
        ProtocolType.SiemensS7 => $"{device.Host}:{device.Port} · {device.Cpu} · Rack {device.Rack} / Slot {device.Slot}",
        _ when Scada.Core.Protocols.ProtocolCatalog.IsTextAddress(device.Protocol) => $"{device.Protocol} · {device.Host}:{device.Port}",
        _ => $"{device.Host}:{device.Port} · 站号 {device.StationNumber} · {device.ByteOrder}"
    };
    private static string Address(Device device, Tag tag) => device.Protocol switch
    {
        ProtocolType.OpcUa => tag.NodeId,
        _ when tag.Area == TagArea.Address => tag.NodeId,
        ProtocolType.SiemensS7 => $"{(tag.Area == TagArea.DataBlock ? $"DB{tag.DbNumber}" : tag.Area.ToString())}:{tag.Address}{(tag.DataType == TagDataType.Bool ? $".{tag.BitOffset}" : "")}",
        _ => $"{tag.Area}:{tag.Address}"
    };
}
