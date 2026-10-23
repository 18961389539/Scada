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

public sealed class CollectorEngine : BackgroundService, ICollectorControl
{
    private readonly IConfigSnapshot _config;
    private readonly IDeviceDriverFactory _driverFactory;
    private readonly IReadPlanBuilder _planBuilder;
    private readonly IRealtimeCache _cache;
    private readonly IRealtimeBus _bus;
    private readonly IAlarmSink _alarmSink;
    private readonly IOptions<ScadaSettings> _settings;
    private readonly ILogger<CollectorEngine> _logger;

    private readonly ConcurrentDictionary<int, DeviceWorker> _workers = new();
    private readonly ConcurrentDictionary<int, CollectorDeviceStatus> _statuses = new();
    private readonly ConcurrentDictionary<int, Task> _workerTasks = new();
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
        ILogger<CollectorEngine> logger)
    {
        _config = config;
        _driverFactory = driverFactory;
        _planBuilder = planBuilder;
        _cache = cache;
        _bus = bus;
        _alarmSink = alarmSink;
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<CollectorDeviceStatus> GetStatuses() =>
        _statuses.Values.OrderBy(x => x.DeviceId).ToList();

    public void RequestReload() => _reloadRequested = true;

    public async Task<CommandExecutionResult> WriteAsync(
        int deviceId,
        Tag tag,
        double value,
        CancellationToken cancellationToken = default)
    {
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
            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_reloadRequested || _config.Version != _loadedConfigVersion)
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
        var enabledDevices = _config.Devices.Where(x => x.Enabled).ToList();
        var tagsByDevice = _config.Tags.GroupBy(x => x.DeviceId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Tag>)x.ToList());

        foreach (var device in enabledDevices)
        {
            if (!tagsByDevice.TryGetValue(device.Id, out var tags) || tags.Count == 0)
            {
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
                status => _statuses[device.Id] = status);

            _workers[device.Id] = worker;
            _statuses[device.Id] = new CollectorDeviceStatus(
                device.Id, device.Name, false, "等待连接", 0, 0, tags.Count, 0, 0);
            _workerTasks[device.Id] = Task.Run(() => worker.RunAsync(_workerCts.Token), CancellationToken.None);
        }

        var activeIds = enabledDevices.Select(x => x.Id).ToHashSet();
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
        private readonly Dictionary<int, TagValue> _lastPublished = new();
        private readonly Dictionary<int, Tag> _tagMap;
        private ReadPlan _plan = new();
        private bool _online;
        private int? _offlineAlarmId;
        private int _reconnectAttempts;
        private long _commFailures;
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
            Action<CollectorDeviceStatus> reportStatus)
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
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _plan = _planBuilder.Build(_device, _tags);
            var backoffMs = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var cycleStart = Stopwatch.GetTimestamp();

                if (!_driver.IsConnected)
                {
                    try
                    {
                        await _driver.ConnectAsync(_device, cancellationToken);
                        backoffMs = 0;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _lastError = ex.Message;
                        await MarkOfflineAsync(ex.Message, cancellationToken);
                        backoffMs = backoffMs == 0 ? 1000 : Math.Min(backoffMs * 2, 30000);
                        _reconnectAttempts++;
                        ReportStatus();
                        await ProcessCommandsAsync(cancellationToken);
                        await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                        continue;
                    }
                }

                try
                {
                    var rawValues = await _driver.ReadAsync(_device, _plan, cancellationToken);
                    var ts = ScadaTime.NowMs();
                    var rawMap = rawValues.ToDictionary(x => x.TagId);

                    foreach (var tag in _tags)
                    {
                        if (!rawMap.TryGetValue(tag.Id, out var raw))
                        {
                            continue;
                        }

                        var value = raw.Quality == TagQuality.Good
                            ? tag.DataType == TagDataType.Bool ? raw.Value : raw.Value * tag.Scale + tag.Offset
                            : _cache.Get(tag.Id)?.Value ?? 0;
                        var quality = raw.Quality;
                        if (!double.IsFinite(value)) { value = _cache.Get(tag.Id)?.Value ?? 0; quality = TagQuality.Bad; }
                        var tagValue = new TagValue(tag.Id, value, quality, ts, raw.SourceTs, raw.Error);
                        _cache.Set(tagValue);
                        if (!_lastPublished.TryGetValue(tag.Id, out var previous)
                            || previous.Value != tagValue.Value
                            || previous.Quality != tagValue.Quality)
                        {
                            _bus.Publish(tagValue);
                            _lastPublished[tag.Id] = tagValue;
                        }
                    }

                    _online = true;
                    _lastError = string.Join("；", rawValues.Where(x => x.Error is not null).Select(x => $"#{x.TagId} {x.Error}").Take(3));
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
                    backoffMs = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _lastError = ex.Message;
                    _commFailures++;
                    await MarkOfflineAsync(ex.Message, cancellationToken);
                    _driver.Disconnect();
                    backoffMs = backoffMs == 0 ? 1000 : Math.Min(backoffMs * 2, 30000);
                    ReportStatus();
                }

                await ProcessCommandsAsync(cancellationToken);
                await WaitForWorkOrDelayAsync(Math.Max(_device.PollIntervalMs, backoffMs), cancellationToken);
            }
        }

        public async Task<CommandExecutionResult> ExecuteWriteAsync(Tag tag, double value, CancellationToken cancellationToken)
        {
            if (!_tagMap.ContainsKey(tag.Id))
            {
                return new CommandExecutionResult(false, "测点不属于该设备");
            }

            if (!_driver.IsConnected || !_online)
            {
                return new CommandExecutionResult(false, "设备离线，无法下发");
            }

            var tcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
            var command = new WriteCommand(tag, value, tcs, deadline.Token);
            if (!_commands.Writer.TryWrite(command))
            {
                return new CommandExecutionResult(false, "命令队列不可用");
            }

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
                    command.Completion.TrySetResult(new CommandExecutionResult(false, "命令已取消，未下发"));
                    continue;
                }
                if (!_driver.IsConnected || !_online)
                {
                    command.Completion.TrySetResult(new CommandExecutionResult(false, "设备离线，无法下发"));
                    continue;
                }

                try
                {
                    command.Started = true;
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(command.Token, cancellationToken);
                    linked.Token.ThrowIfCancellationRequested();
                    var engineering = await WriteAndVerifyAsync(command.Tag, command.Value, linked.Token);
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
                    command.Completion.TrySetResult(new CommandExecutionResult(false, "执行结果未知，请检查设备反馈；不会自动重发"));
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    if (!_driver.IsConnected) await MarkOfflineAsync(ex.Message, cancellationToken);
                    command.Completion.TrySetResult(new CommandExecutionResult(false, ex.Message));
                }
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
            _cache.Set(tagValue);
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
            foreach (var tag in _tags)
            {
                var lastValue = _cache.Get(tag.Id)?.Value ?? 0;
                var bad = new TagValue(tag.Id, lastValue, TagQuality.CommFailure, ts);
                _cache.Set(bad);
                if (!_lastPublished.TryGetValue(tag.Id, out var previous) || previous.Quality != TagQuality.CommFailure)
                {
                    _bus.Publish(bad);
                    _lastPublished[tag.Id] = bad;
                }
            }

            if (_offlineAlarmId is null)
            {
                _offlineAlarmId = await _alarmSink.RaiseDeviceOfflineAsync(
                    _device,
                    $"设备离线：{error}",
                    ts,
                    cancellationToken);
            }
        }

        private async Task WaitForWorkOrDelayAsync(int delayMs, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(Math.Max(delayMs, 10), linked.Token);
            var commandTask = _commands.Reader.WaitToReadAsync(linked.Token).AsTask();
            var completed = await Task.WhenAny(delayTask, commandTask);

            // A queued command wakes the worker immediately; the delay is abandoned.
            await linked.CancelAsync();
            try
            {
                await Task.WhenAll(delayTask, commandTask);
            }
            catch (OperationCanceledException)
            {
                // normal
            }

            _ = completed;
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
                _commFailures));
        }

        public async ValueTask DisposeAsync()
        {
            _commands.Writer.TryComplete();
            while (_commands.Reader.TryRead(out var command))
                command.Completion.TrySetResult(new CommandExecutionResult(false, "设备配置变更或服务停止，未执行的命令已取消"));
            await _driver.DisposeAsync();
        }

        private sealed record WriteCommand(Tag Tag, double Value, TaskCompletionSource<CommandExecutionResult> Completion, CancellationToken Token)
        {
            public volatile bool Started;
        }
    }
}
