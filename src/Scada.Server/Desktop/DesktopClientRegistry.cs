using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Scada.Core.Options;

namespace Scada.Server.Desktop;

public sealed class DesktopClientHeartbeatRequest
{
    public string? ClientId { get; set; }
    public string? MachineName { get; set; }
    public string? WindowsUser { get; set; }
    public string? AppVersion { get; set; }
    public string? WebView2Version { get; set; }
    public string? BootId { get; set; }
    public string? TargetKind { get; set; }
    public int? TargetId { get; set; }
    public string? TargetName { get; set; }
    public string? ConfigScope { get; set; }
    public string? BlockedReason { get; set; }
}

public sealed record DesktopClientHeartbeat(
    string ClientId, string MachineName, string WindowsUser, string AppVersion, string WebView2Version,
    string? BootId, string? TargetKind, int? TargetId, string? TargetName, string? ConfigScope, string? BlockedReason);

public sealed record DesktopClientView(
    string ClientId, string MachineName, string WindowsUser, int? UserId, string? UserName,
    string? TargetKind, int? TargetId, string? TargetName, string AppVersion, string WebView2Version,
    string? ConfigScope, string NodeId, string? RemoteAddress,
    long FirstSeenTs, long LastSeenTs, bool Online, string? BlockedReason,
    bool Conflict, string? PreviousMachineName);

/// <summary>
/// 在线列表。计数按<b>全部</b>客户端统计，<see cref="Clients"/> 只返回最近上报的 500 条，
/// 两者口径分开，避免界面把"前 500 条里的在线数"当成全场在线数。
/// </summary>
public sealed record DesktopClientSnapshot(
    string Node, string HaRole, int OnlineThresholdSeconds, int Total,
    int Online, int Blocked, int Outdated, bool Truncated,
    DesktopClientPolicyView Policy, IReadOnlyList<DesktopClientView> Clients);

/// <summary>
/// 桌面客户端在线状态只保存在内存：心跳是 30 秒级高频数据，写运行库会放大 SQLite WAL 和备份，
/// 而在线状态本身是“本节点视角”的运维信息，不适合进入主备复制的运行数据。
/// 服务端重启后最多一个心跳周期即可补齐。
///
/// 并发模型：<see cref="Entry"/> 是可变的，所有读写都在该条记录的锁内完成。快照要投影
/// 5000 条记录，锁粒度仍是"每个客户端一把"，不会把整表串行化。
/// </summary>
public sealed class DesktopClientRegistry(ILogger<DesktopClientRegistry> logger, TimeProvider timeProvider)
{
    private const int MaxClients = 5000;

    // 50 台对控制室偏小：同一只读账号跑 60 台时，每注册一个新客户端就挤掉一台在线终端，
    // 被挤掉的 30 秒后重新注册、再挤掉下一台，形成持续的轮换（FirstSeen 反复重置、
    // 列表里总有一条刚被踢掉的离线记录）。200 覆盖单站常见规模，仍保留上限防止内存被撑爆。
    private const int MaxClientsPerUser = 200;
    private const int MaxSnapshotClients = 500;
    private static readonly long RetentionMs = (long)TimeSpan.FromDays(7).TotalMilliseconds;
    private readonly ConcurrentDictionary<string, Entry> _clients = new(StringComparer.OrdinalIgnoreCase);
    private long _lastCleanupTs;

