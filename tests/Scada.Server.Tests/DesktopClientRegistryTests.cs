using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Options;
using Scada.Server.Desktop;

namespace Scada.Server.Tests;

public sealed class DesktopClientRegistryTests
{
    [Fact]
    public void TracksOnlineStateAndConflict()
    {
        var clock = new MutableTimeProvider();
        var registry = Registry(clock);
        var clientId = Guid.NewGuid().ToString("D");

        registry.Heartbeat(Heartbeat(clientId, "PC-A"), 1, "kiosk", "127.0.0.1", "A", Options(90));
        var first = registry.Snapshot("A", "Primary", Options(90));
        Assert.Equal(1, first.Total);
        Assert.True(Assert.Single(first.Clients).Online);

        clock.Now = clock.Now.AddSeconds(91);
        var offline = registry.Snapshot("A", "Primary", Options(90));
        Assert.False(Assert.Single(offline.Clients).Online);
        Assert.Equal(0, offline.Online);

        registry.Heartbeat(Heartbeat(clientId, "PC-B"), 1, "kiosk", "127.0.0.1", "A", Options(90));
        var conflict = Assert.Single(registry.Snapshot("A", "Primary", Options(90)).Clients);
        Assert.True(conflict.Conflict);
        Assert.Equal("PC-A", conflict.PreviousMachineName);
        Assert.Equal("PC-B", conflict.MachineName);
    }

    [Fact]
    public void KeepsOneRecordPerClientIdAndCountsMultipleClients()
    {
        var clock = new MutableTimeProvider();
        var registry = Registry(clock);
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");

        registry.Heartbeat(Heartbeat(firstId, "PC-A"), 1, "kiosk", null, "A", Options(90));
        registry.Heartbeat(Heartbeat(firstId, "PC-A"), 1, "kiosk", null, "A", Options(90));
        registry.Heartbeat(Heartbeat(secondId, "PC-B"), 1, "kiosk", null, "A", Options(90));

        var snapshot = registry.Snapshot("A", "Primary", Options(90));
        Assert.Equal(2, snapshot.Total);
        Assert.Equal(2, snapshot.Clients.Count);
    }

    [Fact]
    public void AcceptsWorkspaceTargetKind()
    {
        var registry = Registry(new MutableTimeProvider());
        var clientId = Guid.NewGuid().ToString("D");

        registry.Heartbeat(Heartbeat(clientId, "PC-A") with { TargetKind = "workspace", TargetId = null, TargetName = "多页面工作台" },
            null, "kiosk", null, "A", Options(90));

        var client = Assert.Single(registry.Snapshot("A", "Primary", Options(90)).Clients);
        Assert.Equal("workspace", client.TargetKind);
        Assert.Null(client.TargetId);
        Assert.Equal("多页面工作台", client.TargetName);
    }

    [Fact]
    public void RejectsInvalidClientId()
    {
        var registry = Registry(new MutableTimeProvider());
        Assert.Throws<ArgumentException>(() =>
            registry.Heartbeat(Heartbeat("not-a-guid", "PC-A"), 1, "kiosk", null, "A", Options(90)));
    }

    /// <summary>机器名改回原值时应解除冲突，否则运维页会一直挂着一条早已消失的告警。</summary>
    [Fact]
    public void ConflictClearsWhenMachineNameReverts()
    {
        var clock = new MutableTimeProvider();
        var registry = Registry(clock);
        var clientId = Guid.NewGuid().ToString("D");

        registry.Heartbeat(Heartbeat(clientId, "PC-A"), 7, "kiosk", null, "A", Options(90));
        registry.Heartbeat(Heartbeat(clientId, "PC-B"), 7, "kiosk", null, "A", Options(90));
        Assert.True(Assert.Single(registry.Snapshot("A", "Primary", Options(90)).Clients).Conflict);

        registry.Heartbeat(Heartbeat(clientId, "PC-A"), 7, "kiosk", null, "A", Options(90));
        var client = Assert.Single(registry.Snapshot("A", "Primary", Options(90)).Clients);
        Assert.False(client.Conflict);
        Assert.Null(client.PreviousMachineName);
    }

    /// <summary>60 台共享一个只读账号是常见部署，旧的 50 台上限会让它们互相挤掉。</summary>
    [Fact]
    public void KeepsMoreThanFiftyClientsForOneAccount()
    {
        var clock = new MutableTimeProvider();
        var registry = Registry(clock);
        var ids = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid().ToString("D")).ToList();

        foreach (var id in ids)
            registry.Heartbeat(Heartbeat(id, "PC"), 7, "kiosk", null, "A", Options(90));

