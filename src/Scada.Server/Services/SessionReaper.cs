using Microsoft.EntityFrameworkCore;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Services;

// 兜底回收：管理员改角色/停用账号/删号后，即使没有走管理端点（例如直接改库），
// 也会在一个周期内断开旧的实时连接。
public sealed class SessionReaper(
    SessionRegistry registry,
    IDbContextFactory<ConfigDbContext> factory,
    ILogger<SessionReaper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var revoked = await SweepAsync(stoppingToken);
                    if (revoked > 0) logger.LogWarning("已断开 {Count} 个失效的实时连接", revoked);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "实时连接有效性检查失败，下个周期重试");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        if (registry.Count == 0) return 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var users = await db.Users.AsNoTracking()
            .Select(x => new { x.Id, x.Enabled, x.Role, x.PasswordHash })
            .ToListAsync(cancellationToken);
        var valid = users.Where(x => x.Enabled)
            .ToDictionary(x => x.Id, x => (Role: x.Role.ToString(), Version: JwtTokenService.CredentialsVersion(x.PasswordHash)));

        return registry.AbortWhere(entry =>
            !valid.TryGetValue(entry.UserId, out var current)
            || current.Role != entry.Role
            || current.Version != entry.CredentialsVersion);
    }
}