    public DesktopClientView Heartbeat(
        DesktopClientHeartbeat request, int? userId, string? userName, string? remoteAddress,
        string nodeId, DesktopClientOptions options)
    {
        if (!Guid.TryParse(request.ClientId, out var parsed))
            throw new ArgumentException("clientId 必须是 GUID", nameof(request));

        var clientId = parsed.ToString("D");
        var now = NowMs();
        Cleanup(now);

        var machine = Text(request.MachineName, 64) is { Length: > 0 } machineValue ? machineValue : "unknown";
        var windowsUser = Text(request.WindowsUser, 64) is { Length: > 0 } userValue ? userValue : "unknown";
        var appVersion = Text(request.AppVersion, 32) is { Length: > 0 } versionValue ? versionValue : "unknown";
        var webViewVersion = Text(request.WebView2Version, 32) is { Length: > 0 } webViewValue ? webViewValue : "unknown";
        var targetKind = NormalizeTargetKind(request.TargetKind);
        var targetId = request.TargetId is > 0 ? request.TargetId : null;
        var targetName = Text(request.TargetName, 120) is { Length: > 0 } targetValue ? targetValue : null;
        var configScope = NormalizeScope(request.ConfigScope);
        var blockedReason = Text(request.BlockedReason, 200) is { Length: > 0 } blockedValue ? blockedValue : null;

        if (_clients.TryGetValue(clientId, out var existing))
        {
            return UpdateAndView(existing, machine, windowsUser, userId, userName, appVersion, webViewVersion,
                request.BootId, targetKind, targetId, targetName, configScope, blockedReason, remoteAddress, nodeId, now, options);
        }

        EvictIfNeeded(userId, now, options.OnlineThresholdSeconds);
        var entry = new Entry
        {
            ClientId = clientId,
            MachineName = machine,
            WindowsUser = windowsUser,
            UserId = userId,
            UserName = userName,
            AppVersion = appVersion,
            WebView2Version = webViewVersion,
            BootId = Text(request.BootId, 64),
            TargetKind = targetKind,
            TargetId = targetId,
            TargetName = targetName,
            ConfigScope = configScope,
            BlockedReason = blockedReason,
            RemoteAddress = Text(remoteAddress, 64),
            NodeId = nodeId,
            FirstSeenTs = now,
            LastSeenTs = now
        };
        if (_clients.TryAdd(clientId, entry))
        {
            logger.LogInformation("桌面客户端首次注册 {ClientId} {Machine} {User}", clientId, machine, userName);
            return ToView(entry, now, options);
        }

        // 并发首次心跳：另一个线程已写入，更新胜出的记录而不是覆盖 FirstSeenTs。
        var winner = _clients[clientId];
        return UpdateAndView(winner, machine, windowsUser, userId, userName, appVersion, webViewVersion,
            request.BootId, targetKind, targetId, targetName, configScope, blockedReason, remoteAddress, nodeId, now, options);
    }

    private DesktopClientView UpdateAndView(Entry existing, string machine, string windowsUser, int? userId, string? userName,
        string appVersion, string webViewVersion, string? bootId, string? targetKind, int? targetId, string? targetName,
        string? configScope, string? blockedReason, string? remoteAddress, string nodeId, long now, DesktopClientOptions options)
    {
        lock (existing)
        {
            if (!string.Equals(existing.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.Conflict
                    && string.Equals(existing.PreviousMachineName, machine, StringComparison.OrdinalIgnoreCase))
                {
                    // 机器名改回原值：解除冲突，否则运维页会一直挂着一条早已消失的告警。
                    existing.Conflict = false;
                    existing.PreviousMachineName = null;
                }
                else
                {
                    existing.PreviousMachineName = existing.MachineName;
                    existing.Conflict = true;
                    logger.LogWarning("桌面客户端 {ClientId} 机器名从 {Old} 变为 {New}，保留最新记录并标记冲突",
                        existing.ClientId, existing.MachineName, machine);
                }
            }
            if (!string.IsNullOrEmpty(blockedReason) && !string.Equals(existing.BlockedReason, blockedReason, StringComparison.Ordinal))
                logger.LogWarning("桌面客户端 {ClientId} 被阻塞：{Reason}", existing.ClientId, blockedReason);

            existing.MachineName = machine;
            existing.WindowsUser = windowsUser;
            existing.UserId = userId;
            existing.UserName = userName;
            existing.AppVersion = appVersion;
            existing.WebView2Version = webViewVersion;
            existing.BootId = Text(bootId, 64);
            existing.TargetKind = targetKind;
            existing.TargetId = targetId;
            existing.TargetName = targetName;
            existing.ConfigScope = configScope;
            existing.BlockedReason = blockedReason;
            existing.RemoteAddress = Text(remoteAddress, 64);
            existing.NodeId = nodeId;
            existing.LastSeenTs = now;
            return BuildView(existing, now, options);
        }
    }

