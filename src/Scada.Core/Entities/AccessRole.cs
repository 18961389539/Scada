using Scada.Core.Security;

namespace Scada.Core.Entities;

// 自定义角色：一组能力权限；分配给用户时可限定到某个区域（AreaId 为空表示全部区域）。
public class AccessRole
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Permission Permissions { get; set; }
}

public class UserRoleAssignment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int RoleId { get; set; }
    public int? AreaId { get; set; }
}
