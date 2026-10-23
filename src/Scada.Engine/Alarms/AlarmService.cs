using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Alarms;

public sealed class AlarmService
{
    private readonly IDbContextFactory<ConfigDbContext> _contextFactory;
    private readonly IAlarmNotifier _notifier;

    public AlarmService(IDbContextFactory<ConfigDbContext> contextFactory, IAlarmNotifier notifier)
    {
        _contextFactory = contextFactory;
        _notifier = notifier;
    }

    public async Task<IReadOnlyList<AlarmEvent>> QueryAsync(
        long? from,
        long? to,
        bool activeOnly,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AlarmEvents.AsNoTracking();
        if (from is long f)
        {
            query = query.Where(x => x.StartTs >= f);
        }

        if (to is long t)
        {
            query = query.Where(x => x.StartTs <= t);
        }

        if (activeOnly)
        {
            query = query.Where(x => x.EndTs == null);
        }

        return await query
            .OrderByDescending(x => x.StartTs)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AckAsync(int id, string user, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (alarm is null)
        {
            return false;
        }

        alarm.AckUser = user;
        alarm.AckTs = ScadaTime.NowMs();
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await _notifier.PublishAlarmAsync(alarm, "ack", cancellationToken);
        }
        catch
        {
            // notification failure must not fail the ack
        }

        return true;
    }
}
