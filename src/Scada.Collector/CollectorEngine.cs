using System.Collections.Concurrent;
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
    private const int TickMs = 1000;

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
    // 绑定通道的设备共用一条物理链路；未绑定通道的设备仍然一设备一连接。
    private readonly ChannelRegistry _channels;
    private readonly ConcurrentDictionary<int, Task> _workerTasks = new();
    // 停不下来的旧任务：超时先登记，仍不退出则强制关链路并放弃，避免设备永久停采。
    private readonly ConcurrentDictionary<Guid, RetiredWorker> _retiredTasks = new();
    // 每台设备一个 CTS：增量重建时只停掉配置真正变化的那几台，其余设备保持连接。
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _workerCtsByDevice = new();
    // 设备 + 物理测点的采集指纹，用于判断"配置改了但连接参数没变"，避免无谓断线。
    private readonly ConcurrentDictionary<int, string> _workerFingerprints = new();
    // 启动失败的设备：诊断里直接显示原因，而不是每轮静默重试。
    private readonly ConcurrentDictionary<int, string> _startFailures = new();
    // 正在停机的设备。StopWorkerAsync 会先把任务从 _workerTasks 摘掉再等它退出，
    // 这期间不能报"已静止"：否则停机编排会以为旧 I/O 没了，提前释放安装租约或启动新任务。
    private readonly ConcurrentDictionary<int, byte> _stopping = new();
    private CancellationTokenSource? _workerCts;
    private int _loadedCollectionVersion = -1;
    private volatile bool _reloadRequested;
    private int _reconcileFailures;
    private long _workerEpoch;

    private sealed record RetiredWorker(int DeviceId, Task Task, long SinceMs, DeviceWorker Worker);

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
        _channels = new ChannelRegistry(logger, Math.Max(settings.Value.CollectorBusDisposeGraceMs, 1));
    }

    public IReadOnlyList<CollectorDeviceStatus> GetStatuses() =>
        _statuses.Values.OrderBy(x => x.DeviceId).ToList();

    public void RequestReload() => _reloadRequested = true;

    public bool IsDeviceQuiescent(int deviceId) => !_stopping.ContainsKey(deviceId)
        && (!_workerTasks.TryGetValue(deviceId, out var task) || task.IsCompleted)
        && !_retiredTasks.Values.Any(x => x.DeviceId == deviceId && !x.Task.IsCompleted);

    public CollectorDeviceDiagnostics? GetDiagnostics(int deviceId)
    {
        if (!_config.TryGetDevice(deviceId, out var device)) return null;
        // 按设备索引取测点：诊断会被 UI 与网关逐设备轮询，不能每次都全表扫描。
        var tags = _config.TagsOfDevice(deviceId);
        var pending = _reloadRequested || _config.CollectionVersion != _loadedCollectionVersion;
        if (device.Enabled && tags.Count > 0 && _workers.TryGetValue(deviceId, out var worker)) return worker.Diagnostics(pending);
        var idle = new DeviceDiagnostics(device, tags, _planBuilder.Build(device, tags), _config.CollectionVersion).Snapshot(0, pending);
        // 启动失败要显式暴露，否则面板会一直显示"等待连接"，运维看不到真正的原因。
        if (_startFailures.TryGetValue(deviceId, out var startError))
            return idle with { State = "Faulted", CurrentError = startError };
        return !IsDeviceQuiescent(deviceId) ? idle with { State = "Stopping", CurrentError = "旧采集 I/O 尚未退出，暂不启动新任务" } : idle;
    }

    public bool RequestReconnect(int deviceId)
    {
        if (!_config.TryGetDevice(deviceId, out var device) || !device.Enabled || _config.TagsOfDevice(deviceId).Count == 0) return false;
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
        if (_reloadRequested || _config.CollectionVersion != _loadedCollectionVersion)
            throw new InvalidOperationException("采集配置正在重新加载，请稍后重试");
        if (HasStuckIo(deviceId))
            throw new InvalidOperationException("旧采集 I/O 尚未退出，暂不接受读取请求");
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
        if (tag.Kind != TagKind.Physical || tag.DeviceId != deviceId) return CommandResults.Of(GatewayCommandOutcome.NotSent, "测点不是该设备的物理测点");
        if (!_role.IsActive) return CommandResults.Of(GatewayCommandOutcome.NotSent, _role.StandbyMessage);
        if (_reloadRequested || _config.CollectionVersion != _loadedCollectionVersion)
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "采集配置正在重新加载，请稍后重试");
        if (HasStuckIo(deviceId))
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "旧采集 I/O 尚未退出，暂不接受控制");
        if (!_workers.TryGetValue(deviceId, out var worker))
        {
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "设备不存在或未启用");
        }

        return await worker.ExecuteWriteAsync(tag, value, cancellationToken);
    }

    public async Task<CommandExecutionResult> WriteTextAsync(
        int deviceId,
        Tag tag,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (tag.Kind != TagKind.Physical || tag.DeviceId != deviceId) return CommandResults.Of(GatewayCommandOutcome.NotSent, "测点不是该设备的物理测点");
        if (tag.DataType != TagDataType.String) return CommandResults.Of(GatewayCommandOutcome.NotSent, "测点不是字符串类型");
        if (!_role.IsActive) return CommandResults.Of(GatewayCommandOutcome.NotSent, _role.StandbyMessage);
        if (_reloadRequested || _config.CollectionVersion != _loadedCollectionVersion)
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "采集配置正在重新加载，请稍后重试");
        if (HasStuckIo(deviceId))
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "旧采集 I/O 尚未退出，暂不接受控制");
        if (!_workers.TryGetValue(deviceId, out var worker))
        {
            return CommandResults.Of(GatewayCommandOutcome.NotSent, "设备不存在或未启用");
        }

        return await worker.ExecuteWriteTextAsync(tag, text, cancellationToken);
    }

    private bool HasStuckIo(int deviceId) =>
        _retiredTasks.Values.Any(x => x.DeviceId == deviceId && !x.Task.IsCompleted);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // 阻塞式驱动事务按线程计价：设备多时预热门槛可避免线程注入延迟（约 1 个/500ms）拖慢首轮采集。
        if (_settings.Value.CollectorMinWorkerThreads is > 0 and var minThreads)
        {
            ThreadPool.GetMinThreads(out var workers, out var io);
            if (workers < minThreads) ThreadPool.SetMinThreads(minThreads, io);
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 首次重建也要保护：配置库里一台坏设备（协议不支持、插件构造抛异常、测点重复）
        // 曾让异常逃出 BackgroundService，按默认 StopHost 把整个网关/内嵌服务一起停掉。
        await ReconcileGuardedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _liveness?.Beat("collector");
            try
            {
                await Task.Delay(TickMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var retiredChanged = SweepRetiredWorkers();
            if (!retiredChanged && !_reloadRequested && _config.CollectionVersion == _loadedCollectionVersion && _role.Generation == _loadedRoleGeneration)
                continue;

            if (_config.CollectionVersion != _loadedCollectionVersion || _role.Generation != _loadedRoleGeneration)
            {
                // 只改报警规则、画面或报表时 CollectionVersion 不变，这里不做任何重建：
                // 现场设备不会因为一次无关保存而断线重连。
                _logger.LogInformation("采集配置发生变化，按设备差异重建采集任务");
            }
            await ReconcileGuardedAsync(stoppingToken);
        }
    }

    private async Task ReconcileGuardedAsync(CancellationToken cancellationToken)
    {
        _reloadRequested = false;
        try
        {
            await ReconcileWorkersAsync(cancellationToken);
            _reconcileFailures = 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 配置枚举/角色切换这类整体失败：记录并退避重试，绝不让异常结束采集服务。
            _reconcileFailures++;
            if (_reconcileFailures == 1 || _reconcileFailures % 60 == 0)
                _logger.LogError(ex, "采集任务重建失败（连续 {Count} 次），稍后重试", _reconcileFailures);
            else
                _logger.LogDebug(ex, "采集任务重建失败（连续 {Count} 次）", _reconcileFailures);
            _reloadRequested = true;
            try { await Task.Delay(5000, cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopWorkersAsync();
        // 所有 worker 停完，通道租约已归零；这里兜底释放可能残留的共享链路（例如放弃等待的旧任务）。
        await _channels.DisposeAllAsync();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// 扫一遍"停不下来的旧任务"：已退出的移除并触发一次重建；超过宽限仍未退出的强制关链路并放弃。
    /// 返回是否需要重建采集任务。
    /// </summary>
    private bool SweepRetiredWorkers()
    {
        var reload = false;
        var graceSeconds = _settings.Value.CollectorStuckIoGraceSeconds;
        var now = ScadaTime.NowMs();
        foreach (var (key, retired) in _retiredTasks)
        {
            if (retired.Task.IsCompleted)
            {
                if (_retiredTasks.TryRemove(key, out _)) reload = true;
                continue;
            }
            // 0 表示永不放弃：保持旧行为，只在诊断里提示，不自动重启。
            if (graceSeconds <= 0 || now - retired.SinceMs < graceSeconds * 1000L) continue;
            if (!_retiredTasks.TryRemove(key, out var abandoned)) continue;
            AbandonStuckWorker(abandoned, graceSeconds);
            reload = true;
        }
        return reload;
    }

    private void AbandonStuckWorker(RetiredWorker retired, int graceSeconds)
    {
        var name = _config.TryGetDevice(retired.DeviceId, out var device) ? device.Name : $"#{retired.DeviceId}";
        var message = $"旧采集 I/O 超过 {graceSeconds} 秒未退出，已强制断开连接并放弃该任务；设备将重新建链";
        _logger.LogError("设备 {Device} 的旧采集任务 {Grace} 秒内未退出（驱动可能卡在阻塞读上），强制断开连接并放弃", name, graceSeconds);

        // 最后手段：关掉套接字/串口让卡住的读返回；共享链路同样强拆，否则同通道设备一起陪葬。
        retired.Worker.ForceClose();
        if (device?.ChannelId is int channelId) _channels.ForceClose(channelId);

        _startFailures[retired.DeviceId] = message;
        _statuses[retired.DeviceId] = new(retired.DeviceId, name, false, message, 0, 0, 0, 0, 0);
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

        var wanted = new Dictionary<int, (Device Device, IReadOnlyList<Tag> Tags, string Fingerprint)>();
        foreach (var device in enabledDevices)
        {
            var tags = _config.TagsOfDevice(device.Id).Where(x => x.Kind == TagKind.Physical).ToList();
            if (tags.Count > 0) wanted[device.Id] = (device, tags, CollectionFingerprint.OfDevice(device, tags));
        }

        // 先停掉不再需要采集的设备（已删除、已禁用、已交给网关）。
        foreach (var id in _workers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            await StopWorkerAsync(id);
        }

        var waitingIds = new HashSet<int>();
        // 启动失败的设备要留在 active 集合里：否则下面的状态清理会把它刚写进去的失败原因一起抹掉，
        // 面板又回到"等待连接"，运维永远看不到真正的原因。
        var failedIds = new HashSet<int>();
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
            try
            {
                await StartWorkerAsync(target.Device, target.Tags, target.Fingerprint, _workerCts.Token);
                _startFailures.TryRemove(deviceId, out _);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 一台设备的驱动/插件问题不能拖垮其余设备，也不能让异常逃出宿主。
                // 这里落到 Faulted 状态：面板显示原因，网关据此暂停对这台设备下发命令。
                var message = $"采集任务启动失败：{ex.Message}";
                _startFailures[deviceId] = message;
                failedIds.Add(deviceId);
                _statuses[deviceId] = new(deviceId, target.Device.Name, false, message, 0, 0, target.Tags.Count, 0, 0);
                _logger.LogError(ex, "设备 {Device} 的采集任务启动失败，其余设备继续运行", target.Device.Name);
            }
        }

        var activeIds = _workers.Keys.Concat(waitingIds).Concat(failedIds).ToHashSet();
        foreach (var id in _statuses.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _statuses.TryRemove(id, out _);
            _statusEpochs.TryRemove(id, out _);
            _startFailures.TryRemove(id, out _);
        }

        _loadedCollectionVersion = _config.CollectionVersion;
    }

    private async Task StartWorkerAsync(Device device, IReadOnlyList<Tag> tags, string fingerprint, CancellationToken serviceToken)
    {
        var epoch = Interlocked.Increment(ref _workerEpoch);
        _statusEpochs[device.Id] = epoch;
        // 绑定通道的设备取共享链路租约；未绑定通道时依旧一设备一驱动。
        var driver = _channels.Acquire(device, _driverFactory.Create);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        DeviceWorker worker;
        try
        {
            worker = new DeviceWorker(
                new DeviceWorkerContext(
                    device,
                    tags,
                    _cache,
                    _bus,
                    _alarmSink,
                    _settings.Value,
                    _logger,
                    _config.CollectionVersion,
                    _journal),
                driver,
                _planBuilder,
                // 只接受当前代次的状态：旧 worker 在 5 秒放弃后仍可能回调，
                // 否则会把新 worker 已恢复的"在线"覆盖成旧的"离线/错误"。
                status =>
                {
                    if (_statusEpochs.TryGetValue(device.Id, out var current) && current == epoch) _statuses[device.Id] = status;
                });
        }
        catch
        {
            // 构造失败（重复测点、地址非法）时必须归还租约，否则端口会被一条没人用的连接一直占着。
            cts.Dispose();
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }

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
        _stopping[deviceId] = 0;
        try
        {
            await StopWorkerCoreAsync(deviceId);
        }
        finally
        {
            _stopping.TryRemove(deviceId, out _);
        }
    }

    private async Task StopWorkerCoreAsync(int deviceId)
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
            var graceMs = Math.Max(_settings.Value.CollectorWorkerStopGraceMs, 1);
            try { await task.WaitAsync(TimeSpan.FromMilliseconds(graceMs)); }
            catch (TimeoutException) { /* 阻塞的驱动调用留给放弃名单处理 */ }
            catch (Exception) { /* worker 自身的异常已在内部记录 */ }
            if (!task.IsCompleted && worker is not null)
            {
                // 卡住的调用可能永远不返回：登记后由 SweepRetiredWorkers 定时升级，不能让设备静默停采。
                _retiredTasks[Guid.NewGuid()] = new RetiredWorker(deviceId, task, ScadaTime.NowMs(), worker);
                worker = null;
            }
        }

        // 只有确定任务已退出才释放 CTS：还在跑的 worker 会用它派生 linked token。
        if ((task is null || task.IsCompleted) && cts is not null) cts.Dispose();
        if (worker is not null) await DisposeWorkerAsync(worker, deviceId);
    }

    private async Task DisposeWorkerAsync(DeviceWorker worker, int deviceId)
    {
        // 驱动释放也可能卡（Disconnect 里等内核/串口）：限时，超时只记日志，不拖住停机。
        var graceMs = Math.Max(_settings.Value.CollectorWorkerStopGraceMs, 1);
        try { await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(graceMs)); }
        catch (TimeoutException) { _logger.LogWarning("设备 #{DeviceId} 的驱动释放超过 {Grace}ms，已放弃等待", deviceId, graceMs); }
        catch (Exception ex) { _logger.LogWarning(ex, "设备 #{DeviceId} 的驱动释放失败", deviceId); }
    }

    private async Task StopWorkersAsync()
    {
        var ids = _workers.Keys.Concat(_workerCtsByDevice.Keys).Distinct().ToList();
        // 串行停 100 台设备最坏要等 100 × 5 秒，必然超过宿主停机预算：并行执行，每台各自限时。
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (deviceId, _) => await StopWorkerAsync(deviceId));

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
}
