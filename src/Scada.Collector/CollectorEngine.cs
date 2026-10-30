using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;

namespace Scada.Collector;

public sealed class CollectorEngine : BackgroundService, ICollectorControl, ICollectorDiagnostics
{
    private readonly IConfigSnapshot _config;
    private readonly IDeviceDriverFactory _driverFactory;
    private readonly IReadPlanBuilder _planBuilder;
    private readonly IRealtimeCache _cache;
    private readonly IRealtimeBus _bus;
    private readonly IAlarmSink _alarmSink;
    private readonly IOptions<ScadaSettings> _settings;
    private readonly ILogger<CollectorEngine> _logger;
    private readonly ICollectorJournal? _journal;
    private readonly INodeRole _role;
    private readonly LivenessRegistry? _liveness;
    private long _loadedRoleGeneration = -1;

    private readonly ConcurrentDictionary<int, DeviceWorker> _workers = new();
    private readonly ConcurrentDictionary<int, CollectorDeviceStatus> _statuses = new();
    private readonly ConcurrentDictionary<int, Task> _workerTasks = new();
    private readonly ConcurrentDictionary<Guid, (int DeviceId, Task Task)> _retiredTasks = new();
    private CancellationTokenSource? _workerCts;
    private int _loadedConfigVersion = -1;
    private volatile bool _reloadRequested;

    public CollectorEngine(
        IConfigSnapshot config,
        IDeviceDriverFactory driverFactory,
        IReadPlanBuilder planBuilder,
        IRealtimeCache cache,
        IRealtimeBus bus,
        IAlarmSink alarmSink,
        IOptions<ScadaSettings> settings,
        ILogger<CollectorEngine> logger,
        ICollectorJournal? journal = null,
        INodeRole? role = null,
        LivenessRegistry? liveness = null)
    {
        _liveness = liveness;
        _role = role ?? new StandaloneNodeRole();
        _config = config;
        _driverFactory = driverFactory;
        _planBuilder = planBuilder;
        _cache = cache;
        _bus = bus;
        _alarmSink = alarmSink;
        _settings = settings;
        _logger = logger;
        _journal = journal;
    }

    public IReadOnlyList<CollectorDeviceStatus> GetStatuses() =>
        _statuses.Values.OrderBy(x => x.DeviceId).ToList();

    public void RequestReload() => _reloadRequested = true;

    public bool IsDeviceQuiescent(int deviceId) => (!_workerTasks.TryGetValue(deviceId, out var task) || task.IsCompleted)
        && !_retiredTasks.Values.Any(x => x.DeviceId == deviceId && !x.Task.IsCompleted);

    public CollectorDeviceDiagnostics? GetDiagnostics(int deviceId)
    {
        if (!_config.TryGetDevice(deviceId, out var device)) return null;
        var tags = _config.Tags.Where(x => x.DeviceId == deviceId).OrderBy(x => x.Id).ToList();
        var pending = _reloadRequested || _config.Version != _loadedConfigVersion;
        if (device.Enabled && tags.Count > 0 && _workers.TryGetValue(deviceId, out var worker)) return worker.Diagnostics(pending);
        var idle = new DeviceDiagnostics(device, tags, _planBuilder.Build(device, tags), _config.Version).Snapshot(0, pending);
        return !IsDeviceQuiescent(deviceId) ? idle with { State = "Stopping", CurrentError = "旧采集 I/O 尚未退出，暂不启动新任务" } : idle;
    }

    public bool RequestReconnect(int deviceId)
    {
        if (!_config.TryGetDevice(deviceId, out var device) || !device.Enabled || !_config.Tags.Any(x => x.DeviceId == deviceId)) return false;
        if (_workers.TryGetValue(deviceId, out var worker)) worker.RequestReconnect();
        if (!_workerTasks.TryGetValue(deviceId, out var task) || task.IsCompleted) RequestReload();
        return true;
    }

