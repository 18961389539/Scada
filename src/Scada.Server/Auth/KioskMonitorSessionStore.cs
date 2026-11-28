using System.Collections.Concurrent;

namespace Scada.Server.Auth;

/// <summary>
/// 监视专用 Kiosk 播放列表的服务端会话。进入后，同一登录用户的控制请求会被拒绝，
/// 不再只依赖可伪造的 <c>X-Scada-Monitor-Only</c> 请求头。
/// 会话记在本机内存：主备部署时由主机处理下发，备机本来就拒绝写请求。
/// </summary>
public sealed class KioskMonitorSessionStore
{
    private readonly ConcurrentDictionary<int, int> _users = new();

    public void Enter(int userId, int playlistId) => _users[userId] = playlistId;

    public void Leave(int userId) => _users.TryRemove(userId, out _);

    public bool IsLocked(int? userId) => userId is int id && _users.ContainsKey(id);

    public int? PlaylistId(int? userId) => userId is int id && _users.TryGetValue(id, out var playlist) ? playlist : null;
}
