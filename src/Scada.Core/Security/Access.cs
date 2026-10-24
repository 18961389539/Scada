using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Security;

[Flags]
public enum Permission
{
    None = 0,
    View = 1,          // 查看实时/历史/报警/报表
    Control = 2,       // 下发控制命令、设备连通测试
    AckAlarm = 4,      // 确认报警
    Configure = 8,     // 设备、测点、报警规则、画面等配置
    ManageUsers = 16,  // 用户、角色、授权管理
    System = 32,       // 审计日志、备份、诊断等系统维护
    All = View | Control | AckAlarm | Configure | ManageUsers | System
}

public static class Permissions
{
    public static readonly Permission[] Each = [Permission.View, Permission.Control, Permission.AckAlarm, Permission.Configure, Permission.ManageUsers, Permission.System];

    // 三种内置角色保持原语义，并视为“全部区域”授权。
    public static Permission ForBuiltIn(UserRole role) => role switch
    {
        UserRole.Viewer => Permission.View,
        UserRole.Operator => Permission.View | Permission.Control | Permission.AckAlarm,
        UserRole.Admin => Permission.All,
        _ => Permission.None
    };

    public static string[] Names(Permission permissions) =>
        Each.Where(p => permissions.HasFlag(p)).Select(p => p.ToString()).ToArray();
}

// 一条授权：权限集合 + 区域（null 表示全部区域）。
public sealed record AccessGrant(Permission Permissions, int? AreaId);

// 授权判定所需的只读模型：设备→区域、测点→所需区域集合、用户→授权列表。
public sealed class AccessModel
{
    private static readonly int[] DefaultAreas = [Area.DefaultId];

    public AccessModel(
        IReadOnlyDictionary<int, int> deviceAreas,
        IReadOnlyDictionary<int, int[]> tagAreas,
        IReadOnlyDictionary<int, IReadOnlyList<AccessGrant>> userGrants,
        long builtAt)
    {
        DeviceAreas = deviceAreas;
        TagAreas = tagAreas;
        UserGrants = userGrants;
        BuiltAt = builtAt;
    }

    public IReadOnlyDictionary<int, int> DeviceAreas { get; }
    public IReadOnlyDictionary<int, int[]> TagAreas { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<AccessGrant>> UserGrants { get; }
    public long BuiltAt { get; }

    public AccessContext For(int? userId) =>
        new(userId, userId is int id && UserGrants.TryGetValue(id, out var grants) ? grants : [], this);

    public static AccessModel Build(
        IEnumerable<(int Id, int AreaId)> devices,
        IEnumerable<(int Id, TagKind Kind, int? DeviceId, int? AreaId)> tags,
        IEnumerable<(int TagId, int InputTagId)> dependencies,
        IEnumerable<(int Id, UserRole Role, bool Enabled)> users,
        IEnumerable<(int UserId, Permission Permissions, int? AreaId)> assignments,
        long builtAt)
    {
        var deviceAreas = devices.ToDictionary(x => x.Id, x => x.AreaId);
        var tagList = tags.ToDictionary(x => x.Id);
        var inputs = dependencies.GroupBy(x => x.TagId).ToDictionary(g => g.Key, g => g.Select(x => x.InputTagId).ToArray());
        var resolved = new Dictionary<int, int[]>();

        int[] Resolve(int tagId, HashSet<int> visiting)
        {
            if (resolved.TryGetValue(tagId, out var cached)) return cached;
            if (!tagList.TryGetValue(tagId, out var tag)) return DefaultAreas;
            int[] areas;
            if (tag.Kind == TagKind.Physical)
                areas = tag.DeviceId is int d && deviceAreas.TryGetValue(d, out var a) ? [a] : DefaultAreas;
            else if (tag.AreaId is int explicitArea)
                areas = [explicitArea];
            else if (!visiting.Add(tagId))
                return DefaultAreas;
            else
            {
                // 未显式指定区域的计算测点：必须对全部输入测点所在区域有权限，避免借计算测点越权读取。
                var set = new SortedSet<int>();
                foreach (var input in inputs.GetValueOrDefault(tagId, []))
                    foreach (var area in Resolve(input, visiting)) set.Add(area);
                visiting.Remove(tagId);
                areas = set.Count == 0 ? DefaultAreas : set.ToArray();
            }

            resolved[tagId] = areas;
            return areas;
        }

        foreach (var id in tagList.Keys) Resolve(id, new HashSet<int>());

        var byUser = assignments.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.ToList());
        var grants = new Dictionary<int, IReadOnlyList<AccessGrant>>();
        foreach (var user in users)
        {
            if (!user.Enabled) continue;
            var list = new List<AccessGrant>();
            var builtIn = Permissions.ForBuiltIn(user.Role);
            if (builtIn != Permission.None) list.Add(new AccessGrant(builtIn, null));
            if (byUser.TryGetValue(user.Id, out var own))
                list.AddRange(own.Where(x => x.Permissions != Permission.None).Select(x => new AccessGrant(x.Permissions, x.AreaId)));
            grants[user.Id] = list;
        }

        return new AccessModel(deviceAreas, resolved, grants, builtAt);
    }

    public int[] AreasOfTag(int tagId) => TagAreas.TryGetValue(tagId, out var areas) ? areas : DefaultAreas;
}

public sealed class AccessContext(int? userId, IReadOnlyList<AccessGrant> grants, AccessModel model)
{
    public int? UserId { get; } = userId;
    public IReadOnlyList<AccessGrant> Grants { get; } = grants;
    public AccessModel Model { get; } = model;

    public bool HasAny(Permission permission) => Grants.Any(g => (g.Permissions & permission) == permission);

    public bool HasGlobal(Permission permission) => Grants.Any(g => g.AreaId is null && (g.Permissions & permission) == permission);

    public bool Has(Permission permission, int areaId) =>
        Grants.Any(g => (g.Permissions & permission) == permission && (g.AreaId is null || g.AreaId == areaId));

    public bool HasAll(Permission permission, IEnumerable<int> areaIds) => areaIds.All(a => Has(permission, a));

    // 模型中尚不存在的对象（刚创建、或已删除）只有全局授权可访问，其余一律拒绝。
    public bool CanTag(Permission permission, int tagId) =>
        Model.TagAreas.TryGetValue(tagId, out var areas) ? HasAll(permission, areas) : HasGlobal(permission);

    public bool CanDevice(Permission permission, int deviceId) =>
        Model.DeviceAreas.TryGetValue(deviceId, out var area) ? Has(permission, area) : HasGlobal(permission);

    // 返回具有该权限的区域；null 表示全部区域。
    public IReadOnlySet<int>? AreasWith(Permission permission)
    {
        if (HasGlobal(permission)) return null;
        return Grants.Where(g => g.AreaId is int && (g.Permissions & permission) == permission).Select(g => g.AreaId!.Value).ToHashSet();
    }

    public Permission GlobalPermissions => Grants.Where(g => g.AreaId is null).Aggregate(Permission.None, (acc, g) => acc | g.Permissions);

    public IReadOnlyDictionary<int, Permission> AreaPermissions => Grants.Where(g => g.AreaId is int)
        .GroupBy(g => g.AreaId!.Value).ToDictionary(g => g.Key, g => g.Aggregate(Permission.None, (acc, x) => acc | x.Permissions));
}
