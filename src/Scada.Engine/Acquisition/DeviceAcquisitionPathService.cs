using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Acquisition;
using Scada.Core;
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
    INodeRole? role = null) : BackgroundService, IDeviceAcquisitionPaths
{
    private readonly INodeRole _role = role ?? new StandaloneNodeRole();
    private readonly ConcurrentDictionary<int, AcquisitionPathKind> _active = new();
    public event Action<int, AcquisitionPathKind, string>? PathChanged;

    public AcquisitionPathKind Current(int deviceId) =>
        _active.TryGetValue(deviceId, out var path) ? path : AcquisitionPathKind.Primary;

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
            foreach (var row in await runtime.DeviceAcquisitionStates.AsNoTracking().ToListAsync(ct))
                _active[row.DeviceId] = row.ActivePath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "加载设备采集路径失败");
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        try
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var raised = new List<(int DeviceId, AcquisitionPathKind Path, string Reason)>();
            foreach (var device in config.Devices.Where(x => x.Enabled && DeviceAcquisitionRules.HasStandby(x)))
            {
                var state = await runtime.DeviceAcquisitionStates.SingleOrDefaultAsync(x => x.DeviceId == device.Id, ct)
                    ?? runtime.DeviceAcquisitionStates.Add(new DeviceAcquisitionState
                    {
                        DeviceId = device.Id, ActivePath = AcquisitionPathKind.Primary, ChangedTs = now, Reason = "默认主路径"
                    }).Entity;
                var current = state.ActivePath;
                _active[device.Id] = current;
                var activeHealthy = IsRouteHealthy(device, current, now);
                var primaryHealthy = current == AcquisitionPathKind.Primary ? activeHealthy : IsPrimaryReachable(device);
                var next = Evaluate(device, state, activeHealthy, primaryHealthy, now);
                if (next is null) continue;
                if (Switch(runtime, device, state, next.Value.Path, next.Value.Reason, "system", now))
                    raised.Add((device.Id, next.Value.Path, next.Value.Reason));
            }
            await runtime.SaveChangesAsync(ct);
            foreach (var change in raised)
                PathChanged?.Invoke(change.DeviceId, change.Path, change.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "评估设备采集路径失败");
        }
    }

    public static (AcquisitionPathKind Path, string Reason)? Evaluate(
        Device device, DeviceAcquisitionState state, bool activeHealthy, bool primaryHealthy, long now)
    {
        if (!DeviceAcquisitionRules.HasStandby(device)) return null;
        var failover = Math.Clamp(device.FailoverHoldMs <= 0 ? DeviceAcquisitionRules.DefaultFailoverHoldMs : device.FailoverHoldMs,
            DeviceAcquisitionRules.MinHoldMs, DeviceAcquisitionRules.MaxHoldMs);
        var switchback = Math.Max(failover, device.SwitchbackHoldMs <= 0 ? DeviceAcquisitionRules.DefaultSwitchbackHoldMs : device.SwitchbackHoldMs);

        if (activeHealthy) state.ActiveUnhealthySince = null;
        else state.ActiveUnhealthySince ??= now;

        if (state.ActivePath == AcquisitionPathKind.Standby && primaryHealthy) state.PrimaryHealthySince ??= now;
        else if (!primaryHealthy) state.PrimaryHealthySince = null;

        if (state.ActivePath == AcquisitionPathKind.Primary
            && state.ActiveUnhealthySince is long badSince
            && now - badSince >= failover)
            return (AcquisitionPathKind.Standby, $"主路径持续离线或坏品质超过 {failover} 毫秒，切到备路径");

        if (state.ActivePath == AcquisitionPathKind.Standby
            && state.PrimaryHealthySince is long goodSince
            && now - goodSince >= switchback)
            return (AcquisitionPathKind.Primary, $"主路径恢复并保持 {switchback} 毫秒，自动切回");

        return null;
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
        var state = await runtime.DeviceAcquisitionStates.SingleOrDefaultAsync(x => x.DeviceId == deviceId, ct)
            ?? runtime.DeviceAcquisitionStates.Add(new DeviceAcquisitionState { DeviceId = deviceId }).Entity;
        if (!_role.IsActive)
            throw new InvalidOperationException("备机不能切换采集路径");
        var now = ScadaTime.NowMs();
        var changed = Switch(runtime, device, state, path, string.IsNullOrWhiteSpace(reason) ? "人工切换" : reason, user, now);
        await runtime.SaveChangesAsync(ct);
        if (changed)
            PathChanged?.Invoke(device.Id, path, state.Reason ?? reason);
    }

    private bool Switch(RuntimeDbContext runtime, Device device, DeviceAcquisitionState state, AcquisitionPathKind path, string reason, string user, long now)
    {
        if (state.ActivePath == path && _active.TryGetValue(device.Id, out var known) && known == path)
            return false;
        var from = state.ActivePath;
        state.ActivePath = path;
        state.ChangedTs = now;
        state.ChangedBy = user;
        state.Reason = reason.Length > 1000 ? reason[..1000] : reason;
        state.ActiveUnhealthySince = null;
        state.PrimaryHealthySince = path == AcquisitionPathKind.Primary ? now : null;
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

    private bool IsRouteHealthy(Device device, AcquisitionPathKind path, long now)
    {
        var tags = config.TagsOfDevice(device.Id);
        var physical = tags.Where(x => x.Kind == TagKind.Physical).ToList();
        if (physical.Count == 0) return false;
        var staleMs = Math.Max(device.PollIntervalMs * 3, 3_000);
        var healthy = 0;
        foreach (var tag in physical)
        {
            var value = cache.Get(tag.Id);
            if (value is null) continue;
            if (value.Quality == TagQuality.Good && now - value.Ts <= staleMs) healthy++;
        }
        return healthy * 2 >= physical.Count;
    }

    // 备路径运行时主路径不再官方采集。第一期对主 Host/Port 做短 TCP 探活以判断能否回切；
    // 没有可探地址时不自动回切，只允许人工切回。探活不写实时值和历史。
    private bool IsPrimaryReachable(Device device)
    {
        var primary = DeviceAcquisitionRules.PrimaryOf(device);
        if (string.IsNullOrWhiteSpace(primary.Host) || primary.Port is not int port) return false;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(primary.Host, port);
            return task.Wait(TimeSpan.FromMilliseconds(Math.Clamp(device.TimeoutMs, 200, 1500))) && client.Connected;
        }
        catch { return false; }
    }
}