    public Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        if (!_role.IsActive) throw new InvalidOperationException(_role.StandbyMessage);
        if (_reloadRequested || _config.Version != _loadedConfigVersion || _retiredTasks.Values.Any(x => x.DeviceId == deviceId && !x.Task.IsCompleted))
            throw new InvalidOperationException("Acquisition configuration is reloading.");
        if (!_workers.TryGetValue(deviceId, out var worker)) throw new InvalidOperationException("Device is disabled or unavailable.");
        return worker.ExecuteReadAsync(tagIds, cancellationToken);
    }

    public async Task<CollectorProbeResult?> ProbeAsync(int deviceId, Tag tag, CancellationToken cancellationToken = default)
    {
        if (!_role.IsActive) throw new InvalidOperationException(_role.StandbyMessage);
        return _workers.TryGetValue(deviceId, out var worker) ? await worker.ProbeAsync(tag, cancellationToken) : null;
    }

    public async Task<CommandExecutionResult> WriteAsync(
        int deviceId,
        Tag tag,
        double value,
        CancellationToken cancellationToken = default)
    {
        if (tag.Kind != TagKind.Physical || tag.DeviceId != deviceId) return new(false, "测点不是该设备的物理测点");
        if (!_role.IsActive) return new(false, _role.StandbyMessage);
        if (_reloadRequested || _config.Version != _loadedConfigVersion)
            return new CommandExecutionResult(false, "采集配置正在重新加载，请稍后重试");
        if (_retiredTasks.Values.Any(x => x.DeviceId == deviceId && !x.Task.IsCompleted))
            return new CommandExecutionResult(false, "旧采集 I/O 尚未退出，暂不接受控制");
        if (!_workers.TryGetValue(deviceId, out var worker))
        {
            return new CommandExecutionResult(false, "设备不存在或未启用");
        }

        return await worker.ExecuteWriteAsync(tag, value, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RestartWorkersAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _liveness?.Beat("collector");
            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var (key, retired) in _retiredTasks) if (retired.Task.IsCompleted && _retiredTasks.TryRemove(key, out _)) _reloadRequested = true;
            if (_reloadRequested || _config.Version != _loadedConfigVersion || _role.Generation != _loadedRoleGeneration)
            {
                _reloadRequested = false;
                _logger.LogInformation("检测到配置变更，重建采集任务");
                await RestartWorkersAsync(stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopWorkersAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task RestartWorkersAsync(CancellationToken cancellationToken)
    {
        await StopWorkersAsync();

        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadedRoleGeneration = _role.Generation;
        // 备机不采集：不建立任何设备连接，避免与主机同时轮询现场（部分设备只允许单连接）。
        var enabledDevices = _role.IsActive ? _config.Devices.Where(x => x.Enabled && x.GatewayId == null).ToList() : [];
        if (!_role.IsActive) _statuses.Clear();
        var waitingIds = new HashSet<int>();
        var tagsByDevice = _config.Tags.Where(x => x.Kind == TagKind.Physical && x.DeviceId.HasValue).GroupBy(x => x.DeviceId!.Value)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Tag>)x.ToList());

        foreach (var device in enabledDevices)
        {
            if (!tagsByDevice.TryGetValue(device.Id, out var tags) || tags.Count == 0)
            {
                continue;
            }
            if (!IsDeviceQuiescent(device.Id))
            {
                waitingIds.Add(device.Id);
                _statuses[device.Id] = new(device.Id, device.Name, false, "旧采集 I/O 尚未退出，暂不启动新任务", 0, 0, tags.Count, 0, 0);
                continue;
            }

            var worker = new DeviceWorker(
                device,
                tags,
                _driverFactory.Create(device),
                _planBuilder,
                _cache,
                _bus,
                _alarmSink,
                _settings.Value,
                _logger,
                status => _statuses[device.Id] = status,
                _config.Version,
                _journal);

            _workers[device.Id] = worker;
            _statuses[device.Id] = new CollectorDeviceStatus(
                device.Id, device.Name, false, "等待连接", 0, 0, tags.Count, 0, 0);
            var workerToken = _workerCts.Token;
            _workerTasks[device.Id] = Task.Run(() => worker.RunAsync(workerToken), CancellationToken.None);
        }

        var activeIds = _workers.Keys.Concat(waitingIds).ToHashSet();
        foreach (var id in _statuses.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _statuses.TryRemove(id, out _);
        }

        _loadedConfigVersion = _config.Version;
    }

    private async Task StopWorkersAsync()
    {
        if (_workerCts is not null)
        {
            await _workerCts.CancelAsync();
        }

        var tasks = _workerTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // a blocked driver call is abandoned together with its driver below
            }
        }

        foreach (var (deviceId, task) in _workerTasks) if (!task.IsCompleted) _retiredTasks[Guid.NewGuid()] = (deviceId, task);
        foreach (var worker in _workers.Values)
        {
            await worker.DisposeAsync();
        }

        _workers.Clear();
        _workerTasks.Clear();

        if (_workerCts is not null)
        {
            _workerCts.Dispose();
            _workerCts = null;
        }
    }

    private sealed class DeviceWorker
    {
        private readonly Device _device;
        private readonly IReadOnlyList<Tag> _tags;
        private readonly IDeviceDriver _driver;
        private readonly IReadPlanBuilder _planBuilder;
        private readonly IRealtimeCache _cache;
        private readonly IRealtimeBus _bus;
        private readonly IAlarmSink _alarmSink;
        private readonly ScadaSettings _settings;
        private readonly ILogger _logger;
        private readonly Action<CollectorDeviceStatus> _reportStatus;
        private readonly Channel<WriteCommand> _commands = Channel.CreateBounded<WriteCommand>(128);
        private readonly Channel<ReadCommand> _reads = Channel.CreateBounded<ReadCommand>(32);
        private readonly Channel<ProbeCommand> _probes = Channel.CreateBounded<ProbeCommand>(8);
        private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly Dictionary<int, TagValue> _lastPublished = new();
        private readonly Dictionary<int, Tag> _tagMap;
        private readonly ReadPlan _plan;
        private readonly DeviceDiagnostics _diagnostics;
        private readonly ICollectorJournal? _journal;
        private volatile bool _online;
        private int _reconnectRequested;
        private int? _offlineAlarmId;
        private int _reconnectAttempts;
        private long _commFailures;
        private long _lastProbeMs;
        private string? _lastError;
        private long _lastPollTs;
        private double _lastCycleMs;

        public DeviceWorker(
            Device device,
            IReadOnlyList<Tag> tags,
            IDeviceDriver driver,
            IReadPlanBuilder planBuilder,
            IRealtimeCache cache,
            IRealtimeBus bus,
            IAlarmSink alarmSink,
            ScadaSettings settings,
            ILogger logger,
            Action<CollectorDeviceStatus> reportStatus,
            int configVersion,
            ICollectorJournal? journal)
        {
            _device = device;
            _tags = tags;
            _driver = driver;
            _planBuilder = planBuilder;
            _cache = cache;
            _bus = bus;
            _alarmSink = alarmSink;
            _settings = settings;
            _logger = logger;
            _reportStatus = reportStatus;
            _tagMap = tags.ToDictionary(x => x.Id);
            _plan = _planBuilder.Build(_device, _tags);
            _diagnostics = new DeviceDiagnostics(device, tags, _plan, configVersion);
            _journal = journal;
        }

        public CollectorDeviceDiagnostics Diagnostics(bool pending) => _diagnostics.Snapshot(_commands.Reader.Count, pending);
        public void RequestReconnect()
        {
            if (Interlocked.Exchange(ref _reconnectRequested, 1) == 0) _diagnostics.ReconnectRequested();
            _wake.Writer.TryWrite(true);
        }

        // 复用采集连接做一次探针读取：与采集轮询在同一循环内串行，不会二次打开串口。
        // 返回 null 表示当前未在线，调用方回退到独立连接。
        public Task<CollectorProbeResult?> ProbeAsync(Tag tag, CancellationToken cancellationToken)
        {
            if (!_online || !_driver.IsConnected) return Task.FromResult<CollectorProbeResult?>(null);
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastProbeMs) < 1000)
                return Task.FromResult<CollectorProbeResult?>(new(true, null, "测试读取过于频繁，请稍后再试"));
            Interlocked.Exchange(ref _lastProbeMs, now);
            var completion = new TaskCompletionSource<CollectorProbeResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_probes.Writer.TryWrite(new ProbeCommand(tag, completion, cancellationToken)))
                return Task.FromResult<CollectorProbeResult?>(new(true, null, "测试队列已满，请稍后再试"));
            _wake.Writer.TryWrite(true);
            return completion.Task;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try { await RunCoreAsync(cancellationToken); _diagnostics.Stopped(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _diagnostics.Stopped(); }
            catch (Exception ex)
            {
                _online = false; _lastError = $"采集任务异常停止：{ex.Message}";
                _diagnostics.Stopped(ex.Message); ReportStatus();
                _logger.LogError(ex, "设备 {Device} 的采集任务异常停止", _device.Name);
            }
        }

        private async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            var backoffMs = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var cycleStart = Stopwatch.GetTimestamp();

                if (Interlocked.Exchange(ref _reconnectRequested, 0) != 0)
                {
                    _driver.Disconnect(); _online = false; backoffMs = 0;
                    _diagnostics.Reconnecting(); ReportStatus();
                }

                if (!_driver.IsConnected)
                {
                    try
                    {
                        _diagnostics.Connecting();
                        await _driver.ConnectAsync(_device, cancellationToken);
                        _diagnostics.Connected();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _lastError = ex.Message;
                        _commFailures++;
                        backoffMs = backoffMs == 0 ? 1000 : Math.Min(backoffMs * 2, 30000);
                        _diagnostics.ConnectionFailed(ex.Message, backoffMs);
                        await MarkOfflineAsync(ex.Message, cancellationToken);
                        _reconnectAttempts++;
                        ReportStatus();
                        await ProcessCommandsAsync(cancellationToken);
                        await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                        continue;
                    }
                }

                var readStart = Stopwatch.GetTimestamp();
                _diagnostics.Polling();
                try
                {
                    var rawValues = await _driver.ReadAsync(_device, _plan, cancellationToken);
                    var readDurationMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                    var ts = ScadaTime.NowMs();
                    var rawMap = rawValues.ToDictionary(x => x.TagId);
                    var updates = new List<TagValue>(_tags.Count);

                    foreach (var tag in _tags)
                    {
                        var raw = rawMap.GetValueOrDefault(tag.Id) ?? new RawTagValue(tag.Id, 0, TagQuality.Bad, null, "本次读取未返回该测点");

                        var value = raw.Quality == TagQuality.Good
                            ? tag.DataType == TagDataType.Bool ? raw.Value : raw.Value * tag.Scale + tag.Offset
                            : _cache.Get(tag.Id)?.Value ?? 0;
                        var quality = raw.Quality;
                        var pointError = raw.Error;
                        if (!double.IsFinite(value)) { value = _cache.Get(tag.Id)?.Value ?? 0; quality = TagQuality.Bad; pointError = "工程量换算结果不是有限数值"; }
                        var tagValue = new TagValue(tag.Id, value, quality, ts, raw.SourceTs, pointError);
                        updates.Add(tagValue);
                    }
                    if (_journal is not null) await _journal.AppendAsync(_device.Id, updates, cancellationToken);
                    foreach (var tagValue in updates)
                    {
                        var tag = _tagMap[tagValue.TagId]; var raw = rawMap.GetValueOrDefault(tag.Id);
                        _cache.Set(tagValue);
                        _diagnostics.Point(tagValue, raw?.Quality == TagQuality.Good ? raw.Value : null);
                        if (!_lastPublished.TryGetValue(tag.Id, out var previous)
                            || previous.Value != tagValue.Value
                            || previous.Quality != tagValue.Quality)
                        {
                            _bus.Publish(tagValue);
                            _lastPublished[tag.Id] = tagValue;
                        }
                    }

                    _online = true;
                    _lastError = string.Join("；", _tags.Select(x => _cache.Get(x.Id)).Where(x => x?.Quality != TagQuality.Good)
                        .Select(x => $"#{x!.TagId} {x.Error ?? x.Quality.ToString()}").Take(3));
                    if (_lastError.Length == 0) _lastError = null;
                    _reconnectAttempts = 0;
                    _lastPollTs = ts;
                    _lastCycleMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;

                    if (_offlineAlarmId is int alarmId)
                        {
                            await _alarmSink.RecoverDeviceOfflineAsync(alarmId, ts, cancellationToken);
                            _offlineAlarmId = null;
                        }

                    ReportStatus();
                    _diagnostics.Polled(readDurationMs, _lastError);
                    backoffMs = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _lastError = ex.Message;
                    _commFailures++;
                    backoffMs = backoffMs == 0 ? 1000 : Math.Min(backoffMs * 2, 30000);
                    _diagnostics.PollFailed(Stopwatch.GetElapsedTime(readStart).TotalMilliseconds, ex.Message, Math.Max(_device.PollIntervalMs, backoffMs));
                    await MarkOfflineAsync(ex.Message, cancellationToken);
                    _driver.Disconnect();
                    ReportStatus();
                }

                await ProcessCommandsAsync(cancellationToken);
                await WaitForWorkOrDelayAsync(Math.Max(_device.PollIntervalMs, backoffMs), cancellationToken);
            }
        }

        public async Task<IReadOnlyList<TagValue>> ExecuteReadAsync(IReadOnlyList<int> tagIds, CancellationToken cancellationToken)
        {
            if (tagIds.Count is < 1 or > 256 || tagIds.Any(id => !_tagMap.ContainsKey(id)))
                throw new InvalidOperationException("Read points must belong to this device (1 to 256 points).");
            if (!_driver.IsConnected || !_online || Volatile.Read(ref _reconnectRequested) != 0)
                throw new IOException("Device is offline or reconnecting.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
            var completion = new TaskCompletionSource<IReadOnlyList<TagValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tags = tagIds.Distinct().Select(id => _tagMap[id]).ToArray();
            if (!_reads.Writer.TryWrite(new(tags, completion, deadline.Token))) throw new IOException("Read queue is unavailable.");
            _wake.Writer.TryWrite(true);
            return await completion.Task.WaitAsync(deadline.Token);
        }

        public async Task<CommandExecutionResult> ExecuteWriteAsync(Tag tag, double value, CancellationToken cancellationToken)
        {
            if (!_tagMap.ContainsKey(tag.Id))
            {
                _diagnostics.CommandNotSent(tag.Id, "测点不属于该设备");
                return new CommandExecutionResult(false, "测点不属于该设备");
            }

            if (!_driver.IsConnected || !_online || Volatile.Read(ref _reconnectRequested) != 0)
            {
                _diagnostics.CommandNotSent(tag.Id, "设备离线或等待重连，无法下发");
                return new CommandExecutionResult(false, "设备离线或等待重连，无法下发");
            }

            var tcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
            var command = new WriteCommand(tag, value, tcs, deadline.Token);
            if (!_commands.Writer.TryWrite(command))
            {
                _diagnostics.CommandNotSent(tag.Id, "命令队列不可用");
                return new CommandExecutionResult(false, "命令队列不可用");
            }
            _wake.Writer.TryWrite(true);

            try
            {
                return await tcs.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return new CommandExecutionResult(false, command.Started
                    ? "命令超时或请求已取消，执行结果未知，请检查设备反馈；不会自动重发"
                    : "命令在执行前已超时或取消，未下发");
            }
        }

        private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
        {
            while (_commands.Reader.TryRead(out var command))
            {
                if (command.Token.IsCancellationRequested)
                {
                    _diagnostics.CommandNotSent(command.Tag.Id, "命令已取消，未下发", expired: true);
                    command.Completion.TrySetResult(new CommandExecutionResult(false, "命令已取消，未下发"));
                    continue;
                }
                if (!_driver.IsConnected || !_online)
                {
                    _diagnostics.CommandNotSent(command.Tag.Id, "设备离线，无法下发");
                    command.Completion.TrySetResult(new CommandExecutionResult(false, "设备离线，无法下发"));
                    continue;
                }

                try
                {
                    command.Started = true;
                    _diagnostics.CommandStarted(command.Tag.Id);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(command.Token, cancellationToken);
                    linked.Token.ThrowIfCancellationRequested();
                    var engineering = await WriteAndVerifyAsync(command.Tag, command.Value, linked.Token);
                    _diagnostics.CommandCompleted(command.Tag.Id, true, $"写入及回读成功：{engineering:0.######}");
                    command.Completion.TrySetResult(new CommandExecutionResult(true, "写入成功", engineering));
                    _logger.LogInformation(
                        "设备 {Device} 写入测点 {Tag} = {Value}，回读 {ReadBack}",
                        _device.Name,
                        command.Tag.Name,
                        command.Value,
                        engineering);
                }
                catch (OperationCanceledException)
                {
                    _diagnostics.CommandCompleted(command.Tag.Id, false, "执行结果未知，请检查设备反馈；不会自动重发");
                    command.Completion.TrySetResult(new CommandExecutionResult(false, "执行结果未知，请检查设备反馈；不会自动重发"));
                }
                catch (Exception ex)
                {
                    _diagnostics.CommandCompleted(command.Tag.Id, false, ex.Message);
                    _lastError = ex.Message;
                    if (!_driver.IsConnected) await MarkOfflineAsync(ex.Message, cancellationToken);
                    command.Completion.TrySetResult(new CommandExecutionResult(false, ex.Message));
                }
            }
            while (_reads.Reader.TryRead(out var read))
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(read.Token, cancellationToken);
                    linked.Token.ThrowIfCancellationRequested();
                    if (!_driver.IsConnected || !_online) throw new IOException("Device is offline.");
                    var raw = await _driver.ReadAsync(_device, _planBuilder.Build(_device, read.Tags), linked.Token);
                    var map = raw.ToDictionary(value => value.TagId);
                    var now = ScadaTime.NowMs();
                    var values = read.Tags.Select(tag =>
                    {
                        if (!map.TryGetValue(tag.Id, out var value)) throw new IOException($"Point #{tag.Id} was not returned.");
                        var engineering = tag.DataType == TagDataType.Bool ? value.Value : value.Value * tag.Scale + tag.Offset;
                        return new TagValue(tag.Id, engineering, double.IsFinite(engineering) ? value.Quality : TagQuality.Bad, now, value.SourceTs, value.Error);
                    }).ToArray();
                    read.Completion.TrySetResult(values);
                }
                catch (OperationCanceledException) { read.Completion.TrySetCanceled(); }
                catch (Exception ex) { read.Completion.TrySetException(ex); }
            }
            while (_probes.Reader.TryRead(out var probe))
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(probe.Token, cancellationToken);
                    linked.Token.ThrowIfCancellationRequested();
                    if (!_driver.IsConnected || !_online) throw new IOException("设备当前离线，无法复用采集连接；请先恢复采集。");
                    var raw = await _driver.ReadAsync(_device, _planBuilder.Build(_device, [probe.Tag]), linked.Token);
                    probe.Completion.TrySetResult(new CollectorProbeResult(true, raw.FirstOrDefault(), null));
                }
                // 探针失败只回传请求方，不能让一次错误地址把整台设备标记离线或断开。
                catch (OperationCanceledException) { probe.Completion.TrySetResult(new CollectorProbeResult(true, null, "测试读取超时")); }
                catch (Exception ex) { probe.Completion.TrySetResult(new CollectorProbeResult(true, null, ex.Message)); }
            }
        }

        private async Task<double> WriteAndVerifyAsync(Tag tag, double value, CancellationToken cancellationToken)
        {
            if (!CommandValuePolicy.TryGetRawValue(tag, value, out var expectedRaw, out var error))
                throw new InvalidOperationException(error);
            await _driver.WriteAsync(_device, tag, expectedRaw, cancellationToken);
            var verifyPlan = _planBuilder.Build(_device, new[] { tag });
            var readBack = await _driver.ReadAsync(_device, verifyPlan, cancellationToken);
            var feedback = readBack.FirstOrDefault(x => x.TagId == tag.Id) ?? throw new IOException("回读失败");
            if (feedback.Quality != TagQuality.Good || !double.IsFinite(feedback.Value))
                throw new IOException($"写入已发送，回读质量无效，结果未知：{feedback.Error ?? feedback.Quality.ToString()}");
            var raw = feedback.Value;

            var engineering = tag.DataType == TagDataType.Bool
                ? raw
                : raw * tag.Scale + tag.Offset;
            if (!double.IsFinite(engineering)) throw new IOException("写入已发送，回读工程量无效，结果未知");

            var ts = ScadaTime.NowMs();
            var tagValue = new TagValue(tag.Id, engineering, TagQuality.Good, ts, feedback.SourceTs);
            if (_journal is not null)
            {
                try { await _journal.AppendAsync(_device.Id, new[] { tagValue }, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new IOException($"写入已发送，回读已取得，但持久记录失败；请检查现场反馈：{ex.Message}", ex); }
            }
            _cache.Set(tagValue);
            _diagnostics.Point(tagValue, raw);
            if (!_lastPublished.TryGetValue(tag.Id, out var previous)
                || previous.Value != tagValue.Value
                || previous.Quality != tagValue.Quality)
            {
                _bus.Publish(tagValue);
                _lastPublished[tag.Id] = tagValue;
            }

            if (!CommandValuePolicy.Matches(tag, expectedRaw, raw))
                throw new IOException($"回读不匹配：目标 {value:0.######}，实际 {engineering:0.######}，请检查设备");
            return engineering;
        }

        private async Task MarkOfflineAsync(string? error, CancellationToken cancellationToken)
        {
            if (!_online && _offlineAlarmId is not null)
            {
                return;
            }

            _online = false;
            var ts = ScadaTime.NowMs();
            var updates = _tags.Select(tag => new TagValue(tag.Id, _cache.Get(tag.Id)?.Value ?? 0, TagQuality.CommFailure, ts, _cache.Get(tag.Id)?.SourceTs, error)).ToArray();
            Exception? journalFailure = null;
            if (_journal is not null)
            {
                try { await _journal.AppendAsync(_device.Id, updates, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { journalFailure = ex; }
            }
            foreach (var bad in updates)
            {
                var tag = _tagMap[bad.TagId]; _cache.Set(bad);
                _diagnostics.Point(bad, null);
                if (!_lastPublished.TryGetValue(tag.Id, out var previous) || previous.Quality != TagQuality.CommFailure)
                {
                    _bus.Publish(bad);
                    _lastPublished[tag.Id] = bad;
                }
            }
            if (journalFailure is not null) throw new IOException($"采集持久记录失败，任务已暂停；已保存数据不会被丢弃：{journalFailure.Message}", journalFailure);

            if (_offlineAlarmId is null)
            {
                _offlineAlarmId = await _alarmSink.RaiseDeviceOfflineAsync(
                    _device,
                    error,
                    ts,
                    cancellationToken);
            }
        }

        private async Task WaitForWorkOrDelayAsync(int delayMs, CancellationToken cancellationToken)
        {
            while (_wake.Reader.TryRead(out _)) { }
            if (Volatile.Read(ref _reconnectRequested) != 0 || _commands.Reader.TryPeek(out _) || _reads.Reader.TryPeek(out _) || _probes.Reader.TryPeek(out _)) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(Math.Max(delayMs, 10));
            try { await _wake.Reader.ReadAsync(linked.Token); while (_wake.Reader.TryRead(out _)) { } }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        private void ReportStatus()
        {
            _reportStatus(new CollectorDeviceStatus(
                _device.Id,
                _device.Name,
                _online,
                _lastError,
                _lastPollTs,
                _lastCycleMs,
                _tags.Count,
                _reconnectAttempts,
                _commFailures,
                ScadaTime.NowMs()));
        }

        public async ValueTask DisposeAsync()
        {
            _reads.Writer.TryComplete();
            while (_reads.Reader.TryRead(out var read)) read.Completion.TrySetException(new IOException("Acquisition stopped."));
            _probes.Writer.TryComplete();
            while (_probes.Reader.TryRead(out var probe)) probe.Completion.TrySetResult(new CollectorProbeResult(true, null, "采集已停止"));
            _commands.Writer.TryComplete();
            while (_commands.Reader.TryRead(out var command))
            {
                _diagnostics.CommandNotSent(command.Tag.Id, "采集任务已停止，未下发", expired: true);
                command.Completion.TrySetResult(new CommandExecutionResult(false, "设备配置变更或服务停止，未执行的命令已取消"));
            }
            await _driver.DisposeAsync();
        }

        private sealed record WriteCommand(Tag Tag, double Value, TaskCompletionSource<CommandExecutionResult> Completion, CancellationToken Token)
        {
            public volatile bool Started;
        }

        private sealed record ReadCommand(IReadOnlyList<Tag> Tags, TaskCompletionSource<IReadOnlyList<TagValue>> Completion, CancellationToken Token);

        private sealed record ProbeCommand(Tag Tag, TaskCompletionSource<CollectorProbeResult?> Completion, CancellationToken Token);
    }
}
