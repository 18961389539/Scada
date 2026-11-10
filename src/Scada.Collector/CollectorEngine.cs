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
using Scada.Core.Protocols;

namespace Scada.Collector;

public sealed class CollectorEngine : BackgroundService, ILocalCollector
{
    private static CommandExecutionResult CommandResult(GatewayCommandOutcome outcome, string message, double? readBack = null) =>
        CommandResults.Of(outcome, message, readBack);

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
    // 每台设备当前有效的 worker 代次。放弃的旧 worker 仍可能在后台回调状态，
    // 用代次比对把它们挡在 _statuses 之外。
    private readonly ConcurrentDictionary<int, long> _statusEpochs = new();
    private long _workerEpoch;
    // 绑定通道的设备共用一条物理链路；未绑定通道的设备仍然一设备一连接。
    private readonly ChannelRegistry _channels = new();
    private readonly ConcurrentDictionary<int, Task> _workerTasks = new();
    private readonly ConcurrentDictionary<Guid, (int DeviceId, Task Task)> _retiredTasks = new();
    // 每台设备一个 CTS：增量重建时只停掉配置真正变化的那几台，其余设备保持连接。
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _workerCtsByDevice = new();
    // 设备 + 物理测点的采集指纹，用于判断"配置改了但连接参数没变"，避免无谓断线。
    private readonly ConcurrentDictionary<int, string> _workerFingerprints = new();
    private CancellationTokenSource? _workerCts;
    private int _loadedCollectionVersion = -1;
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
        var pending = _reloadRequested || _config.CollectionVersion != _loadedCollectionVersion;
        if (device.Enabled && tags.Count > 0 && _workers.TryGetValue(deviceId, out var worker)) return worker.Diagnostics(pending);
        var idle = new DeviceDiagnostics(device, tags, _planBuilder.Build(device, tags), _config.CollectionVersion).Snapshot(0, pending);
        return !IsDeviceQuiescent(deviceId) ? idle with { State = "Stopping", CurrentError = "旧采集 I/O 尚未退出，暂不启动新任务" } : idle;
    }

    public bool RequestReconnect(int deviceId)
    {
        if (!_config.TryGetDevice(deviceId, out var device) || !device.Enabled || !_config.Tags.Any(x => x.DeviceId == deviceId)) return false;
        if (_workers.TryGetValue(deviceId, out var worker)) worker.RequestReconnect();
        if (!_workerTasks.TryGetValue(deviceId, out var task) || task.IsCompleted) RequestReload();
        return true;
    }

    public ChannelStatistics GetChannelStatistics(int channelId)
    {
        var buses = _channels.BusesOf(channelId);
        if (buses.Count == 0)
            return new ChannelStatistics(channelId, "", "", "", false, 0, 0, 0, 0, 0, 0, 0, null, ScadaTime.NowMs(), []);

        // 正常情况一条通道只有一条链路。改端点或改绑时会短暂出现新旧两条，
        // 取成员最多的一条来报——把两边加起来会得到"两倍总线"的假数字。
        var bus = buses.OrderByDescending(x => x.LeaseCount).ThenByDescending(x => x.Transactions).First();
        return bus.Snapshot(DescribeDevice) with { Connections = buses.Count };
    }

    private (string Name, int Station) DescribeDevice(int deviceId) =>
        _config.TryGetDevice(deviceId, out var device) ? (device.Name, device.StationNumber) : ($"#{deviceId}", 0);

    public Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        if (!_role.IsActive) throw new InvalidOperationException(_role.StandbyMessage);
        if (_reloadRequested || _config.CollectionVersion != _loadedCollectionVersion || _retiredTasks.Values.Any(x => x.DeviceId == deviceId && !x.Task.IsCompleted))
            throw new InvalidOperationException("采集配置正在重新加载，请稍后重试");
        if (!_workers.TryGetValue(deviceId, out var worker)) throw new InvalidOperationException("设备未启用或不可用");
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
        if (tag.Kind != TagKind.Physical || tag.DeviceId != deviceId) return CommandResult(GatewayCommandOutcome.NotSent, "测点不是该设备的物理测点");
        if (!_role.IsActive) return CommandResult(GatewayCommandOutcome.NotSent, _role.StandbyMessage);
        if (_reloadRequested || _config.CollectionVersion != _loadedCollectionVersion)
            return CommandResult(GatewayCommandOutcome.NotSent, "采集配置正在重新加载，请稍后重试");
        if (_retiredTasks.Values.Any(x => x.DeviceId == deviceId && !x.Task.IsCompleted))
            return CommandResult(GatewayCommandOutcome.NotSent, "旧采集 I/O 尚未退出，暂不接受控制");
        if (!_workers.TryGetValue(deviceId, out var worker))
        {
            return CommandResult(GatewayCommandOutcome.NotSent, "设备不存在或未启用");
        }

        return await worker.ExecuteWriteAsync(tag, value, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileWorkersAsync(stoppingToken);

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
            if (_reloadRequested || _config.CollectionVersion != _loadedCollectionVersion || _role.Generation != _loadedRoleGeneration)
            {
                _reloadRequested = false;
                // 只改报警规则、画面或报表时 CollectionVersion 不变，这里不做任何重建：
                // 现场设备不会因为一次无关保存而断线重连。
                if (_config.CollectionVersion != _loadedCollectionVersion || _role.Generation != _loadedRoleGeneration)
                {
                    _logger.LogInformation("采集配置发生变化，按设备差异重建采集任务");
                }
                await ReconcileWorkersAsync(stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopWorkersAsync();
        // 所有 worker 停完，通道租约已归零；这里兜底释放可能残留的共享链路（例如放弃等待的旧任务）。
        await _channels.DisposeAllAsync();
        await base.StopAsync(cancellationToken);
    }

    // 把运行中的 worker 调整到与当前配置一致：新增设备启动、删除/禁用设备停止、
    // 连接参数或测点变化的设备重建，其余设备保持原连接不动。
    private async Task ReconcileWorkersAsync(CancellationToken cancellationToken)
    {
        var roleChanged = _role.Generation != _loadedRoleGeneration;
        if (roleChanged)
        {
            _loadedRoleGeneration = _role.Generation;
            await StopWorkersAsync();
        }
        _workerCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 备机不采集：不建立任何设备连接，避免与主机同时轮询现场（部分设备只允许单连接）。
        var enabledDevices = _role.IsActive ? _config.Devices.Where(x => x.Enabled && x.GatewayId == null).ToList() : [];
        if (!_role.IsActive) { _statuses.Clear(); _statusEpochs.Clear(); }

        var tagsByDevice = _config.Tags.Where(x => x.Kind == TagKind.Physical && x.DeviceId.HasValue).GroupBy(x => x.DeviceId!.Value)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Tag>)x.ToList());
        var wanted = new Dictionary<int, (Device Device, IReadOnlyList<Tag> Tags, string Fingerprint)>();
        foreach (var device in enabledDevices)
        {
            if (tagsByDevice.TryGetValue(device.Id, out var tags) && tags.Count > 0)
                wanted[device.Id] = (device, tags, CollectionFingerprint.OfDevice(device, tags));
        }

        // 先停掉不再需要采集的设备（已删除、已禁用、已交给网关）。
        foreach (var id in _workers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            await StopWorkerAsync(id);
        }

        var waitingIds = new HashSet<int>();
        foreach (var (deviceId, target) in wanted)
        {
            var sameConfig = _workerFingerprints.TryGetValue(deviceId, out var fingerprint) && fingerprint == target.Fingerprint;
            var running = _workerTasks.TryGetValue(deviceId, out var task) && !task.IsCompleted;
            if (sameConfig && running) continue;

            if (!IsDeviceQuiescent(deviceId))
            {
                waitingIds.Add(deviceId);
                _statuses[deviceId] = new(deviceId, target.Device.Name, false, "旧采集 I/O 尚未退出，暂不启动新任务", 0, 0, target.Tags.Count, 0, 0);
                continue;
            }

            await StopWorkerAsync(deviceId);
            StartWorker(target.Device, target.Tags, target.Fingerprint, _workerCts.Token);
        }

        var activeIds = _workers.Keys.Concat(waitingIds).ToHashSet();
        foreach (var id in _statuses.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _statuses.TryRemove(id, out _);
            _statusEpochs.TryRemove(id, out _);
        }

        _loadedCollectionVersion = _config.CollectionVersion;
    }

    private void StartWorker(Device device, IReadOnlyList<Tag> tags, string fingerprint, CancellationToken serviceToken)
    {
        var epoch = Interlocked.Increment(ref _workerEpoch);
        _statusEpochs[device.Id] = epoch;
        var worker = new DeviceWorker(
            device,
            tags,
            // 绑定通道的设备取共享链路租约；未绑定通道时依旧一设备一驱动。
            _channels.Acquire(device, _driverFactory.Create),
            _planBuilder,
            _cache,
            _bus,
            _alarmSink,
            _settings.Value,
            _logger,
            // 只接受当前代次的状态：旧 worker 在 5 秒放弃后仍可能回调，
            // 否则会把新 worker 已恢复的"在线"覆盖成旧的"离线/错误"。
            status =>
            {
                if (_statusEpochs.TryGetValue(device.Id, out var current) && current == epoch) _statuses[device.Id] = status;
            },
            _config.CollectionVersion,
            _journal);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        _workers[device.Id] = worker;
        _workerCtsByDevice[device.Id] = cts;
        _workerFingerprints[device.Id] = fingerprint;
        _statuses[device.Id] = new CollectorDeviceStatus(
            device.Id, device.Name, false, "等待连接", 0, 0, tags.Count, 0, 0);
        var workerToken = cts.Token;
        _workerTasks[device.Id] = Task.Run(() => worker.RunAsync(workerToken), CancellationToken.None);
    }

    private async Task StopWorkerAsync(int deviceId)
    {
        var worker = _workers.TryRemove(deviceId, out var existing) ? existing : null;
        var task = _workerTasks.TryRemove(deviceId, out var running) ? running : null;
        _workerFingerprints.TryRemove(deviceId, out _);
        _statusEpochs.TryRemove(deviceId, out _);
        var cts = _workerCtsByDevice.TryRemove(deviceId, out var source) ? source : null;

        if (cts is not null)
        {
            try { await cts.CancelAsync(); }
            catch (ObjectDisposedException) { /* 已停过 */ }
        }

        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { /* 阻塞的驱动调用随驱动一起放弃 */ }
            catch (Exception) { /* worker 自身的异常已在内部记录 */ }
            if (!task.IsCompleted) _retiredTasks[Guid.NewGuid()] = (deviceId, task);
        }

        // 只有确定任务已退出才释放 CTS：还在跑的 worker 会用它派生 linked token。
        if ((task is null || task.IsCompleted) && cts is not null) cts.Dispose();
        if (worker is not null) await worker.DisposeAsync();
    }

    private async Task StopWorkersAsync()
    {
        foreach (var deviceId in _workers.Keys.Concat(_workerCtsByDevice.Keys).Distinct().ToList())
        {
            await StopWorkerAsync(deviceId);
        }

        if (_workerCts is not null)
        {
            try { await _workerCts.CancelAsync(); }
            catch (ObjectDisposedException) { }
            _workerCts.Dispose();
            _workerCts = null;
        }

        _workers.Clear();
        _workerTasks.Clear();
        _workerFingerprints.Clear();
        foreach (var cts in _workerCtsByDevice.Values) cts.Dispose();
        _workerCtsByDevice.Clear();
    }

    private sealed class DeviceWorker
    {
        // 采集日志写失败后的重试间隔：磁盘满通常需要人工处理，但文件被临时占用/瞬时 IO 错误应能自行恢复。
        private const int JournalRetryMs = 5000;
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
        // 诊断展示用全量布局：读取按档位拆分，但点位地址视图仍应看到设备全部测点。
        private readonly ReadPlan _plan;
        private readonly IReadOnlyList<ScanLane> _lanes;
        private readonly DeviceDiagnostics _diagnostics;
        private readonly ICollectorJournal? _journal;
        private volatile bool _online;
        private int _reconnectRequested;
        private int? _offlineAlarmId;
        private int _reconnectAttempts;
        private long _commFailures;
        private long _journalFailures;
        private bool _journalFaulted;
        private bool _targetBlocked;
        private string? _journalError;
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
            _lanes = BuildLanes(_device, _tags, _planBuilder);
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
            // 采集循环遇到未预期异常（磁盘写满、驱动库抛出）时不能让设备永久停采：
            // 原先记录日志后任务即结束，只有配置变更或重启服务才会恢复，现场就是一段静默数据黑洞。
            // 这里按指数退避重启循环，退避上限 60 秒；取消信号仍然立即生效。
            var backoffMs = 1000;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunCoreAsync(cancellationToken);
                    break; // RunCoreAsync 只在取消时正常返回
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _online = false;
                    _lastError = $"采集任务异常停止，{backoffMs / 1000} 秒后自动重启：{ex.Message}";
                    _diagnostics.Stopped(ex.Message);
                    ReportStatus();
                    _logger.LogError(ex, "设备 {Device} 的采集任务异常停止，{Delay} 秒后自动重启", _device.Name, backoffMs / 1000);
                    try { _driver.Disconnect(); }
                    catch (Exception) { /* 驱动已损坏时断开也会抛，重启路径必须继续 */ }
                    try { await Task.Delay(backoffMs, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                    backoffMs = Math.Min(backoffMs * 2, 60_000);
                }
            }
            _diagnostics.Stopped();
        }

        // 采集日志（网关缓冲/落盘）失败不能直接结束采集任务，但也绝不能把未落盘的数据发出去。
        // 返回 false 时调用方停住本轮、保持连接，退避后重试；恢复后自动接上。
        private async Task<bool> TryAppendJournalAsync(IReadOnlyList<TagValue> values, CancellationToken cancellationToken)
        {
            if (_journal is null) return true;
            try
            {
                await _journal.AppendAsync(_device.Id, values, cancellationToken);
                _journalError = null;
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _journalFailures++;
                _journalError = $"采集持久记录失败，本轮数据不发布，稍后重试（累计 {_journalFailures} 次）：{ex.Message}";
                _logger.LogWarning(ex, "设备 {Device} 的采集日志写入失败，累计 {Count} 次", _device.Name, _journalFailures);
                return false;
            }
        }

        // 一个档位 = 一个独立节拍 + 一份只含该档测点的读取计划。
        // 同一设备的档位共用一条连接，读取仍在单循环内串行，不会并发访问驱动。
        private sealed class ScanLane(int intervalMs, ReadPlan plan, IReadOnlyList<Tag> tags)
        {
            public int IntervalMs { get; } = intervalMs;
            public ReadPlan Plan { get; } = plan;
            public IReadOnlyList<Tag> Tags { get; } = tags;
            // 单调时钟上的下一个采集时点；0 表示尚未对齐（首轮、退避或日志故障后）。
            public long NextDueMs;
        }

        private static IReadOnlyList<ScanLane> BuildLanes(Device device, IReadOnlyList<Tag> tags, IReadPlanBuilder planBuilder)
        {
            if (tags.Count == 0) return [new ScanLane(ScanCadence.Effective(device), planBuilder.Build(device, tags), tags)];
            return tags.GroupBy(tag => ScanCadence.Effective(device, tag))
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var laneTags = group.ToList();
                    return new ScanLane(group.Key, planBuilder.Build(device, laneTags), laneTags);
                })
                .ToArray();
        }

        private void ResetLanes()
        {
            foreach (var lane in _lanes) lane.NextDueMs = 0;
        }

        // 未对齐的档位优先（启动/退避恢复后先把各档都读一遍），其余取最早到期。
        private ScanLane NextLane()
        {
            ScanLane? earliest = null;
            foreach (var lane in _lanes)
            {
                if (lane.NextDueMs == 0) return lane;
                if (earliest is null || lane.NextDueMs < earliest.NextDueMs) earliest = lane;
            }
            return earliest!;
        }

        private async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            var backoffMs = 0;
            // 各档位独立栅格；0 表示尚未对齐（首轮、退避或日志故障后）。
            ResetLanes();

            while (!cancellationToken.IsCancellationRequested)
            {
                var cycleStart = Stopwatch.GetTimestamp();

                if (Interlocked.Exchange(ref _reconnectRequested, 0) != 0)
                {
                    _driver.Disconnect(); _online = false; backoffMs = 0;
                    ResetLanes();
                    _diagnostics.Reconnecting(); ReportStatus();
                }

                if (!_driver.IsConnected)
                {
                    // 出站目标策略：保存时已校验，这里兜底直接改库、导入工程或下发网关配置绕过校验的情况。
                    // 被拒绝时根本不会发起连接，固定 30 秒退避即可，不必按指数退避打满。
                    if (OutboundTargetPolicy.ValidateDevice(_device, _settings) is string targetError)
                    {
                        backoffMs = 30000;
                        ResetLanes();
                        _online = false;
                        _lastError = targetError;
                        if (!_targetBlocked)
                        {
                            _targetBlocked = true;
                            _commFailures++;
                            _diagnostics.ConnectionFailed(targetError, CollectorErrorKind.Configuration, backoffMs);
                            await MarkOfflineAsync(targetError, cancellationToken);
                        }
                        ReportStatus();
                        await ProcessCommandsAsync(cancellationToken);
                        await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                        continue;
                    }
                    _targetBlocked = false;

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
                        ResetLanes();
                        _diagnostics.ConnectionFailed(ex.Message, CollectorErrorClassifier.Classify(ex), backoffMs);
                        await MarkOfflineAsync(ex.Message, cancellationToken);
                        _reconnectAttempts++;
                        ReportStatus();
                        await ProcessCommandsAsync(cancellationToken);
                        await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                        continue;
                    }
                }

                // 选出最早到期的档位。未对齐的档位（首轮/退避恢复）立即执行，
                // 因此启动时各档会依次读一遍，而稳态下每档按自己的周期独立运行。
                var lane = NextLane();
                var dueNowMs = Environment.TickCount64;
                lane.NextDueMs = PollSchedule.Align(lane.NextDueMs, dueNowMs, lane.IntervalMs);
                if (lane.NextDueMs > dueNowMs)
                {
                    // 等待期间仍会处理写命令、重连与即时读取；被唤醒后重新评估最早到期的档位。
                    await WaitForWorkOrDelayAsync(PollSchedule.DelayMs(lane.NextDueMs, dueNowMs, lane.IntervalMs), cancellationToken);
                    await ProcessCommandsAsync(cancellationToken);
                    continue;
                }

                var readStart = Stopwatch.GetTimestamp();
                _diagnostics.Polling();
                try
                {
                    var rawValues = await _driver.ReadAsync(_device, lane.Plan, cancellationToken);
                    var readDurationMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                    var ts = ScadaTime.NowMs();
                    var rawMap = rawValues.ToDictionary(x => x.TagId);
                    var updates = new List<TagValue>(lane.Tags.Count);

                    foreach (var tag in lane.Tags)
                    {
                        var raw = rawMap.GetValueOrDefault(tag.Id) ?? new RawTagValue(tag.Id, 0, TagQuality.Bad, null, "本次读取未返回该测点");

                        var value = raw.Quality == TagQuality.Good
                            ? tag.DataType == TagDataType.Bool ? raw.Value : raw.Value * tag.Scale + tag.Offset
                            : _cache.Get(tag.Id)?.Value ?? 0;
                        var quality = raw.Quality;
                        var pointError = raw.Error;
                        if (!double.IsFinite(value)) { value = _cache.Get(tag.Id)?.Value ?? 0; quality = TagQuality.Bad; pointError = "工程量换算结果不是有限数值"; }
                        var tagValue = new TagValue(tag.Id, value, quality, ts, raw.SourceTs, pointError, tag.CalculationRevision);
                        updates.Add(tagValue);
                    }
                    if (!await TryAppendJournalAsync(updates, cancellationToken))
                    {
                        // 未落盘的批次既不能发布也不能写缓存：网关断线重传依赖本地日志，
                        // 一旦发布出去就再也补不回来。这里只停住本轮并保持连接，下一轮继续尝试，
                        // 日志恢复后自动接上（原实现直接结束任务，磁盘满一次就要重启服务）。
                        await MarkOfflineAsync(_journalError, cancellationToken);
                        _online = false;
                        _lastError = _journalError;
                        if (!_journalFaulted)
                        {
                            _journalFaulted = true;
                            _diagnostics.Stopped(_journalError);
                        }
                        ReportStatus();
                        await ProcessCommandsAsync(cancellationToken);
                        ResetLanes();
                        await WaitForWorkOrDelayAsync(Math.Max(lane.IntervalMs, JournalRetryMs), cancellationToken);
                        continue;
                    }
                    _journalFaulted = false;
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
                    // 多档位下，未采集到的档位在缓存里还没有值（首次启动时慢档尚未读过），
                    // 这里必须排除 null，否则会把"还没采过"当成坏点并空引用。
                    var pointErrorText = string.Join("；", _tags.Select(x => _cache.Get(x.Id)).Where(x => x is not null && x.Quality != TagQuality.Good)
                        .Select(x => $"#{x!.TagId} {x.Error ?? x.Quality.ToString()}").Take(3));
                    // 采集日志的错误优先展示：它意味着这一段数据没进历史，比个别坏点更需要处理。
                    _lastError = string.Join("；", new[] { _journalError, pointErrorText }.Where(x => !string.IsNullOrEmpty(x)));
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
                    ResetLanes();
                    _diagnostics.PollFailed(Stopwatch.GetElapsedTime(readStart).TotalMilliseconds, ex.Message, CollectorErrorClassifier.Classify(ex), Math.Max(lane.IntervalMs, backoffMs));
                    await MarkOfflineAsync(ex.Message, cancellationToken);
                    _driver.Disconnect();
                    ReportStatus();
                    // 档位已全部重置，必须显式退避，否则下一轮会立刻重连重试。
                    await ProcessCommandsAsync(cancellationToken);
                    await WaitForWorkOrDelayAsync(backoffMs, cancellationToken);
                    continue;
                }

                await ProcessCommandsAsync(cancellationToken);
                // 推进该档位的固定栅格：本档采集耗时由下一轮的 Align 扣减，
                // 落后超过一个周期时重新对齐，不连续补帧。
                lane.NextDueMs = PollSchedule.Advance(lane.NextDueMs, lane.IntervalMs);
            }
        }

        public async Task<IReadOnlyList<TagValue>> ExecuteReadAsync(IReadOnlyList<int> tagIds, CancellationToken cancellationToken)
        {
            if (tagIds.Count is < 1 or > 256 || tagIds.Any(id => !_tagMap.ContainsKey(id)))
                throw new InvalidOperationException("读取测点必须属于该设备，且一次 1～256 个");
            if (!_driver.IsConnected || !_online || Volatile.Read(ref _reconnectRequested) != 0)
                throw new IOException("设备离线或正在重连");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
            var completion = new TaskCompletionSource<IReadOnlyList<TagValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tags = tagIds.Distinct().Select(id => _tagMap[id]).ToArray();
            if (!_reads.Writer.TryWrite(new(tags, completion, deadline.Token))) throw new IOException("读取队列不可用");
            _wake.Writer.TryWrite(true);
            return await completion.Task.WaitAsync(deadline.Token);
        }

        public async Task<CommandExecutionResult> ExecuteWriteAsync(Tag tag, double value, CancellationToken cancellationToken)
        {
            if (!_tagMap.ContainsKey(tag.Id))
            {
                _diagnostics.CommandNotSent(tag.Id, "测点不属于该设备");
                return CommandResult(GatewayCommandOutcome.NotSent, "测点不属于该设备");
            }

            if (!_driver.IsConnected || !_online || Volatile.Read(ref _reconnectRequested) != 0)
            {
                _diagnostics.CommandNotSent(tag.Id, "设备离线或等待重连，无法下发");
                return CommandResult(GatewayCommandOutcome.NotSent, "设备离线或等待重连，无法下发");
            }

            var tcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Max(_settings.CommandTimeoutMs, 1));
            var command = new WriteCommand(tag, value, tcs, deadline.Token);
            if (!_commands.Writer.TryWrite(command))
            {
                _diagnostics.CommandNotSent(tag.Id, "命令队列不可用");
                return CommandResult(GatewayCommandOutcome.NotSent, "命令队列不可用");
            }
            _wake.Writer.TryWrite(true);

            try
            {
                return await tcs.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return CommandResult(command.Started ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent, command.Started
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
                    command.Completion.TrySetResult(CommandResult(GatewayCommandOutcome.NotSent, "命令已取消，未下发"));
                    continue;
                }
                if (!_driver.IsConnected || !_online)
                {
                    _diagnostics.CommandNotSent(command.Tag.Id, "设备离线，无法下发");
                    command.Completion.TrySetResult(CommandResult(GatewayCommandOutcome.NotSent, "设备离线，无法下发"));
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
                    command.Completion.TrySetResult(CommandResult(GatewayCommandOutcome.Confirmed, "写入成功", engineering));
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
                    command.Completion.TrySetResult(CommandResult(GatewayCommandOutcome.Unknown, "执行结果未知，请检查设备反馈；不会自动重发"));
                }
                catch (Exception ex)
                {
                    _diagnostics.CommandCompleted(command.Tag.Id, false, ex.Message);
                    _lastError = ex.Message;
                    if (!_driver.IsConnected) await MarkOfflineAsync(ex.Message, cancellationToken);
                    command.Completion.TrySetResult(CommandResult(CommandFailure.Read(ex), ex.Message));
                }
            }
            await ProcessSideReadsAsync(cancellationToken);
        }

        // 即时读取和探针只把结果交还给请求方：不写缓存、不写采集日志、不改最近采样，也不结束本轮等待。
        private async Task ProcessSideReadsAsync(CancellationToken cancellationToken)
        {
            while (_reads.Reader.TryRead(out var read))
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(read.Token, cancellationToken);
                    linked.Token.ThrowIfCancellationRequested();
                    if (!_driver.IsConnected || !_online) throw new IOException("设备当前离线");
                    var raw = await _driver.ReadAsync(_device, _planBuilder.Build(_device, read.Tags), linked.Token);
                    var map = raw.ToDictionary(value => value.TagId);
                    var now = ScadaTime.NowMs();
                    var values = read.Tags.Select(tag =>
                    {
                        if (!map.TryGetValue(tag.Id, out var value)) throw new IOException($"设备未返回测点 #{tag.Id}");
                        var engineering = tag.DataType == TagDataType.Bool ? value.Value : value.Value * tag.Scale + tag.Offset;
                        return new TagValue(tag.Id, engineering, double.IsFinite(engineering) ? value.Quality : TagQuality.Bad, now, value.SourceTs, value.Error, tag.CalculationRevision);
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
                // 探针失败只回传请求方；但驱动在 ReadAsync 异常时会自行 IsConnected=false，是否重连由采集循环决定。
                catch (OperationCanceledException) { probe.Completion.TrySetResult(new CollectorProbeResult(true, null, "测试读取超时")); }
                catch (Exception ex) { probe.Completion.TrySetResult(new CollectorProbeResult(true, null, ex.Message)); }
            }
        }

        private async Task<double> WriteAndVerifyAsync(Tag tag, double value, CancellationToken cancellationToken)
        {
            if (!CommandValuePolicy.TryGetRawValue(tag, value, out var expectedRaw, out var error))
                throw CommandFailure.Tag(new InvalidOperationException(error), GatewayCommandOutcome.NotSent);
            await _driver.WriteAsync(_device, tag, expectedRaw, cancellationToken);
            var verifyPlan = _planBuilder.Build(_device, new[] { tag });
            var readBack = await _driver.ReadAsync(_device, verifyPlan, cancellationToken);
            var feedback = readBack.FirstOrDefault(x => x.TagId == tag.Id) ?? throw CommandFailure.Tag(new IOException("回读失败"), GatewayCommandOutcome.Unknown);
            if (feedback.Quality != TagQuality.Good || !double.IsFinite(feedback.Value))
                throw CommandFailure.Tag(new IOException($"写入已发送，回读质量无效，结果未知：{feedback.Error ?? feedback.Quality.ToString()}"), GatewayCommandOutcome.Unknown);
            var raw = feedback.Value;

            var engineering = tag.DataType == TagDataType.Bool
                ? raw
                : raw * tag.Scale + tag.Offset;
            if (!double.IsFinite(engineering)) throw CommandFailure.Tag(new IOException("写入已发送，回读工程量无效，结果未知"), GatewayCommandOutcome.Unknown);

            var ts = ScadaTime.NowMs();
            var tagValue = new TagValue(tag.Id, engineering, TagQuality.Good, ts, feedback.SourceTs, DefinitionRevision: tag.CalculationRevision);
            if (_journal is not null)
            {
                try { await _journal.AppendAsync(_device.Id, new[] { tagValue }, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw CommandFailure.Tag(new IOException($"写入已发送，回读已取得，但持久记录失败；请检查现场反馈：{ex.Message}", ex), GatewayCommandOutcome.Unknown); }
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
                throw CommandFailure.Tag(new IOException($"回读不匹配：目标 {value:0.######}，实际 {engineering:0.######}，请检查设备"), GatewayCommandOutcome.Failed);
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
            var updates = _tags.Select(tag => new TagValue(tag.Id, _cache.Get(tag.Id)?.Value ?? 0, TagQuality.CommFailure, ts, _cache.Get(tag.Id)?.SourceTs, error, tag.CalculationRevision)).ToArray();
            await TryAppendJournalAsync(updates, cancellationToken);
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
            if (_journalError is not null && (_lastError is null || !_lastError.Contains(_journalError, StringComparison.Ordinal)))
            {
                _lastError = _lastError is null ? _journalError : $"{_lastError}；{_journalError}";
            }

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
            var deadline = Environment.TickCount64 + Math.Max(delayMs, 10);
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_wake.Reader.TryRead(out _)) { }
                // 写入和重连仍提前结束等待。即时读取在等待内完成，到点才开始下一轮采集。
                if (Volatile.Read(ref _reconnectRequested) != 0 || _commands.Reader.TryPeek(out _)) return;
                if (_reads.Reader.TryPeek(out _) || _probes.Reader.TryPeek(out _))
                {
                    await ProcessSideReadsAsync(cancellationToken);
                    continue;
                }
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromMilliseconds(remaining));
                try { await _wake.Reader.ReadAsync(linked.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return; }
            }
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
            while (_reads.Reader.TryRead(out var read)) read.Completion.TrySetException(new IOException("采集已停止"));
            _probes.Writer.TryComplete();
            while (_probes.Reader.TryRead(out var probe)) probe.Completion.TrySetResult(new CollectorProbeResult(true, null, "采集已停止"));
            _commands.Writer.TryComplete();
            while (_commands.Reader.TryRead(out var command))
            {
                _diagnostics.CommandNotSent(command.Tag.Id, "采集任务已停止，未下发", expired: true);
                command.Completion.TrySetResult(CommandResult(GatewayCommandOutcome.NotSent, "设备配置变更或服务停止，未执行的命令已取消"));
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