    public DesktopClientSnapshot Snapshot(string node, string haRole, DesktopClientOptions options)
    {
        var now = NowMs();
        Cleanup(now);
        var views = _clients.Values.Select(entry => ToView(entry, now, options)).ToList();
        var clients = views
            .OrderByDescending(view => view.Online)
            .ThenByDescending(view => view.LastSeenTs)
            .Take(MaxSnapshotClients)
            .ToList();
        return new DesktopClientSnapshot(
            node, haRole, Math.Clamp(options.OnlineThresholdSeconds, 30, 600), views.Count,
            Online: views.Count(view => view.Online),
            Blocked: views.Count(view => view.BlockedReason is not null),
            Outdated: views.Count(view => DesktopClientPolicy.IsOutdated(view.AppVersion, options)),
            Truncated: views.Count > MaxSnapshotClients,
            Policy: DesktopClientPolicy.Describe(options),
            clients);
    }

    /// <summary>
    /// 腾出位置。优先淘汰离线记录（已下线/已报废的终端），仍然超出时才淘汰最久未上报的在线记录，
    /// 并记 Warn：这说明同一账号的终端数确实超过上限，接下来会反复轮换。
    /// </summary>
    private void EvictIfNeeded(int? userId, long now, int onlineThresholdSeconds)
    {
        while (_clients.Count >= MaxClients && TryEvict(_clients.Values, now, onlineThresholdSeconds, "全局上限"))
        {
        }

        if (userId is not int uid) return;
        while (true)
        {
            var userEntries = _clients.Values.Where(entry => UserIdOf(entry) == uid).ToList();
            if (userEntries.Count < MaxClientsPerUser) return;
            if (!TryEvict(userEntries, now, onlineThresholdSeconds, $"账号 {uid} 的终端上限")) return;
        }
    }

    private bool TryEvict(IEnumerable<Entry> candidates, long now, int onlineThresholdSeconds, string scope)
    {
        var victim = candidates
            .Select(entry => (Entry: entry, SeenAt: SeenAt(entry), Online: IsOnline(entry, now, onlineThresholdSeconds)))
            .OrderBy(item => item.Online)
            .ThenBy(item => item.SeenAt)
            .FirstOrDefault();

        if (victim.Entry is null) return false;
        if (victim.Online)
        {
            logger.LogWarning(
                "桌面客户端 {Scope} 已满且全部在线，淘汰最久未上报的 {ClientId} {Machine}；该终端会重新注册并再次触发淘汰",
                scope, victim.Entry.ClientId, MachineOf(victim.Entry));
        }
        return _clients.TryRemove(victim.Entry.ClientId, out _);
    }

    private void Cleanup(long now)
    {
        var last = Interlocked.Read(ref _lastCleanupTs);
        if (now - last < 60_000) return;
        if (Interlocked.CompareExchange(ref _lastCleanupTs, now, last) != last) return;
        foreach (var entry in _clients.Values)
        {
            if (now - SeenAt(entry) > RetentionMs) _clients.TryRemove(entry.ClientId, out _);
        }
    }

    private DesktopClientView ToView(Entry entry, long now, DesktopClientOptions options)
    {
        lock (entry) return BuildView(entry, now, options);
    }

    /// <summary>调用方必须已持有 <paramref name="entry"/> 的锁。</summary>
    private DesktopClientView BuildView(Entry entry, long now, DesktopClientOptions options)
    {
        // 服务端按上报版本自行判定，客户端上报的原因只作补充。
        var blocked = DesktopClientPolicy.IsBlocked(entry.AppVersion, options) ? "version" : entry.BlockedReason;
        var online = TrackOffline(entry, now, options);
        return new DesktopClientView(
            entry.ClientId, entry.MachineName, entry.WindowsUser, entry.UserId, entry.UserName,
            entry.TargetKind, entry.TargetId, entry.TargetName, entry.AppVersion, entry.WebView2Version,
            entry.ConfigScope, entry.NodeId, entry.RemoteAddress,
            entry.FirstSeenTs, entry.LastSeenTs, online,
            blocked, entry.Conflict, entry.PreviousMachineName);
    }