        var snapshot = registry.Snapshot("A", "Primary", Options(90));
        Assert.Equal(60, snapshot.Total);
        Assert.Equal(60, snapshot.Clients.Count);
        Assert.All(ids, id => Assert.Contains(snapshot.Clients, client => client.ClientId == id));
    }

    /// <summary>超出上限时先淘汰已离线的记录，正在上报的终端不能因为排得早就被踢掉。</summary>
    [Fact]
    public void EvictsOfflineEntriesBeforeOnlineOnes()
    {
        var clock = new MutableTimeProvider();
        var registry = Registry(clock);
        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid().ToString("D")).ToList();

        // 时间逐个递增，保证"最久未上报"是可判定的（同一时刻注册时顺序不确定）。
        foreach (var id in ids)
        {
            clock.Now = clock.Now.AddSeconds(1);
            registry.Heartbeat(Heartbeat(id, "PC"), 7, "kiosk", null, "A", Options(90));
        }

        // 全部离线；第一个客户端重新上报，重新变成在线。
        clock.Now = clock.Now.AddMinutes(5);
        registry.Heartbeat(Heartbeat(ids[0], "PC"), 7, "kiosk", null, "A", Options(90));
        registry.Heartbeat(Heartbeat(Guid.NewGuid().ToString("D"), "PC"), 7, "kiosk", null, "A", Options(90));

        var snapshot = registry.Snapshot("A", "Primary", Options(90));
        Assert.Equal(200, snapshot.Total);
        Assert.Contains(snapshot.Clients, client => client.ClientId == ids[0]);
        Assert.DoesNotContain(snapshot.Clients, client => client.ClientId == ids[1]);
    }

    /// <summary>被门禁挡住的状态由服务端按上报版本判定，不依赖客户端自报。</summary>
    [Fact]
    public void SnapshotDerivesBlockedAndOutdatedFromReportedVersion()
    {
        var clock = new MutableTimeProvider();
        var registry = Registry(clock);
        var options = new DesktopClientOptions
        {
            MinimumVersion = "2.0.0",
            LatestVersion = "3.0.0",
            OnlineThresholdSeconds = 90
        };

        registry.Heartbeat(Heartbeat(Guid.NewGuid().ToString("D"), "PC-A") with { AppVersion = "1.0.0" }, 7, "kiosk", null, "A", options);
        registry.Heartbeat(Heartbeat(Guid.NewGuid().ToString("D"), "PC-B") with { AppVersion = "2.5.0" }, 7, "kiosk", null, "A", options);
        registry.Heartbeat(Heartbeat(Guid.NewGuid().ToString("D"), "PC-C") with { AppVersion = "3.1.0" }, 7, "kiosk", null, "A", options);

        var snapshot = registry.Snapshot("A", "Primary", options);
        Assert.Equal(3, snapshot.Total);
        Assert.Equal(3, snapshot.Online);
        Assert.Equal(1, snapshot.Blocked);
        Assert.Equal(2, snapshot.Outdated);

        var blocked = Assert.Single(snapshot.Clients, client => client.BlockedReason is not null);
        Assert.Equal("version", blocked.BlockedReason);
    }

    [Fact]
    public void ListsCurrentlyOfflineClientsAndClearsAfterRecovery()
    {
        var clock = new MutableTimeProvider();
        var registry = Registry(clock);
        var clientId = Guid.NewGuid().ToString("D");
        registry.Heartbeat(Heartbeat(clientId, "PC-A"), 1, "kiosk", "127.0.0.1", "A", Options(90));
        Assert.Empty(registry.ListCurrentlyOffline(Options(90)));

        clock.Now = clock.Now.AddSeconds(91);
        registry.Sweep(Options(90));
        var offline = Assert.Single(registry.ListCurrentlyOffline(Options(90)));
        Assert.Equal(clientId, offline.ClientId);
        Assert.False(offline.Online);

        registry.Heartbeat(Heartbeat(clientId, "PC-A"), 1, "kiosk", "127.0.0.1", "A", Options(90));
        Assert.Empty(registry.ListCurrentlyOffline(Options(90)));
    }

    private static DesktopClientRegistry Registry(TimeProvider clock) =>
        new(NullLogger<DesktopClientRegistry>.Instance, clock);

    private static DesktopClientOptions Options(int threshold) =>
        new() { OnlineThresholdSeconds = threshold };

    private static DesktopClientHeartbeat Heartbeat(string clientId, string machine) => new(
        clientId, machine, "operator", "1.0.0", "131.0", "boot", "screen", 1, "总览", "user", null);
}

internal sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
