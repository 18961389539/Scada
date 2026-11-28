using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Acquisition;
using Scada.Core;
using Scada.Core.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Acquisition;

public sealed class DeviceAcquisitionPathService(
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    IConfigSnapshot config,
    IRealtimeCache cache,
    ILogger<DeviceAcquisitionPathService> logger,
    INodeRole? role = null,
    IDeviceDriverFactory? drivers = null,
    IReadPlanBuilder? plans = null,
    IServiceProvider? services = null,
    IOptions<ScadaSettings>? settings = null) : BackgroundService, IDeviceAcquisitionPaths
{
    private readonly INodeRole _role = role ?? new StandaloneNodeRole();
    private readonly IOptions<ScadaSettings>? _settings = settings;
    private readonly ConcurrentDictionary<int, AcquisitionPathKind> _active = new();
    private readonly ConcurrentDictionary<int, PrimaryProbe> _probes = new();
    private readonly ConcurrentDictionary<int, byte> _probing = new();
    private readonly SemaphoreSlim _probeGate = new(2, 2);
    private readonly object _loadLock = new();
    private long _loadedGeneration = long.MinValue;
    private int _version;
    public int Version => Volatile.Read(ref _version);
    public event Action<int, AcquisitionPathKind, string>? PathChanged;
    private IRealtimeBus? Bus => services?.GetService(typeof(IRealtimeBus)) as IRealtimeBus;

    public AcquisitionPathKind Current(int deviceId)
    {
        EnsureFresh();
        return _active.TryGetValue(deviceId, out var path) ? path : AcquisitionPathKind.Primary;
    }

    public bool HasStandby(Device device) => DeviceAcquisitionRules.HasStandby(device);

    public DeviceAcquisitionRoute Resolve(Device device)
    {
        if (!DeviceAcquisitionRules.HasStandby(device) || Current(device.Id) != AcquisitionPathKind.Standby)
            return DeviceAcquisitionRules.PrimaryOf(device);
        return DeviceAcquisitionRules.StandbyOf(device);
    }

    public Device Apply(Device device) => DeviceAcquisitionRules.Apply(device, Resolve(device));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (_loadedGeneration != _role.Generation)
                await LoadAsync(stoppingToken);
            if (_role.IsActive)
                await TickAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var rows = await runtime.DeviceAcquisitionStates.AsNoTracking().ToListAsync(ct);
            List<(int DeviceId, AcquisitionPathKind Path, string Reason)> raised;
            lock (_loadLock)
            {
                raised = ApplyRows(rows);
                _loadedGeneration = _role.Generation;
            }
            NotifyPathChanges(raised);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "加载设备采集路径失败");
        }
    }

    // 写值路由在升主后可能立刻到来，不能等下一轮 Tick。角色任期变化时同步从已同步的运行库重载。
    internal void EnsureFresh()
    {
        var generation = _role.Generation;
        if (generation == _loadedGeneration) return;
        List<(int DeviceId, AcquisitionPathKind Path, string Reason)> raised = [];
        lock (_loadLock)
        {
            if (generation == _loadedGeneration) return;
            try
            {
                using var runtime = runtimeFactory.CreateDbContext();
                raised = ApplyRows(runtime.DeviceAcquisitionStates.AsNoTracking().ToList());
                _loadedGeneration = generation;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "角色切换后重载设备采集路径失败");
            }
        }
        NotifyPathChanges(raised);
    }

    private List<(int DeviceId, AcquisitionPathKind Path, string Reason)> ApplyRows(IReadOnlyList<DeviceAcquisitionState> rows)
    {
        var raised = new List<(int DeviceId, AcquisitionPathKind Path, string Reason)>();
        var keep = new HashSet<int>();
        foreach (var row in rows)
        {
            var had = _active.TryGetValue(row.DeviceId, out var previous);
            _active[row.DeviceId] = row.ActivePath;
            keep.Add(row.DeviceId);
            if (had && previous != row.ActivePath)
                raised.Add((row.DeviceId, row.ActivePath, row.Reason ?? "采集路径已与运行库对账"));
        }
        foreach (var key in _active.Keys)
            if (!keep.Contains(key)) _active.TryRemove(key, out _);
        Interlocked.Increment(ref _version);
        return raised;
    }

    private void NotifyPathChanges(IEnumerable<(int DeviceId, AcquisitionPathKind Path, string Reason)> changes)
    {
        foreach (var change in changes)
        {
            MarkOfficialUncertain(change.DeviceId, change.Reason);
            PathChanged?.Invoke(change.DeviceId, change.Path, change.Reason);
        }
    }

    private void MarkOfficialUncertain(int deviceId, string reason)
    {
        var now = ScadaTime.NowMs();
        foreach (var tag in config.TagsOfDevice(deviceId))
        {
            var previous = cache.Get(tag.Id);
            var value = new TagValue(tag.Id, previous?.Value ?? 0, TagQuality.Uncertain, now, previous?.SourceTs, reason, tag.CalculationRevision, previous?.Text, previous?.Simulated ?? false);
            cache.Set(value);
            Bus?.Publish(value);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        try
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var raised = new List<(int DeviceId, AcquisitionPathKind Path, string Reason)>();
            var localCollectionEnabled = _settings?.Value.LocalCollectionEnabled ?? true;
            var enabled = config.Devices.Where(x => x.Enabled).ToList();
            foreach (var group in DeviceAcquisitionRules.SharedBusGroups(enabled))
                await TickGroupAsync(runtime, group, raised, now, localCollectionEnabled, ct);
            await runtime.SaveChangesAsync(ct);
            Interlocked.Increment(ref _version);
            NotifyPathChanges(raised);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "评估设备采集路径失败");
        }
    }

    public static (AcquisitionPathKind Path, string Reason)? Evaluate(
        Device device, DeviceAcquisitionState state, bool activeHealthy, bool primaryHealthy, long now,
        bool localCollectionEnabled = true)
    {
        if (!DeviceAcquisitionRules.HasStandby(device)) return null;
        var failover = Math.Clamp(device.FailoverHoldMs <= 0 ? DeviceAcquisitionRules.DefaultFailoverHoldMs : device.FailoverHoldMs,
            DeviceAcquisitionRules.MinHoldMs, DeviceAcquisitionRules.MaxHoldMs);
        var switchback = Math.Max(failover, device.SwitchbackHoldMs <= 0 ? DeviceAcquisitionRules.DefaultSwitchbackHoldMs : device.SwitchbackHoldMs);

        if (activeHealthy) state.ActiveUnhealthySince = null;
        else state.ActiveUnhealthySince ??= now;

        if (state.ActivePath == AcquisitionPathKind.Standby && primaryHealthy) state.PrimaryHealthySince ??= now;
        else if (!primaryHealthy) state.PrimaryHealthySince = null;

        (AcquisitionPathKind Path, string Reason)? next = null;
        if (state.ActivePath == AcquisitionPathKind.Primary
            && state.ActiveUnhealthySince is long badSince
            && now - badSince >= failover)
            next = (AcquisitionPathKind.Standby, $"主路径持续离线或坏品质超过 {failover} 毫秒，切到备路径");
        else if (state.ActivePath == AcquisitionPathKind.Standby
            && !state.ManualLock
            && state.PrimaryHealthySince is long goodSince
            && now - goodSince >= switchback)
            next = (AcquisitionPathKind.Primary, $"主路径恢复并保持 {switchback} 毫秒，自动切回");

        if (next is not null && !localCollectionEnabled && DeviceAcquisitionRules.RouteIsLocal(device, next.Value.Path))
            return null;
        return next;
    }

    public async Task SwitchManualAsync(int deviceId, AcquisitionPathKind path, string reason, string user, CancellationToken ct)
    {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        if (!config.TryGetDevice(deviceId, out var device))
            throw new InvalidOperationException("设备不存在");
        if (path == AcquisitionPathKind.Standby && !DeviceAcquisitionRules.HasStandby(device))
            throw new InvalidOperationException("该设备未配置采集备路径");
        if (device.SimulationEnabled)
            throw new InvalidOperationException("仿真设备不能切换采集路径");
        if (!_role.IsActive)
            throw new InvalidOperationException("备机不能切换采集路径");
        var localCollectionEnabled = _settings?.Value.LocalCollectionEnabled ?? true;
        if (path == AcquisitionPathKind.Standby && !localCollectionEnabled && DeviceAcquisitionRules.RouteIsLocal(device, path))
            throw new InvalidOperationException("本机采集已关闭，不能切到本机备路径");
        var peers = BusPeers(device);
        if (peers.Any(peer => peer.Id != device.Id && !DeviceAcquisitionRules.SameStandbyRoute(device, peer)))
            throw new InvalidOperationException(DeviceAcquisitionRules.SharedBusStandbyMismatch);
        var now = ScadaTime.NowMs();
        var text = string.IsNullOrWhiteSpace(reason) ? "人工切换" : reason;
        var raised = new List<(int DeviceId, AcquisitionPathKind Path, string Reason)>();
        foreach (var peer in peers)
        {
            if (path == AcquisitionPathKind.Standby && !DeviceAcquisitionRules.HasStandby(peer))
                throw new InvalidOperationException("共享通道上仍有设备未配置相同备路径，不能单独切换");
            var state = await LoadStateAsync(runtime, peer, now, ct);
            var reasonText = peer.Id == device.Id ? text : text + "（共享通道一并切换）";
            if (Switch(runtime, peer, state, path, reasonText, user, now))
                raised.Add((peer.Id, path, state.Reason ?? reasonText));
        }
        await runtime.SaveChangesAsync(ct);
        if (raised.Count > 0)
        {
            Interlocked.Increment(ref _version);
            NotifyPathChanges(raised);
        }
    }

    public async Task ResumeAutoAsync(int deviceId, string user, CancellationToken ct)
    {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        if (!config.TryGetDevice(deviceId, out var device))
            throw new InvalidOperationException("设备不存在");
        if (!_role.IsActive)
            throw new InvalidOperationException("备机不能切换采集路径");
        var now = ScadaTime.NowMs();
        var unlocked = false;
        foreach (var peer in BusPeers(device))
        {
            var state = await LoadStateAsync(runtime, peer, now, ct);
            if (!state.ManualLock) continue;
            state.ManualLock = false;
            state.PrimaryHealthySince = null;
            state.ChangedTs = now;
            state.ChangedBy = user;
            state.Reason = peer.Id == device.Id ? "恢复自动回切" : "恢复自动回切（共享通道一并解锁）";
            runtime.OperationLogs.Add(new OperationLog
            {
                UserName = user, Ts = now,
                Action = $"设备 {peer.Name} 采集路径恢复自动回切",
                Result = state.Reason, TargetType = "DeviceAcquisition", TargetId = peer.Id.ToString()
            });
            logger.LogInformation("设备 {Device} 采集路径恢复自动回切", peer.Name);
            unlocked = true;
        }
        if (unlocked) await runtime.SaveChangesAsync(ct);
    }

    private IReadOnlyList<Device> BusPeers(Device device) =>
        config.Devices.Where(x => x.Enabled && (x.Id == device.Id || DeviceAcquisitionRules.SharesBusWith(device, x))).ToList();

    private async Task<DeviceAcquisitionState> LoadStateAsync(RuntimeDbContext runtime, Device device, long now, CancellationToken ct) =>
        await runtime.DeviceAcquisitionStates.SingleOrDefaultAsync(x => x.DeviceId == device.Id, ct)
        ?? runtime.DeviceAcquisitionStates.Add(new DeviceAcquisitionState
        {
            DeviceId = device.Id, ActivePath = AcquisitionPathKind.Primary, ChangedTs = now, Reason = "默认主路径"
        }).Entity;

    private async Task TickGroupAsync(RuntimeDbContext runtime, List<Device> group,
        List<(int DeviceId, AcquisitionPathKind Path, string Reason)> raised, long now, bool localCollectionEnabled, CancellationToken ct)
    {
        var members = group.Where(DeviceAcquisitionRules.HasStandby).ToList();
        if (members.Count == 0) return;
        var shared = group.Count > 1;
        if (shared && group.Any(x => !DeviceAcquisitionRules.SameStandbyRoute(members[0], x)))
            return;

        var decisions = new List<(Device Device, DeviceAcquisitionState State, (AcquisitionPathKind Path, string Reason)? Next)>();
        foreach (var device in members)
        {
            var state = await LoadStateAsync(runtime, device, now, ct);
            var current = state.ActivePath;
            _active[device.Id] = current;
            var activeHealthy = IsRouteHealthy(device, now);
            if (current == AcquisitionPathKind.Standby)
                RequestPrimaryProbe(device, ct);
            var primaryHealthy = current == AcquisitionPathKind.Primary ? activeHealthy : IsPrimaryHealthy(device, now);
            var next = Evaluate(device, state, activeHealthy, primaryHealthy, now, localCollectionEnabled);
            decisions.Add((device, state, next));
        }

        if (!shared)
        {
            foreach (var (device, state, next) in decisions)
            {
                if (next is null) continue;
                if (Switch(runtime, device, state, next.Value.Path, next.Value.Reason, "system", now))
                    raised.Add((device.Id, next.Value.Path, next.Value.Reason));
            }
            return;
        }

        if (decisions.Any(x => x.State.ManualLock)) return;
        var failover = decisions.FirstOrDefault(x => x.Next is { Path: AcquisitionPathKind.Standby });
        if (failover.Device is not null)
        {
            var reason = failover.Next!.Value.Reason + "（共享通道一并切换）";
            foreach (var (device, state, _) in decisions)
                if (Switch(runtime, device, state, AcquisitionPathKind.Standby, reason, "system", now))
                    raised.Add((device.Id, AcquisitionPathKind.Standby, reason));
            return;
        }

        if (decisions.All(x => x.Next is { Path: AcquisitionPathKind.Primary }))
        {
            var reason = decisions[0].Next!.Value.Reason + "（共享通道一并切换）";
            foreach (var (device, state, _) in decisions)
                if (Switch(runtime, device, state, AcquisitionPathKind.Primary, reason, "system", now))
                    raised.Add((device.Id, AcquisitionPathKind.Primary, reason));
        }
    }

    private bool Switch(RuntimeDbContext runtime, Device device, DeviceAcquisitionState state, AcquisitionPathKind path, string reason, string user, long now)
    {
        var locking = path == AcquisitionPathKind.Standby && !string.Equals(user, "system", StringComparison.Ordinal);
        if (state.ActivePath == path && _active.TryGetValue(device.Id, out var known) && known == path && state.ManualLock == locking)
            return false;
        var from = state.ActivePath;
        state.ActivePath = path;
        state.ChangedTs = now;
        state.ChangedBy = user;
        state.Reason = reason.Length > 1000 ? reason[..1000] : reason;
        state.ActiveUnhealthySince = null;
        state.PrimaryHealthySince = path == AcquisitionPathKind.Primary ? now : null;
        state.ManualLock = locking;
        _active[device.Id] = path;
        runtime.OperationLogs.Add(new OperationLog
        {
            UserName = user, Ts = now,
            Action = path == AcquisitionPathKind.Standby ? $"设备 {device.Name} 采集切到备路径" : $"设备 {device.Name} 采集切回主路径",
            Result = state.Reason ?? reason, TargetType = "DeviceAcquisition", TargetId = device.Id.ToString()
        });
        logger.LogInformation("设备 {Device} 采集路径 {From} → {To}：{Reason}", device.Name, from, path, state.Reason);
        return true;
    }

    internal static int StaleMs(Device device) => Math.Max(device.PollIntervalMs * 3, 3_000);

    internal static bool IsSampleHealthy(int physicalCount, int goodCount) =>
        physicalCount > 0 && goodCount * 2 >= physicalCount;

    private bool IsRouteHealthy(Device device, long now)
    {
        var physical = config.TagsOfDevice(device.Id).Where(x => x.Kind == TagKind.Physical).ToList();
        if (physical.Count == 0) return false;
        var staleMs = StaleMs(device);
        var healthy = 0;
        foreach (var tag in physical)
        {
            var value = cache.Get(tag.Id);
            if (value is null) continue;
            if (value.Quality == TagQuality.Good && now - value.Ts <= staleMs) healthy++;
        }
        return IsSampleHealthy(physical.Count, healthy);
    }

    private bool IsPrimaryHealthy(Device device, long now) =>
        _probes.TryGetValue(device.Id, out var probe) && now - probe.Ts <= StaleMs(device) && probe.Healthy;

    private void RequestPrimaryProbe(Device device, CancellationToken ct)
    {
        if (!_probing.TryAdd(device.Id, 0)) return;
        _ = ProbePrimaryAsync(device, ct);
    }

    private async Task ProbePrimaryAsync(Device device, CancellationToken ct)
    {
        try
        {
            await _probeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var healthy = await MeasurePrimaryHealthAsync(device, ct).ConfigureAwait(false);
                _probes[device.Id] = new PrimaryProbe(healthy, ScadaTime.NowMs());
            }
            finally { _probeGate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _probes[device.Id] = new PrimaryProbe(false, ScadaTime.NowMs());
            logger.LogDebug(ex, "主路径探活失败：{Device}", device.Name);
        }
        finally { _probing.TryRemove(device.Id, out _); }
    }

    // 回切与切出使用同一套品质门槛。官方缓存在备路径运行时反映的是备路径，
    // 因此对主键做短时、非发布的协议读取；主键走网关时改看网关租约/会话，主机不对 PLC 做 TCP 探活。
    // 探活在评估循环之外异步进行，不会拖死 1 秒 Tick。
    private async Task<bool> MeasurePrimaryHealthAsync(Device device, CancellationToken ct)
    {
        if (device.GatewayId is int gatewayId)
        {
            var health = services?.GetService(typeof(IGatewaySessionHealth)) as IGatewaySessionHealth;
            return health?.IsHealthy(gatewayId) == true;
        }
        if (drivers is null || plans is null) return false;
        var tags = config.TagsOfDevice(device.Id).Where(x => x.Kind == TagKind.Physical).ToList();
        if (tags.Count == 0) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Clamp(device.TimeoutMs, 500, 5_000));
        await using var driver = drivers.Create(device);
        await driver.ConnectAsync(device, timeout.Token);
        var values = await driver.ReadAsync(device, plans.Build(device, tags), timeout.Token);
        var byId = values.ToDictionary(x => x.TagId);
        var good = 0;
        foreach (var tag in tags)
        {
            if (byId.TryGetValue(tag.Id, out var value) && value.Quality == TagQuality.Good)
                good++;
        }
        return IsSampleHealthy(tags.Count, good);
    }

    private readonly record struct PrimaryProbe(bool Healthy, long Ts);
}
