using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Licensing;

namespace Scada.Data.Infrastructure;

/// <summary>
/// 设备数和测点数在落库前核对授权上限。0 表示不限制。试用和未注册闸门时不拦截。
/// </summary>
public sealed class LicenseQuotaInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        EnforceAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await EnforceAsync(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async Task EnforceAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is not ConfigDbContext db) return;
        var license = services.GetService<ILicenseEnforcement>();
        if (license is null) return;
        var addDevices = db.ChangeTracker.Entries<Device>().Count(entry => entry.State == EntityState.Added);
        var addTags = db.ChangeTracker.Entries<Tag>().Count(entry => entry.State == EntityState.Added);
        if (addDevices == 0 && addTags == 0) return;
        if ((addDevices == 0 || license.MaxDevices <= 0) && (addTags == 0 || license.MaxTags <= 0)) return;
        var factory = services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using var countDb = await factory.CreateDbContextAsync(cancellationToken);
        var devices = addDevices > 0 ? await countDb.Devices.CountAsync(cancellationToken) : 0;
        var tags = addTags > 0 ? await countDb.Tags.CountAsync(cancellationToken) : 0;
        var message = LicenseQuota.Deny(license, devices, tags, addDevices, addTags);
        if (message is not null) throw new LicenseQuotaException(message);
    }
}
