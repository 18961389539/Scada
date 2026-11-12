using System.Text.Json.Serialization;
using Scada.Core.Security;

namespace Scada.Core.Entities;

// 自定义角色：一组能力权限；分配给用户时可限定到某个区域（AreaId 为空表示全部区域）。
public class AccessRole : IAuditedEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Permission Permissions { get; set; }

    // 审计拦截器用的派生属性；TargetType 沿用既有的 "Role"（不是 AccessRole）。
    [JsonIgnore] public string AuditTargetType => "Role";
    [JsonIgnore] public string AuditLabel => "角色";
    [JsonIgnore] public string AuditName => Name;
}

public class UserRoleAssignment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int RoleId { get; set; }
    public int? AreaId { get; set; }
}
