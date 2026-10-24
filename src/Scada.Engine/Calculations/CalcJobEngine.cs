using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;

namespace Scada.Engine.Calculations;

// 脚本写现场测点的出口：必须以任务的执行身份复核 Control 权限，并经控制命令服务下发与审计。
public interface ICalcTagWriter
{
    Task<string?> WriteAsync(CalcJob job, int tagId, double value, CancellationToken ct);
    // 核对执行身份：用户存在且启用，并对全部可写测点有 Control；返回问题描述，正常返回 null。
    Task<string?> CheckIdentityAsync(CalcJob job, IReadOnlyCollection<int> writeTags, CancellationToken ct);
}

public sealed class ControlledCalcTagWriter(AccessModelProvider access, ControlCommandService commands, IDbContextFactory<ConfigDbContext> factory) : ICalcTagWriter
{
    public async Task<string?> CheckIdentityAsync(CalcJob job, IReadOnlyCollection<int> writeTags, CancellationToken ct)
    {
        if (job.RunAsUserId is not int userId) return "任务没有执行身份";
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var user = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.Enabled }).SingleOrDefaultAsync(ct);
            if (user is null) return $"执行身份 {job.RunAsUserName} 已被删除";
            if (!user.Enabled) return $"执行身份 {job.RunAsUserName} 已停用";
        }
        var context = await access.ForUserAsync(userId, ct);
        var denied = writeTags.Where(id => !context.CanTag(Permission.Control, id)).ToArray();
        return denied.Length == 0 ? null : $"执行身份 {job.RunAsUserName} 对测点 {string.Join(",", denied.Select(x => "#" + x))} 已无控制权限";
    }

    public async Task<string?> WriteAsync(CalcJob job, int tagId, double value, CancellationToken ct)
    {
        var who = $"{job.RunAsUserName}（计算任务 {job.Name}）";
        string? denied = null;
        if (job.RunAsUserId is not int userId) denied = "任务没有执行身份";
        else if (!(await access.ForUserAsync(userId, ct)).CanTag(Permission.Control, tagId)) denied = "执行身份无该测点区域的控制权限";
        if (denied is not null)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = job.RunAsUserId, UserName = who, TagId = tagId, Ts = ScadaTime.NowMs(), Action = $"计算任务写测点 #{tagId}",
                NewValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Result = "拒绝：" + denied, TargetType = "CalcJob", TargetId = job.Id.ToString()
            });
            await db.SaveChangesAsync(ct);
            return denied;
        }
        var result = await commands.ExecuteAsync(tagId, value, job.RunAsUserId, who, ct);
        return result.Success ? null : result.Message;
    }
}

public sealed record CalcJobStatus(int JobId, IReadOnlyDictionary<string, double> Values, string PeriodKey, long PeriodStartTs, long? LastRunTs, string? LastError, string? LastEvent, long? LastScriptSteps, double? LastScriptMs);