    /// <summary>
    /// 离线巡检：不投影视图，只维护"在线→离线/恢复"的状态与日志。由后台服务周期调用，
    /// 这样没人打开运维页时也能在服务端日志里看到终端掉线（通知渠道接入另行设计）。
    /// </summary>
    public void Sweep(DesktopClientOptions options)
    {
        var now = NowMs();
        foreach (var entry in _clients.Values)
        {
            lock (entry) TrackOffline(entry, now, options);
        }
    }

    /// <summary>当前已登记且心跳超时的桌面/操作台，供健康告警与交班页使用。</summary>
    public IReadOnlyList<DesktopClientView> ListCurrentlyOffline(DesktopClientOptions options)
    {
        var now = NowMs();
        var views = new List<DesktopClientView>();
        foreach (var entry in _clients.Values)
        {
            lock (entry)
            {
                if (IsOnline(entry, now, options.OnlineThresholdSeconds)) continue;
                views.Add(BuildView(entry, now, options));
            }
        }
        return views.OrderBy(view => view.LastSeenTs).ToArray();
    }

    /// <summary>调用方必须已持有 <paramref name="entry"/> 的锁。返回当前是否在线。</summary>
    private bool TrackOffline(Entry entry, long now, DesktopClientOptions options)
    {
        var online = now - entry.LastSeenTs <= Math.Clamp(options.OnlineThresholdSeconds, 30, 600) * 1000L;
        // 只在状态转换时记一条：快照每 15 秒拉一次，逐次告警会刷满日志。
        if (!online && !entry.OfflineLogged)
        {
            entry.OfflineLogged = true;
            logger.LogWarning("桌面客户端 {ClientId} {Machine} 已离线（最后心跳 {LastSeen}）",
                entry.ClientId, entry.MachineName, DateTimeOffset.FromUnixTimeMilliseconds(entry.LastSeenTs).ToLocalTime());
        }
        else if (online && entry.OfflineLogged)
        {
            entry.OfflineLogged = false;
            logger.LogInformation("桌面客户端 {ClientId} {Machine} 已恢复在线", entry.ClientId, entry.MachineName);
        }
        return online;
    }

    private static long SeenAt(Entry entry)
    {
        lock (entry) return entry.LastSeenTs;
    }

    private static bool IsOnline(Entry entry, long now, int onlineThresholdSeconds)
    {
        lock (entry) return now - entry.LastSeenTs <= Math.Clamp(onlineThresholdSeconds, 30, 600) * 1000L;
    }

    private static int? UserIdOf(Entry entry)
    {
        lock (entry) return entry.UserId;
    }

    private static string MachineOf(Entry entry)
    {
        lock (entry) return entry.MachineName;
    }

    private static string? NormalizeTargetKind(string? value)
    {
        var kind = Text(value, 20).ToLowerInvariant();
        return kind is "screen" or "layout" or "playlist" or "workstation" or "workspace" ? kind : null;
    }

    private static string? NormalizeScope(string? value)
    {
        var scope = Text(value, 20).ToLowerInvariant();
        return scope is "machine" or "user" or "local" ? scope : null;
    }

    private static string Text(string? value, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }

    private long NowMs() => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private sealed class Entry
    {
        public required string ClientId { get; init; }
        public required string MachineName { get; set; }
        public required string WindowsUser { get; set; }
        public int? UserId { get; set; }
        public string? UserName { get; set; }
        public required string AppVersion { get; set; }
        public required string WebView2Version { get; set; }
        public string? BootId { get; set; }
        public string? TargetKind { get; set; }
        public int? TargetId { get; set; }
        public string? TargetName { get; set; }
        public string? ConfigScope { get; set; }
        public string? BlockedReason { get; set; }
        public string? RemoteAddress { get; set; }
        public required string NodeId { get; set; }
        public long FirstSeenTs { get; init; }
        public long LastSeenTs { get; set; }
        public bool Conflict { get; set; }
        public string? PreviousMachineName { get; set; }
        public bool OfflineLogged { get; set; }
    }
}
