using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class User
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Viewer;
    public bool Enabled { get; set; } = true;
    // 界面语言偏好（zh-CN / en-US），null 表示未设置。
    public string? Locale { get; set; }

    // 首装管理员与管理员新建/重置过口令的账号必须自己改一次：出厂口令不能长期留在现场。
    // 迁移前的历史账号默认 false，他们已经在用现有口令，不应该被强制跳转打断。
    public bool MustChangePassword { get; set; }
    public long? PasswordChangedTs { get; set; }
}
