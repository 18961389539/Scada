using Microsoft.EntityFrameworkCore;
using Scada.Core.Configuration;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Data;

/// <summary>
/// 比较两次配置加载之间的分类指纹，供重载器与跨进程刷新共用。
/// 首次 <see cref="Initialize"/> 只记录基线，不发布变更。
/// </summary>
public sealed class ConfigChangeTracker(IDbContextFactory<ConfigDbContext> factory)
{
    private readonly object _gate = new();
    private bool _initialized;
    private int _collectionVersion;
    private int _alarmVersion;
    private int _addressSpaceVersion;
    private string? _accessFingerprint;
    private string? _jobsFingerprint;
    public string? CalendarFingerprint { get; private set; }

    public bool IsInitialized { get { lock (_gate) return _initialized; } }

    public async Task InitializeAsync(IConfigSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var aux = await ReadAuxiliaryAsync(cancellationToken);
        lock (_gate)
        {
            Capture(snapshot, aux);
            _initialized = true;
        }
    }

    public async Task<(ConfigChangeSet Changes, bool CalendarChanged)> DiffAfterReloadAsync(
        IConfigSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var aux = await ReadAuxiliaryAsync(cancellationToken);
        lock (_gate)
        {
            if (!_initialized)
            {
                Capture(snapshot, aux);
                _initialized = true;
                return (ConfigChangeSet.Empty, false);
            }

            var kinds = new List<ConfigChangeKind>();
            if (snapshot.CollectionVersion != _collectionVersion) kinds.Add(ConfigChangeKind.Collection);
            if (snapshot.AlarmVersion != _alarmVersion) kinds.Add(ConfigChangeKind.Alarms);
            if (snapshot.AddressSpaceVersion != _addressSpaceVersion) kinds.Add(ConfigChangeKind.ExternalAddressSpace);
            if (!string.Equals(aux.Access, _accessFingerprint, StringComparison.Ordinal)) kinds.Add(ConfigChangeKind.Access);
            if (!string.Equals(aux.Jobs, _jobsFingerprint, StringComparison.Ordinal)) kinds.Add(ConfigChangeKind.Jobs);
            var calendarChanged = !string.Equals(aux.Calendar, CalendarFingerprint, StringComparison.Ordinal);
            Capture(snapshot, aux);
            return (ConfigChangeSet.Of([.. kinds]), calendarChanged);
        }
    }

    public async Task<AuxiliaryFingerprints> ReadAuxiliaryAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var users = await db.Users.AsNoTracking()
            .Select(x => x.Id + "\u001f" + x.UserName + "\u001f" + x.Enabled + "\u001f" + (x.Role) + "\u001f" + (x.PasswordHash ?? ""))
            .ToListAsync(cancellationToken);
        var roles = await db.AccessRoles.AsNoTracking()
            .Select(x => x.Id + "\u001f" + (x.Name ?? "") + "\u001f" + (int)x.Permissions)
            .ToListAsync(cancellationToken);
        var assignments = await db.UserRoleAssignments.AsNoTracking()
            .Select(x => x.UserId + "\u001f" + x.RoleId + "\u001f" + x.AreaId)
            .ToListAsync(cancellationToken);
        var jobs = await db.CalcJobs.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => x.Id + "\u001f" + x.Revision + "\u001f" + x.Enabled + "\u001f" + (x.Script ?? "") + "\u001f" + (x.Name ?? ""))
            .ToListAsync(cancellationToken);
        var calendar = await db.SiteOperationsSettings.AsNoTracking()
            .Select(x => x.ShiftTimeZone)
            .FirstOrDefaultAsync(cancellationToken);
        users.Sort(StringComparer.Ordinal);
        roles.Sort(StringComparer.Ordinal);
        assignments.Sort(StringComparer.Ordinal);
        return new AuxiliaryFingerprints(
            CollectionFingerprint.OfText("access", string.Join("\n", users), string.Join("\n", roles), string.Join("\n", assignments)),
            CollectionFingerprint.OfText("jobs", string.Join("\n", jobs)),
            calendar ?? "");
    }

    private void Capture(IConfigSnapshot snapshot, AuxiliaryFingerprints aux)
    {
        _collectionVersion = snapshot.CollectionVersion;
        _alarmVersion = snapshot.AlarmVersion;
        _addressSpaceVersion = snapshot.AddressSpaceVersion;
        _accessFingerprint = aux.Access;
        _jobsFingerprint = aux.Jobs;
        CalendarFingerprint = aux.Calendar;
    }

    public readonly record struct AuxiliaryFingerprints(string Access, string Jobs, string Calendar);
}
