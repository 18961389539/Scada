using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Alarms;

public sealed record ShelveResult(bool Success, string? Error, AlarmShelve? Shelve = null);

// 暂挂（ISA-18.2 shelving）：按报警规则临时屏蔽，限时、必须写原因，到期由 AlarmEngine 定时器自动解除。
// 暂挂期间报警照常记录（带 Shelved 标记），但不发声、不外发；解除时仍活动且未确认的报警重新报警。
public sealed class AlarmShelfService
{
    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly IAlarmNotifier _notifier;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<int, AlarmShelve> _active = new();
    private volatile bool _loaded;

    public AlarmShelfService(IDbContextFactory<ConfigDbContext> factory, IAlarmNotifier notifier, ILogger<AlarmShelfService>? logger = null)
    {
        _factory = factory;
        _notifier = notifier;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public bool IsShelved(int ruleId) => _active.ContainsKey(ruleId);

    public IReadOnlyList<AlarmShelve> Active => _active.Values.OrderBy(x => x.UntilTs).ToList();

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            await using var db = await _factory.CreateDbContextAsync(ct);
            _active = (await db.AlarmShelves.AsNoTracking().Where(x => x.ClearedTs == null).ToListAsync(ct))
                .GroupBy(x => x.RuleId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.StartTs).First());
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<ShelveResult> ShelveAsync(int ruleId, int minutes, string? reason, string user, int maxMinutes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return new(false, "暂挂必须填写原因");
        if (minutes < 1 || minutes > Math.Max(maxMinutes, 1)) return new(false, $"暂挂时长须在 1～{Math.Max(maxMinutes, 1)} 分钟之间");
        await EnsureLoadedAsync(ct);
        var notify = new List<AlarmEvent>();
        AlarmShelve shelve;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            if (!await db.AlarmRules.AnyAsync(x => x.Id == ruleId, ct)) return new(false, "报警规则不存在");
            var now = ScadaTime.NowMs();
            foreach (var old in await db.AlarmShelves.Where(x => x.RuleId == ruleId && x.ClearedTs == null).ToListAsync(ct))
            {
                old.ClearedTs = now; old.ClearedBy = user;
            }
            shelve = new AlarmShelve { RuleId = ruleId, Reason = reason.Trim(), User = user, StartTs = now, UntilTs = now + minutes * 60_000L };
            db.AlarmShelves.Add(shelve);
            foreach (var alarm in await db.AlarmEvents.Where(x => x.RuleId == ruleId && x.EndTs == null && !x.Shelved).ToListAsync(ct))
            {
                alarm.Shelved = true;
                db.AlarmActivities.Add(new AlarmActivity { AlarmEventId = alarm.Id, Ts = now, User = user, Action = "shelve", Note = $"暂挂 {minutes} 分钟：{shelve.Reason}" });
                notify.Add(alarm);
            }
            await db.SaveChangesAsync(ct);
            var next = new Dictionary<int, AlarmShelve>(_active) { [ruleId] = shelve };
            _active = next;
        }
        finally { _gate.Release(); }

        foreach (var alarm in notify) await NotifyAsync(alarm, "shelve", ct);
        return new(true, null, shelve);
    }

    public async Task<bool> UnshelveAsync(int ruleId, string? user, string note, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var notify = new List<AlarmEvent>();
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var shelves = await db.AlarmShelves.Where(x => x.RuleId == ruleId && x.ClearedTs == null).ToListAsync(ct);
            if (shelves.Count == 0)
            {
                if (_active.ContainsKey(ruleId)) { var copy = new Dictionary<int, AlarmShelve>(_active); copy.Remove(ruleId); _active = copy; }
                return false;
            }
            var now = ScadaTime.NowMs();
            foreach (var s in shelves) { s.ClearedTs = now; s.ClearedBy = user ?? "system"; }
            foreach (var alarm in await db.AlarmEvents.Where(x => x.RuleId == ruleId && x.EndTs == null && x.Shelved).ToListAsync(ct))
            {
                alarm.Shelved = false;
                db.AlarmActivities.Add(new AlarmActivity { AlarmEventId = alarm.Id, Ts = now, User = user, Action = "unshelve", Note = note });
                notify.Add(alarm);
            }
            await db.SaveChangesAsync(ct);
            var next = new Dictionary<int, AlarmShelve>(_active); next.Remove(ruleId); _active = next;
        }
        finally { _gate.Release(); }

        foreach (var alarm in notify) await NotifyAsync(alarm, "unshelve", ct);
        return true;
    }

    // 到期自动解除。返回本轮到期的规则数（个别规则可能因已无暂挂行而未真正清除）。
    public async Task<int> ExpireDueAsync(long now, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var due = _active.Values.Where(x => x.UntilTs <= now).Select(x => x.RuleId).ToList();
        foreach (var ruleId in due) await UnshelveAsync(ruleId, null, "暂挂到期自动解除", ct);
        return due.Count;
    }

    private async Task NotifyAsync(AlarmEvent alarm, string action, CancellationToken ct)
    {
        try { await _notifier.PublishAlarmAsync(alarm, action, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "报警暂挂状态推送失败"); }
    }
}
