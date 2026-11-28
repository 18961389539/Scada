using System.ComponentModel.DataAnnotations;

namespace Scada.Core.Entities;

/// <summary>
/// 监视专用 Kiosk 播放列表的服务端会话，按登录令牌（jti）绑定，随令牌过期。
/// 记在运行库，主备复制后升主可重建，避免锁在切换窗口里蒸发。
/// </summary>
public class KioskMonitorSession
{
    [MaxLength(64)]
    public string SessionId { get; set; } = string.Empty;
    public int UserId { get; set; }
    public int PlaylistId { get; set; }
    public long ExpiresTs { get; set; }
    public long EnteredTs { get; set; }
}
