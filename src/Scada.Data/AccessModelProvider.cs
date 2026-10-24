using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;

namespace Scada.Data;

// 授权模型缓存：配置/授权变更时主动失效；另有短 TTL 兜底（例如其他进程改库）。
public sealed class AccessModelProvider
{
    private readonly Func<CancellationToken, Task<AccessModel>> _loader;

    public AccessModelProvider(IDbContextFactory<ConfigDbContext> factory)
    {
        _loader = async ct =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await BuildAsync(db, ct);
        };
    }

    // 测试或特殊宿主可直接提供模型加载器。
    public AccessModelProvider(Func<CancellationToken, Task<AccessModel>> loader) => _loader = loader;

    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile AccessModel? _model;
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _model = null;
    }

    public async Task<AccessModel> GetAsync(CancellationToken ct = default)
    {
        var current = _model;
        if (current is not null && ScadaTime.NowMs() - current.BuiltAt < Ttl.TotalMilliseconds) return current;
        await _gate.WaitAsync(ct);
        try
        {
            current = _model;
            if (current is not null && ScadaTime.NowMs() - current.BuiltAt < Ttl.TotalMilliseconds) return current;
            var generation = Interlocked.Read(ref _generation);
            var built = await _loader(ct);
            if (generation == Interlocked.Read(ref _generation)) _model = built;
            return built;
        }
        finally { _gate.Release(); }
    }

    public async Task<AccessContext> ForUserAsync(int? userId, CancellationToken ct = default) => (await GetAsync(ct)).For(userId);

    public static async Task<AccessModel> BuildAsync(ConfigDbContext db, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Select(x => new { x.Id, x.AreaId }).ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().Select(x => new { x.Id, x.Kind, x.DeviceId, x.AreaId }).ToListAsync(ct);
        var deps = await db.TagDependencies.AsNoTracking().Select(x => new { x.TagId, x.InputTagId }).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Select(x => new { x.Id, x.Role, x.Enabled }).ToListAsync(ct);
        var assignments = await (from a in db.UserRoleAssignments.AsNoTracking()
                                 join r in db.AccessRoles.AsNoTracking() on a.RoleId equals r.Id
                                 select new { a.UserId, r.Permissions, a.AreaId }).ToListAsync(ct);
        return AccessModel.Build(
            devices.Select(x => (x.Id, x.AreaId)),
            tags.Select(x => (x.Id, x.Kind, x.DeviceId, x.AreaId)),
            deps.Select(x => (x.TagId, x.InputTagId)),
            users.Select(x => (x.Id, x.Role, x.Enabled)),
            assignments.Select(x => (x.UserId, x.Permissions, x.AreaId)),
            ScadaTime.NowMs());
    }

    // 执行时复核（例如网关授权下发、OPC 导入落库）：直接查库，不走缓存。
    public static async Task<bool> UserHasAsync(ConfigDbContext db, int userId, Permission permission, int areaId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.Role, x.Enabled }).FirstOrDefaultAsync(ct);
        if (user is null || !user.Enabled) return false;
        if ((Permissions.ForBuiltIn(user.Role) & permission) == permission) return true;
        var grants = await (from a in db.UserRoleAssignments.AsNoTracking()
                            join r in db.AccessRoles.AsNoTracking() on a.RoleId equals r.Id
                            where a.UserId == userId && (a.AreaId == null || a.AreaId == areaId)
                            select r.Permissions).ToListAsync(ct);
        return grants.Any(p => (p & permission) == permission);
    }

    public static async Task<bool> UserHasForDeviceAsync(ConfigDbContext db, int userId, Permission permission, int deviceId, CancellationToken ct)
    {
        var area = await db.Devices.AsNoTracking().Where(x => x.Id == deviceId).Select(x => (int?)x.AreaId).FirstOrDefaultAsync(ct);
        return area is int a && await UserHasAsync(db, userId, permission, a, ct);
    }
}