// 计算任务引擎：按周期采样输入、积分/累计、周期复位留快照、定期落盘以便断电续算，并执行受限脚本。
public sealed class CalcJobEngine(
    IDbContextFactory<ConfigDbContext> factory,
    IConfigSnapshot configuration,
    IRealtimeCache cache,
    IRealtimeBus bus,
    ICalcTagWriter writer,
    IOptions<ScadaSettings> options,
    ILogger<CalcJobEngine> logger) : BackgroundService
{
    private sealed class Running
    {
        public required CalcJob Job;
        public JobRuntimeState State = new();
        public CalcScript? Script;
        public IReadOnlyList<TouPeriod> Tou = [];
        public Dictionary<string, int> Outputs = new();
        public long Due;
        public long? LastRunTs;
        public string? Error;
        public bool Dirty;
        public long? Steps; public double? Ms;
        public string? IdentityProblem; public long IdentityCheckedTs;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, Running> _jobs = new();
    private int _configVersion = -1;
    private volatile bool _reload = true;
    private long _lastPersist;
    private TimeZoneInfo? _zone;

    public void RequestReload() => _reload = true;

    // 执行身份失效（用户删除/停用/权限收回）的脚本任务：作为系统诊断告警出现在 /health 与诊断包中。
    private volatile IReadOnlyList<string> _warnings = [];
    public IReadOnlyList<string> Warnings() => _warnings;
    private void UpdateWarnings() => _warnings = _jobs.Values.Where(x => x.Job.Enabled && x.IdentityProblem is not null)
        .Select(x => $"计算任务「{x.Job.Name}」无法写测点：{x.IdentityProblem}；请由有控制权限的人员重新保存任务").ToList();

    private TimeZoneInfo Zone => _zone ??= ResolveZone(options.Value.ShiftTimeZone);
    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { return TimeZoneInfo.Local; }
    }

    public async Task<IReadOnlyList<CalcJobStatus>> StatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _jobs.Values.Select(r => new CalcJobStatus(r.Job.Id, new Dictionary<string, double>(r.State.Values), r.State.PeriodKey, r.State.PeriodStartTs, r.LastRunTs, r.Error, r.State.LastEvent, r.Steps, r.Ms)).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task TickAsync(long now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_reload || configuration.Version != _configVersion) await LoadAsync(ct);
            foreach (var running in _jobs.Values)
            {
                if (!running.Job.Enabled || running.Due > now) continue;
                running.Due = now + Math.Max(100, running.Job.IntervalMs);
                await RunAsync(running, now, ct);
            }
            if (now - _lastPersist >= Math.Max(1, options.Value.CalcStatePersistSec) * 1000L) await PersistAsync(now, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        _reload = false; _configVersion = configuration.Version;
        await using var db = await factory.CreateDbContextAsync(ct);
        var jobs = await db.CalcJobs.AsNoTracking().ToListAsync(ct);
        var states = await db.CalcJobStates.AsNoTracking().ToDictionaryAsync(x => x.JobId, ct);
        var outputs = await db.Tags.AsNoTracking().Where(x => x.Kind == TagKind.Derived && x.JobId != null)
            .Select(x => new { x.Id, JobId = x.JobId!.Value, x.JobOutput }).ToListAsync(ct);
        foreach (var removed in _jobs.Keys.Where(id => jobs.All(j => j.Id != id)).ToList()) _jobs.Remove(removed);
        UpdateWarnings();
        foreach (var job in jobs)
        {
            if (!_jobs.TryGetValue(job.Id, out var running))
            {
                running = new Running { Job = job };
                if (states.TryGetValue(job.Id, out var saved))
                {
                    try { running.State = JsonSerializer.Deserialize<JobRuntimeState>(saved.StateJson) ?? new(); }
                    catch (JsonException ex) { logger.LogWarning(ex, "计算任务 {Job} 的持久化状态损坏，从零开始", job.Name); }
                    // 断电期间的时长不能当作运行/积分区间：清除上次采样时刻，下一次采样重新起算。
                    running.State.LastTs = null; running.State.LastValue = null;
                    if (running.State.LastRunning == true) running.State.LastRunning = null;
                }
                _jobs[job.Id] = running;
            }
            if (running.Job.Revision != job.Revision) { running.IdentityProblem = null; running.IdentityCheckedTs = 0; }
            running.Job = job; running.Error = null; running.Script = null;
            running.Outputs = outputs.Where(x => x.JobId == job.Id && x.JobOutput != null).ToDictionary(x => x.JobOutput!, x => x.Id);
            try
            {
                running.Tou = job.Kind == CalcJobKind.Energy ? CalcJobMath.ParseTou(job.TouJson) : [];
                if (job.Kind == CalcJobKind.Script) running.Script = CalcScript.Compile(job.Script, CalcScript.ParseIds(job.ReadTags), CalcScript.ParseIds(job.WriteTags), Limits());
            }
            catch (Exception ex) when (ex is TagExpressionException or JsonException) { running.Error = "配置无效：" + ex.Message; }
        }
    }

    public ScriptLimits Limits() => new(MaxSteps: options.Value.CalcScriptMaxSteps, TimeoutMs: options.Value.CalcScriptTimeoutMs);

    private JobSample? Sample(int? tagId)
    {
        if (tagId is not int id || cache.Get(id) is not { } value) return null;
        return new JobSample(value.Value, value.Quality == TagQuality.Good, value.Ts);
    }

    private async Task RunAsync(Running running, long now, CancellationToken ct)
    {
        var job = running.Job; var state = running.State;
        running.LastRunTs = now; running.Dirty = true;
        if (running.Error is not null && running.Error.StartsWith("配置无效", StringComparison.Ordinal)) { PublishAll(running, now, TagQuality.Bad); return; }
        if (job.Kind != CalcJobKind.Script)
        {
            var snapshot = CalcJobMath.Roll(job, state, now, Zone);
            if (snapshot is not null) await SaveSnapshotAsync(job.Id, "周期复位", snapshot, now, ct);
        }
        var quality = TagQuality.Good;
        try
        {
            var sample = Sample(job.InputTagId);
            switch (job.Kind)
            {
                case CalcJobKind.Accumulator:
                    CalcJobMath.Accumulate(job, state, sample, now);
                    if (sample is not { Good: true }) quality = TagQuality.Uncertain;
                    break;
                case CalcJobKind.Energy:
                    CalcJobMath.Energy(job, state, sample, now, running.Tou, Zone);
                    if (state.LastValue is null) quality = TagQuality.Uncertain;
                    break;
                case CalcJobKind.RunHours:
                    CalcJobMath.RunHours(job, state, sample, now);
                    if (state.LastRunning is null) quality = TagQuality.Uncertain;
                    break;
                case CalcJobKind.Script:
                    await RunScriptAsync(running, now, ct);
                    break;
            }
            running.Error = null;
        }
        catch (TagExpressionException ex)
        {
            running.Error = ex.Message; quality = TagQuality.Bad;
        }
        PublishAll(running, now, quality);
    }

    private async Task RunScriptAsync(Running running, long now, CancellationToken ct)
    {
        var job = running.Job; var script = running.Script!;
        var maxAge = Math.Max(1, job.MaxGapSec) * 1000L;
        double Read(int id)
        {
            var value = cache.Get(id);
            if (value is null || value.Quality != TagQuality.Good || !double.IsFinite(value.Value) || now - value.Ts > maxAge)
                throw new TagExpressionException($"输入 #{id} 缺失、质量非正常或已过期");
            return value.Value;
        }
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(now), Zone);
        var builtIns = new Dictionary<string, double>
        {
            ["dt"] = running.State.LastTs is long last ? (now - last) / 1000.0 : 0,
            ["hour"] = local.Hour, ["minute"] = local.Minute, ["weekday"] = (int)local.DayOfWeek, ["day"] = local.Day, ["month"] = local.Month
        };
        running.State.LastTs = now;
        var result = script.Execute(Read, builtIns);
        running.Steps = result.Steps; running.Ms = result.ElapsedMs;
        foreach (var (name, value) in result.Outputs) running.State.Values[name] = value;
        if (script.Writes.Count > 0 && (running.IdentityProblem is not null || now - running.IdentityCheckedTs >= 60_000))
        {
            running.IdentityCheckedTs = now;
            var problem = await writer.CheckIdentityAsync(job, script.Writes, ct);
            if (problem != running.IdentityProblem)
            {
                if (problem is not null) logger.LogWarning("计算任务 {Job} 执行身份失效：{Problem}", job.Name, problem);
                running.IdentityProblem = problem; UpdateWarnings();
            }
            if (problem is not null) throw new TagExpressionException("执行身份失效：" + problem);
        }
        var errors = new List<string>();
        foreach (var (tagId, value) in result.Writes)
        {
            // 只在目标值变化时下发，避免周期任务每次都刷写现场。
            if (running.State.LastWrites.TryGetValue(tagId, out var previous) && Math.Abs(previous - value) <= 1e-9) continue;
            var error = await writer.WriteAsync(job, tagId, value, ct);
            if (error is null) running.State.LastWrites[tagId] = value;
            else errors.Add($"写 #{tagId} 失败：{error}");
        }
        if (errors.Count > 0) running.State.LastEvent = string.Join("；", errors);
    }

    private void PublishAll(Running running, long now, TagQuality quality)
    {
        foreach (var (name, tagId) in running.Outputs)
        {
            var hasValue = running.State.Values.TryGetValue(name, out var value);
            var q = hasValue ? quality : TagQuality.Uncertain;
            var current = new TagValue(tagId, hasValue ? value : 0, q, now, null, quality == TagQuality.Good ? null : running.Error);
            var old = cache.Get(tagId); if (old is not null && old.Ts >= now) continue;
            cache.Set(current); bus.Publish(current);
        }
    }

    private async Task SaveSnapshotAsync(int jobId, string reason, PeriodSnapshot snapshot, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.CalcJobSnapshots.Add(new CalcJobSnapshot { JobId = jobId, Reason = reason, PeriodKey = snapshot.PeriodKey, PeriodStartTs = snapshot.PeriodStartTs, Ts = now, ValuesJson = JsonSerializer.Serialize(snapshot.Values) });
        await db.SaveChangesAsync(ct);
        // 复位前状态立即落盘，避免快照已写但周期量未清零时掉电导致重复计入。
        await PersistOneAsync(db, _jobs[jobId], now, ct);
    }

    private async Task PersistAsync(long now, CancellationToken ct)
    {
        _lastPersist = now;
        var dirty = _jobs.Values.Where(x => x.Dirty).ToList();
        if (dirty.Count == 0) return;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            foreach (var running in dirty) await PersistOneAsync(db, running, now, ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException) { logger.LogWarning(ex, "计算任务状态落盘失败"); }
    }

    private static async Task PersistOneAsync(ConfigDbContext db, Running running, long now, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(running.State);
        var updated = await db.CalcJobStates.Where(x => x.JobId == running.Job.Id).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.StateJson, json).SetProperty(x => x.UpdatedTs, now).SetProperty(x => x.Revision, running.Job.Revision).SetProperty(x => x.LastError, running.Error), ct);
        if (updated == 0 && await db.CalcJobs.AnyAsync(x => x.Id == running.Job.Id, ct))
        {
            db.CalcJobStates.Add(new CalcJobState { JobId = running.Job.Id, StateJson = json, UpdatedTs = now, Revision = running.Job.Revision, LastError = running.Error });
            await db.SaveChangesAsync(ct);
        }
        running.Dirty = false;
    }

    public Task FlushAsync(CancellationToken ct = default) => WithGate(() => PersistAsync(ScadaTime.NowMs(), ct), ct);

    // 手动复位周期量（留快照）。
    public Task<bool> ResetAsync(int jobId, string reason, CancellationToken ct = default) => WithGate(async () =>
    {
        if (_reload) await LoadAsync(ct);
        if (!_jobs.TryGetValue(jobId, out var running)) return false;
        var now = ScadaTime.NowMs();
        var snapshot = CalcJobMath.ResetPeriod(running.State, running.State.PeriodKey);
        await SaveSnapshotAsync(jobId, reason, snapshot, now, ct);
        PublishAll(running, now + 1, TagQuality.Good);
        return true;
    }, ct);

    // 维护确认：把“自上次维护起”的基准移到当前累计小时，并留快照。
    public Task<bool> MaintenanceDoneAsync(int jobId, CancellationToken ct = default) => WithGate(async () =>
    {
        if (_reload) await LoadAsync(ct);
        if (!_jobs.TryGetValue(jobId, out var running) || running.Job.Kind != CalcJobKind.RunHours) return false;
        var now = ScadaTime.NowMs();
        var before = new PeriodSnapshot(running.State.PeriodKey, running.State.PeriodStartTs, new Dictionary<string, double>(running.State.Values));
        running.State.MaintenanceBase = running.State.Get("hours");
        running.State.Values["sinceMaintenance"] = 0; running.State.Values["maintenanceDue"] = 0;
        await SaveSnapshotAsync(jobId, "维护确认", before, now, ct);
        PublishAll(running, now + 1, TagQuality.Good);
        return true;
    }, ct);

    private async Task<T> WithGate<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); } finally { _gate.Release(); }
    }
    private Task WithGate(Func<Task> action, CancellationToken ct) => WithGate(async () => { await action(); return true; }, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await TickAsync(ScadaTime.NowMs(), stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "计算任务执行异常"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await FlushAsync(CancellationToken.None); } catch (Exception ex) { logger.LogWarning(ex, "停止时保存计算任务状态失败"); }
    }
}
