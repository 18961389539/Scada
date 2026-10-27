using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Alarms;

public sealed class AlarmService
{
    public const int RtnUnackRetainDays = 7;

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
        CancellationToken cancellationToken = default,
        bool unackedOnly = false,
        bool includeShelved = true,
        bool liveOnly = false,
        bool priorityFirst = false,
        int skip = 0)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = BuildQuery(db, from, to, activeOnly, unackedOnly, includeShelved, liveOnly);
        // priorityFirst：严重程度优先，再按时间。操作台横幅必须先看到仍在活动的最高等级，
        // 不能被更新的低等级报警挤出 Take 窗口。
        var ordered = priorityFirst
            ? query.OrderByDescending(x => x.Level).ThenByDescending(x => x.StartTs)
            : query.OrderByDescending(x => x.StartTs);
        return await ordered
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(limit, 1, 5000))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(
        long? from,
        long? to,
        bool activeOnly = false,
        CancellationToken cancellationToken = default,
        bool unackedOnly = false,
        bool includeShelved = true,
        bool liveOnly = false)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await BuildQuery(db, from, to, activeOnly, unackedOnly, includeShelved, liveOnly)
            .CountAsync(cancellationToken);
    }

    private static IQueryable<AlarmEvent> BuildQuery(
        ConfigDbContext db,
        long? from,
        long? to,
        bool activeOnly,
        bool unackedOnly,
        bool includeShelved,
        bool liveOnly)
    {
        var query = db.AlarmEvents.AsNoTracking();
        // liveOnly：活动的，或已恢复但未确认的（ISA-18.2 RTN Unack），即仍需操作员关注的报警。
        // 状态变化事件（开始即结束）不算待确认报警；恢复未确认只保留 7 天，避免旧库遗留记录淹没实时视图。
        if (liveOnly)
        {
            var since = ScadaTime.NowMs() - (long)TimeSpan.FromDays(RtnUnackRetainDays).TotalMilliseconds;
            query = query.Where(x => x.EndTs == null || (x.AckTs == null && x.EndTs >= since && x.EndTs != x.StartTs));
        }

        if (unackedOnly)
        {
            query = query.Where(x => x.AckTs == null);
        }

        if (!includeShelved)
        {
            query = query.Where(x => !x.Shelved);
        }
        if (from is long f)
        {
            query = query.Where(x => x.StartTs >= f);
        }

        if (to is long t)
        {
            query = query.Where(x => x.StartTs <= t);
        }

        if (!liveOnly && activeOnly)
        {
            query = query.Where(x => x.EndTs == null);
        }

        return query;
    }

    public async Task<bool> AckAsync(int id, string user, CancellationToken cancellationToken = default) =>
        await AckAsync(id, user, null, cancellationToken) is not null;

    // 确认可附备注；已确认的报警再次确认只追加备注，不改首次确认人和时间。
    public async Task<AlarmEvent?> AckAsync(int id, string user, string? note, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (alarm is null)
        {
            return null;
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var now = ScadaTime.NowMs();
        var first = alarm.AckTs is null;
        if (first)
        {
            alarm.AckUser = user;
            alarm.AckTs = now;
        }
        if (first || note is not null)
            db.AlarmActivities.Add(new AlarmActivity { AlarmEventId = id, Ts = now, User = user, Action = first ? "ack" : "comment", Note = note });
        await db.SaveChangesAsync(cancellationToken);
        if (!first) return alarm;
        try
        {
            await _notifier.PublishAlarmAsync(alarm, "ack", cancellationToken);
        }
        catch
        {
            // notification failure must not fail the ack
        }

        return alarm;
    }

    public async Task<AlarmActivity?> AddNoteAsync(int id, string user, string note, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.AlarmEvents.AnyAsync(x => x.Id == id, cancellationToken)) return null;
        var activity = new AlarmActivity { AlarmEventId = id, Ts = ScadaTime.NowMs(), User = user, Action = "comment", Note = note.Trim() };
        db.AlarmActivities.Add(activity);
        await db.SaveChangesAsync(cancellationToken);
        return activity;
    }

    public async Task<(AlarmEvent Alarm, IReadOnlyList<AlarmActivity> Activities, IReadOnlyList<AlarmNotificationLog> Notifications)?> TimelineAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await db.AlarmEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (alarm is null) return null;
        var activities = await db.AlarmActivities.AsNoTracking().Where(x => x.AlarmEventId == id).OrderBy(x => x.Ts).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var notifications = await db.AlarmNotificationLogs.AsNoTracking().Where(x => x.AlarmEventId == id).OrderBy(x => x.Ts).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return (alarm, activities, notifications);
    }
}
