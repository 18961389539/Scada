using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Scripting;
using Scada.Data;

namespace Scada.Engine.Calculations;

public sealed record CalcJobRequest(
    string Name, CalcJobKind Kind, bool Enabled = true, int AreaId = Area.DefaultId, int IntervalMs = 1000,
    int? InputTagId = null, AccumulatorMode Mode = AccumulatorMode.Pulse, double Factor = 1, double? CounterMax = null, double? MaxDelta = null,
    BadQualityPolicy BadQuality = BadQualityPolicy.Hold, int MaxGapSec = 60, double RunThreshold = 0.5, double? MaintenanceHours = null,
    CalcResetPeriod ResetPeriod = CalcResetPeriod.Day, string? ShiftStarts = null, string? TouJson = null,
    string? Script = null, string? ReadTags = null, string? WriteTags = null, string? Unit = null, long Revision = 0,
    CalcTrigger Trigger = CalcTrigger.Periodic, double ChangeDeadband = 0, int ChangeCooldownMs = 1000,
    AlarmLevel AlarmMinLevel = AlarmLevel.Info, bool AlarmOnRaise = true, bool AlarmOnAck = false, bool AlarmOnRecover = false,
    int HistoryWindowSec = 0, bool AlarmSnapshot = false, string? Libraries = null,
    double? PreviewArgument = null, ScriptPreviewOverride[]? PreviewInputs = null);

public sealed record ScriptPreviewOverride(int TagId, double Value, string Quality, long Timestamp);
public sealed record ScriptPreviewSample(int TagId, string Name, double Value, string Quality, long Timestamp, bool Good);
public sealed record CSharpPreviewResult(CSharpScriptReply Reply, ScriptPreviewSample[] Inputs);

public sealed record ScriptLibraryInput(string Name, string Source, long Revision);
public sealed record ScriptScreenReference(int Id, string Name, bool Published);
public sealed record ScriptTagReference(string Token, int? TagId, string? CurrentName, bool Resolved);
public sealed record ScriptJobReference(int Id, string Name);
public sealed record ScriptReferenceList(ScriptScreenReference[] Screens, ScriptTagReference[] Tags);
public sealed record CalcJobOutput(string Name, int TagId);
public sealed record CalcJobInfo(CalcJob Job, IReadOnlyList<CalcJobOutput> Outputs, CalcJobStatus? Status);

public sealed class CalcJobConfiguration(IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CalcJobEngine engine)
{
    public const int MaxJobs = 500;
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CalcJobInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var jobs = await db.CalcJobs.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var outputs = await db.Tags.AsNoTracking().Where(x => x.Kind == TagKind.Derived).Select(x => new { x.Id, x.JobId, x.JobOutput }).ToListAsync(ct);
        var statuses = (await engine.StatusAsync(ct)).ToDictionary(x => x.JobId);
        return jobs.Select(job => new CalcJobInfo(job,
            outputs.Where(x => x.JobId == job.Id).Select(x => new CalcJobOutput(x.JobOutput ?? "", x.Id)).ToList(),
            statuses.GetValueOrDefault(job.Id))).ToList();
    }

    public async Task<CalcJob?> GetAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        return await db.CalcJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<CalcJobSnapshot>> SnapshotsAsync(int id, int take, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        return await runtime.CalcJobSnapshots.AsNoTracking().Where(x => x.JobId == id).OrderByDescending(x => x.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    public static CalcJob Map(CalcJobRequest r, int id = 0) => new()
    {
        Id = id, Name = r.Name?.Trim() ?? "", Kind = r.Kind, Enabled = r.Enabled, AreaId = r.AreaId, IntervalMs = r.IntervalMs,
        InputTagId = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp ? null : r.InputTagId, Mode = r.Mode, Factor = r.Factor, CounterMax = r.CounterMax, MaxDelta = r.MaxDelta,
        BadQuality = r.BadQuality, MaxGapSec = r.MaxGapSec, RunThreshold = r.RunThreshold, MaintenanceHours = r.MaintenanceHours,
        ResetPeriod = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp ? CalcResetPeriod.None : r.ResetPeriod,
        ShiftStarts = string.IsNullOrWhiteSpace(r.ShiftStarts) ? "08:00,20:00" : r.ShiftStarts.Trim(),
        TouJson = string.IsNullOrWhiteSpace(r.TouJson) ? "[]" : r.TouJson.Trim(),
        Script = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp ? r.Script?.Trim() ?? "" : "",
        ReadTags = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp ? r.ReadTags?.Trim() ?? "" : "",
        WriteTags = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp ? r.WriteTags?.Trim() ?? "" : "",
        Trigger = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp ? r.Trigger : CalcTrigger.Periodic,
        ChangeDeadband = r.ChangeDeadband, ChangeCooldownMs = r.ChangeCooldownMs,
        AlarmMinLevel = r.AlarmMinLevel, AlarmOnRaise = r.AlarmOnRaise, AlarmOnAck = r.AlarmOnAck, AlarmOnRecover = r.AlarmOnRecover,
        HistoryWindowSec = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp ? r.HistoryWindowSec : 0,
        AlarmSnapshot = r.Kind is CalcJobKind.Script or CalcJobKind.CSharp && r.AlarmSnapshot,
        Libraries = r.Kind == CalcJobKind.CSharp ? r.Libraries?.Trim() ?? "" : ""
    };

    public async Task<(string Reads, string Writes)> NormalizeScriptTagsAsync(string? reads, string? writes, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var catalog = await LoadCatalogAsync(db, ct);
        return (string.Join(",", ScriptTagNames.Resolve(reads, catalog)), string.Join(",", ScriptTagNames.Resolve(writes, catalog)));
    }

    private static async Task<IReadOnlyList<ScriptTagRef>> LoadCatalogAsync(ConfigDbContext db, CancellationToken ct)
    {
        var tags = await db.Tags.AsNoTracking().Select(x => new { x.Id, x.Name, x.DeviceId }).ToListAsync(ct);
        var deviceIds = tags.Where(x => x.DeviceId != null).Select(x => x.DeviceId!.Value).Distinct().ToArray();
        var devices = deviceIds.Length == 0
            ? new Dictionary<int, string>()
            : await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return tags.Select(x => new ScriptTagRef(x.Id, x.Name, x.DeviceId is int id && devices.TryGetValue(id, out var name) ? name : null)).ToArray();
    }

    // 校验并返回输出名列表；任何问题抛 CalculationRequestException(400)。
    public async Task<IReadOnlyList<string>> ValidateAsync(ConfigDbContext db, CalcJob job, CancellationToken ct)
    {
        void Fail(string message) => throw new CalculationRequestException(400, message);
        if (job.Name.Length is 0 or > 80) Fail("名称须为 1～80 个字符");
        if (!Enum.IsDefined(job.Kind)) Fail("任务类型无效");
        if (!await db.Areas.AnyAsync(x => x.Id == job.AreaId, ct)) Fail("区域不存在");
        if (job.IntervalMs is < 200 or > 3_600_000) Fail("执行周期须为 200～3600000 毫秒");
        if (job.IsScript && job.IntervalMs < 1000) Fail("脚本任务执行周期不得小于 1000 毫秒");
        if (!Enum.IsDefined(job.Trigger)) Fail("触发方式无效");
        if (!job.IsScript) job.Trigger = CalcTrigger.Periodic;
        if (!double.IsFinite(job.ChangeDeadband) || job.ChangeDeadband < 0) Fail("变化死区须为非负数");
        if (job.ChangeCooldownMs is < 0 or > 3_600_000) Fail("变化冷却须为 0～3600000 毫秒");
        if (!Enum.IsDefined(job.AlarmMinLevel)) Fail("报警等级无效");
        if (job.Trigger == CalcTrigger.OnAlarm && !job.AlarmOnRaise && !job.AlarmOnAck && !job.AlarmOnRecover) Fail("报警触发至少选择产生、确认或恢复之一");
        if (job.IsScript && job.HistoryWindowSec is < 0 or > 3600) Fail("历史窗口须为 0～3600 秒");
        if (job.MaxGapSec is < 1 or > 86_400) Fail("最大间隔须为 1～86400 秒");
        if (!double.IsFinite(job.Factor) || job.Factor <= 0) Fail("换算系数须为正数");
        if (job.CounterMax is double max && (!double.IsFinite(max) || max <= 0)) Fail("计数器满量程须为正数");
        if (job.MaxDelta is double delta && (!double.IsFinite(delta) || delta <= 0)) Fail("单次增量上限须为正数");
        if (job.MaintenanceHours is double hours && (!double.IsFinite(hours) || hours <= 0)) Fail("维护阈值须为正数");
        if (!double.IsFinite(job.RunThreshold)) Fail("运行阈值无效");
        try
        {
            if (job.ResetPeriod == CalcResetPeriod.Shift) CalcJobMath.ParseShifts(job.ShiftStarts);
            if (job.Kind == CalcJobKind.Energy) CalcJobMath.ParseTou(job.TouJson);
        }
        catch (Exception ex) when (ex is TagExpressionException or JsonException) { Fail(ex.Message); }

        if (!job.IsScript)
        {
            if (job.InputTagId is not int input) { Fail("请选择输入测点"); return []; }
            var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input, ct);
            if (tag is null) Fail($"输入测点 #{input} 不存在");
            else if (tag.Kind == TagKind.Derived && tag.JobId == job.Id) Fail("输入不能是本任务自己的输出");
            return CalcJobMath.Outputs(job.Kind);
        }

        IReadOnlyList<string> outputs = [];
        try
        {
            var catalog = await LoadCatalogAsync(db, ct);
            job.ReadTags = string.Join(",", ScriptTagNames.Resolve(job.ReadTags, catalog));
            job.WriteTags = string.Join(",", ScriptTagNames.Resolve(job.WriteTags, catalog));
            if (job.Kind == CalcJobKind.CSharp)
            {
                job.Libraries = string.Join(",", ParseLibraryNames(job.Libraries));
                var bundle = await BundleLibrariesAsync(db, job.Libraries, publishedOnly: false, ct);
                var result = await engine.CompileCSharpAsync(job.Script, bundle, ct);
                if (!result.Success) Fail(string.Join("\n", result.Diagnostics));
                outputs = result.OutputNames;
            }
            else if (job.Libraries.Length > 0) Fail("只有 C# 脚本可以引用脚本库");
            else outputs = CalcScript.Compile(job.Script, CalcScript.ParseIds(job.ReadTags), CalcScript.ParseIds(job.WriteTags), engine.Limits()).Outputs;
        }
        catch (TagExpressionException ex) { Fail(ex.Message); }
        var reads = CalcScript.ParseIds(job.ReadTags).ToArray(); var writes = CalcScript.ParseIds(job.WriteTags).ToArray();
        if (job.Trigger == CalcTrigger.OnChange && reads.Length == 0) Fail("变化触发至少要有一个可读测点");
        if (job.HistoryWindowSec > 0 && reads.Length > 8) Fail("开启历史快照时，可读测点最多 8 个");
        if (reads.Length > 64 || writes.Length > 16) Fail("可读白名单最多 64 个、可写白名单最多 16 个测点");
        var known = await db.Tags.AsNoTracking().Where(x => reads.Contains(x.Id) || writes.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var id in reads)
        {
            if (!known.TryGetValue(id, out var tag)) Fail($"可读测点 #{id} 不存在");
            else if (tag.DataType == TagDataType.String && job.Kind != CalcJobKind.CSharp) Fail($"字符串测点 #{id} 只能由 C# 脚本通过 ReadText 读取");
        }
        foreach (var id in writes)
        {
            if (!known.TryGetValue(id, out var tag)) Fail($"可写测点 #{id} 不存在");
            else if (tag.Kind != TagKind.Physical || !tag.Writable) Fail($"可写测点 #{id} 须为可写的现场测点；派生结果请用 @输出");
            else if (tag.DataType == TagDataType.String) Fail($"字符串测点 #{id} 不能由计算脚本写入");
        }
        var owners = await db.Tags.AsNoTracking().Where(tag => tag.Kind == TagKind.Derived && tag.JobId != null).ToDictionaryAsync(tag => tag.Id, tag => tag.JobId!.Value, ct);
        var jobs = await db.CalcJobs.AsNoTracking().ToDictionaryAsync(item => item.Id, ct);
        jobs[job.Id] = job;
        var path = new HashSet<int>(); var visited = new HashSet<int>();
        bool Cyclic(int id)
        {
            if (path.Contains(id)) return true;
            if (!visited.Add(id) || !jobs.TryGetValue(id, out var current)) return false;
            path.Add(id);
            var inputs = current.IsScript ? CalcScript.ParseIds(current.ReadTags) : current.InputTagId is int inputId ? new HashSet<int> { inputId } : new HashSet<int>();
            foreach (var input in inputs) if (owners.TryGetValue(input, out var owner) && Cyclic(owner)) return true;
            path.Remove(id); return false;
        }
        if (Cyclic(job.Id)) Fail("计算任务存在自引用或循环依赖");
        return outputs;
    }

    public async Task<CSharpPreviewResult> CheckCSharpAsync(CalcJobRequest request, bool preview, CancellationToken ct)
    {
        if (request.Kind != CalcJobKind.CSharp) throw new CalculationRequestException(400, "Only C# scripts support compile and preview.");
        var job = Map(request with { Name = string.IsNullOrWhiteSpace(request.Name) ? "C# preview" : request.Name });
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        await ValidateAsync(db, job, ct);
        job.PublishedLibrarySource = await BundleLibrariesAsync(db, job.Libraries, publishedOnly: true, ct);
        return preview
            ? await engine.PreviewCSharpAsync(job, ct, request.PreviewArgument, request.PreviewInputs)
            : new(await engine.CompileCSharpAsync(job.Script, job.PublishedLibrarySource, ct), []);
    }

    public async Task<CalcJob> SaveAsync(int? id, CalcJobRequest request, int userId, string userName, CancellationToken ct)
    {
        var candidate = Map(request, id ?? 0);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var outputs = await ValidateAsync(db, candidate, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        CalcJob job; string? before = null;
        if (id is int existing)
        {
            job = await db.CalcJobs.SingleOrDefaultAsync(x => x.Id == existing, ct) ?? throw new CalculationRequestException(404, "计算任务不存在");
            if (request.Revision != job.Revision) throw new CalculationRequestException(409, "计算任务已被修改，请重新读取后核对");
            if (job.Kind != candidate.Kind) throw new CalculationRequestException(400, "不能修改任务类型，请新建任务");
            before = JsonSerializer.Serialize(job, AuditJson);
        }
        else
        {
            if (request.Revision != 0) throw new CalculationRequestException(400, "新建计算任务不能携带旧版本");
            if (await db.CalcJobs.CountAsync(ct) >= MaxJobs) throw new CalculationRequestException(409, $"计算任务已达 {MaxJobs} 个上限");
            job = new CalcJob();
        }
        if (await db.CalcJobs.AnyAsync(x => x.Name == candidate.Name && x.Id != candidate.Id, ct)) throw new CalculationRequestException(409, "任务名称已存在");

        var revision = id is null ? 1 : checked(job.Revision + 1);
        var keepId = job.Id;
        var publishedScript = job.PublishedScript;
        var publishedReads = job.PublishedReadTags;
        var publishedWrites = job.PublishedWriteTags;
        var publishedWindow = job.PublishedHistoryWindowSec;
        var publishedAlarms = job.PublishedAlarmSnapshot;
        var publishedRevision = job.PublishedRevision;
        var publishedLibraries = job.PublishedLibraries;
        var publishedLibrarySource = job.PublishedLibrarySource;
        var publishedTrigger = job.PublishedTrigger;
        var publishedInterval = job.PublishedIntervalMs;
        var publishedDeadband = job.PublishedChangeDeadband;
        var publishedCooldown = job.PublishedChangeCooldownMs;
        var publishedLevel = job.PublishedAlarmMinLevel;
        var publishedRaise = job.PublishedAlarmOnRaise;
        var publishedAck = job.PublishedAlarmOnAck;
        var publishedRecover = job.PublishedAlarmOnRecover;
        db.Entry(job).CurrentValues.SetValues(candidate);
        if (id is null) db.CalcJobs.Add(job); else job.Id = keepId;
        job.Revision = revision; job.UpdatedTs = ScadaTime.NowMs();
        if (id is null && job.IsScript)
        {
            CopyPublished(job, revision);
            job.PublishedLibrarySource = await BundleLibrariesAsync(db, job.Libraries, publishedOnly: true, ct);
        }
        else if (id is not null)
        {
            job.PublishedScript = publishedScript; job.PublishedReadTags = publishedReads; job.PublishedWriteTags = publishedWrites;
            job.PublishedHistoryWindowSec = publishedWindow; job.PublishedAlarmSnapshot = publishedAlarms; job.PublishedRevision = publishedRevision;
            job.PublishedLibraries = publishedLibraries; job.PublishedLibrarySource = publishedLibrarySource;
            job.PublishedTrigger = publishedTrigger; job.PublishedIntervalMs = publishedInterval;
            job.PublishedChangeDeadband = publishedDeadband; job.PublishedChangeCooldownMs = publishedCooldown;
            job.PublishedAlarmMinLevel = publishedLevel; job.PublishedAlarmOnRaise = publishedRaise;
            job.PublishedAlarmOnAck = publishedAck; job.PublishedAlarmOnRecover = publishedRecover;
        }
        // 写现场测点的脚本以保存人的身份执行（端点已核对其 Control 权限）。
        if (job.Kind == CalcJobKind.CSharp || job.IsScript && job.WriteTags.Length > 0) { job.RunAsUserId = userId; job.RunAsUserName = userName; }
        else { job.RunAsUserId = null; job.RunAsUserName = ""; }
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);

        var removed = await SyncOutputsAsync(db, job, outputs, request.Unit, ct);
        if (id is null && job.IsScript) await RememberPublicationAsync(db, job, userName, ct);
        runtime.OperationLogs.Add(AuditLog(job, id is null ? "创建计算任务" : "修改计算任务", before, JsonSerializer.Serialize(job, AuditJson), userId, userName));
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        await runtime.SaveChangesAsync(ct);
        try { await CloseRemovedAlarmsAsync(removed, "计算任务输出移除", ct); }
        finally { engine.RequestReload(); }
        return job;
    }

    public async Task<IReadOnlyList<CalcJobPublication>> PublicationsAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        return await db.CalcJobPublications.AsNoTracking().Where(x => x.JobId == id).OrderByDescending(x => x.Id).Take(20).ToListAsync(ct);
    }

    public async Task<CalcJob> PublishAsync(int id, long revision, int userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var job = await db.CalcJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "计算任务不存在");
        if (!job.IsScript) throw new CalculationRequestException(400, "只有脚本任务可以发布");
        if (job.Revision != revision) throw new CalculationRequestException(409, "计算任务已被修改，请重新读取后核对");
        var bundle = job.Kind == CalcJobKind.CSharp ? await BundleLibrariesAsync(db, job.Libraries, publishedOnly: true, ct) : "";
        if (job.Kind == CalcJobKind.CSharp)
        {
            var compiled = await engine.CompileCSharpAsync(job.Script, bundle, ct);
            if (!compiled.Success) throw new CalculationRequestException(400, string.Join("\n", compiled.Diagnostics));
        }
        if (job.PublishedRevision == job.Revision && job.PublishedScript == job.Script && job.PublishedReadTags == job.ReadTags && job.PublishedWriteTags == job.WriteTags
            && job.PublishedHistoryWindowSec == job.HistoryWindowSec && job.PublishedAlarmSnapshot == job.AlarmSnapshot
            && job.PublishedLibraries == job.Libraries && job.PublishedLibrarySource == bundle
            && job.PublishedTrigger == job.Trigger && job.PublishedIntervalMs == job.IntervalMs
            && job.PublishedChangeDeadband == job.ChangeDeadband && job.PublishedChangeCooldownMs == job.ChangeCooldownMs
            && job.PublishedAlarmMinLevel == job.AlarmMinLevel && job.PublishedAlarmOnRaise == job.AlarmOnRaise
            && job.PublishedAlarmOnAck == job.AlarmOnAck && job.PublishedAlarmOnRecover == job.AlarmOnRecover) return job;
        var before = JsonSerializer.Serialize(job, AuditJson);
        CopyPublished(job, checked(job.Revision + 1));
        job.PublishedLibrarySource = bundle;
        job.Revision = job.PublishedRevision; job.UpdatedTs = ScadaTime.NowMs();
        await RememberPublicationAsync(db, job, userName, ct);
        runtime.OperationLogs.Add(AuditLog(job, "发布计算任务脚本", before, JsonSerializer.Serialize(job, AuditJson), userId, userName));
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);
        engine.RequestReload();
        return job;
    }

    public async Task<CalcJob> RollbackAsync(int id, long publicationId, long revision, int userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var job = await db.CalcJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "计算任务不存在");
        if (!job.IsScript) throw new CalculationRequestException(400, "只有脚本任务可以回退");
        if (job.Revision != revision) throw new CalculationRequestException(409, "计算任务已被修改，请重新读取后核对");
        var publication = await db.CalcJobPublications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == publicationId && x.JobId == id, ct) ?? throw new CalculationRequestException(404, "找不到要回退的发布版本");
        var before = JsonSerializer.Serialize(job, AuditJson);
        job.Script = publication.Script; job.ReadTags = publication.ReadTags; job.WriteTags = publication.WriteTags;
        job.HistoryWindowSec = publication.HistoryWindowSec; job.AlarmSnapshot = publication.AlarmSnapshot;
        job.Trigger = publication.Trigger; job.IntervalMs = publication.IntervalMs;
        job.ChangeDeadband = publication.ChangeDeadband; job.ChangeCooldownMs = publication.ChangeCooldownMs;
        job.AlarmMinLevel = publication.AlarmMinLevel; job.AlarmOnRaise = publication.AlarmOnRaise;
        job.AlarmOnAck = publication.AlarmOnAck; job.AlarmOnRecover = publication.AlarmOnRecover;
        job.Libraries = publication.Libraries;
        CopyPublished(job, checked(job.Revision + 1));
        job.PublishedLibrarySource = publication.LibrarySource;
        job.Revision = job.PublishedRevision; job.UpdatedTs = ScadaTime.NowMs();
        await RememberPublicationAsync(db, job, userName, ct);
        runtime.OperationLogs.Add(AuditLog(job, "回退计算任务脚本", before, JsonSerializer.Serialize(job, AuditJson), userId, userName));
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);
        engine.RequestReload();
        return job;
    }

    private static void CopyPublished(CalcJob job, long revision)
    {
        job.PublishedScript = job.Script; job.PublishedReadTags = job.ReadTags; job.PublishedWriteTags = job.WriteTags;
        job.PublishedHistoryWindowSec = job.HistoryWindowSec; job.PublishedAlarmSnapshot = job.AlarmSnapshot; job.PublishedRevision = revision;
        job.PublishedLibraries = job.Libraries;
        job.PublishedTrigger = job.Trigger; job.PublishedIntervalMs = job.IntervalMs;
        job.PublishedChangeDeadband = job.ChangeDeadband; job.PublishedChangeCooldownMs = job.ChangeCooldownMs;
        job.PublishedAlarmMinLevel = job.AlarmMinLevel; job.PublishedAlarmOnRaise = job.AlarmOnRaise;
        job.PublishedAlarmOnAck = job.AlarmOnAck; job.PublishedAlarmOnRecover = job.AlarmOnRecover;
    }

    private static async Task RememberPublicationAsync(ConfigDbContext db, CalcJob job, string userName, CancellationToken ct)
    {
        var stale = await db.CalcJobPublications.Where(x => x.JobId == job.Id).OrderByDescending(x => x.Id).Skip(19).ToListAsync(ct);
        if (stale.Count > 0) db.CalcJobPublications.RemoveRange(stale);
        db.CalcJobPublications.Add(new CalcJobPublication
        {
            JobId = job.Id, Revision = job.PublishedRevision, Script = job.PublishedScript, ReadTags = job.PublishedReadTags, WriteTags = job.PublishedWriteTags,
            HistoryWindowSec = job.PublishedHistoryWindowSec, AlarmSnapshot = job.PublishedAlarmSnapshot,
            Trigger = job.PublishedTrigger, IntervalMs = job.PublishedIntervalMs, ChangeDeadband = job.PublishedChangeDeadband,
            ChangeCooldownMs = job.PublishedChangeCooldownMs, AlarmMinLevel = job.PublishedAlarmMinLevel,
            AlarmOnRaise = job.PublishedAlarmOnRaise, AlarmOnAck = job.PublishedAlarmOnAck, AlarmOnRecover = job.PublishedAlarmOnRecover,
            Libraries = job.PublishedLibraries, LibrarySource = job.PublishedLibrarySource, Ts = ScadaTime.NowMs(), UserName = userName
        });
    }

    private static string? UnitOf(string output, string? unit) => output switch
    {
        "hours" or "periodHours" or "sinceMaintenance" => "h",
        "starts" or "periodStarts" => "次",
        "maintenanceDue" => null,
        _ => string.IsNullOrWhiteSpace(unit) ? null : unit.Trim()[..Math.Min(32, unit.Trim().Length)]
    };

    // 只改配置库里的输出测点，并返回被移除的测点编号。调用方提交成功后再关闭运行库里未结束的报警。
    public static async Task<List<int>> SyncOutputsAsync(ConfigDbContext db, CalcJob job, IReadOnlyList<string> outputs, string? unit, CancellationToken ct)
    {
        // 派生测点随计算任务自动增删，属于任务的内部实现；任务本身已有审计，这里不再逐点留痕。
        using var derivedAudit = Scada.Core.Security.AuditContext.Suppress();
        var removed = new List<int>();
        var tags = await db.Tags.Where(x => x.Kind == TagKind.Derived && x.JobId == job.Id).ToListAsync(ct);
        foreach (var obsolete in tags.Where(x => !outputs.Contains(x.JobOutput)).ToList())
        {
            var scripts = await db.CalcJobs.AsNoTracking().Where(item => item.Id != job.Id && (item.ReadTags != "" || item.PublishedReadTags != "")).Select(item => new { item.Name, item.ReadTags, item.PublishedReadTags }).ToListAsync(ct);
            foreach (var script in scripts)
                if (CalcScript.ParseIds(script.ReadTags).Concat(CalcScript.ParseIds(script.PublishedReadTags)).Contains(obsolete.Id)) throw new CalculationRequestException(409, $"输出 {obsolete.JobOutput}（#{obsolete.Id}）仍被脚本 {script.Name} 引用，不能移除");
            if (await db.TagDependencies.AnyAsync(x => x.InputTagId == obsolete.Id, ct))
                throw new CalculationRequestException(409, $"输出 {obsolete.JobOutput}（#{obsolete.Id}）仍被计算测点引用，不能移除");
            if (await db.CalcJobs.AnyAsync(x => x.InputTagId == obsolete.Id, ct))
                throw new CalculationRequestException(409, $"输出 {obsolete.JobOutput}（#{obsolete.Id}）仍被其他计算任务作为输入，不能移除");
            removed.Add(obsolete.Id);
            db.Tags.Remove(obsolete);
        }
        foreach (var name in outputs)
        {
            var tag = tags.FirstOrDefault(x => x.JobOutput == name);
            if (tag is null) { tag = new Tag { Kind = TagKind.Derived, JobId = job.Id, JobOutput = name, DeviceId = null, Writable = false, DataType = TagDataType.Double, Scale = 1, Offset = 0, ArchiveMode = ArchiveMode.OnChange }; db.Tags.Add(tag); }
            tag.Name = $"{job.Name}.{name}"; tag.AreaId = job.AreaId; tag.Unit = UnitOf(name, unit);
            tag.Description = $"计算任务「{job.Name}」输出 {name}";
        }
        return removed;
    }

    private async Task CloseRemovedAlarmsAsync(IReadOnlyList<int> tagIds, string reason, CancellationToken ct)
    {
        if (tagIds.Count == 0) return;
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        foreach (var tagId in tagIds)
        {
            var now = ScadaTime.NowMs();
            await runtime.AlarmEvents.Where(x => x.TagId == tagId && x.EndTs == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.EndTs, now).SetProperty(x => x.EndReason, reason), ct);
        }
    }

    public async Task DeleteAsync(int id, long revision, int userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.CalcJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "计算任务不存在");
        if (revision != job.Revision) throw new CalculationRequestException(409, "计算任务已被修改，请重新读取后核对");
        var removed = await SyncOutputsAsync(db, job, [], null, ct);
        runtime.OperationLogs.Add(AuditLog(job, "删除计算任务", JsonSerializer.Serialize(job, AuditJson), null, userId, userName));
        db.CalcJobs.Remove(job);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        await runtime.SaveChangesAsync(ct);
        try { await CloseRemovedAlarmsAsync(removed, "计算任务输出移除", ct); }
        finally { engine.RequestReload(); }
    }

    public async Task AuditAsync(CalcJob job, string action, int userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        runtime.OperationLogs.Add(AuditLog(job, action, null, null, userId, userName));
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ScriptLibrary>> LibrariesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        return await db.ScriptLibraries.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<ScriptLibrary> SaveLibraryAsync(int? id, ScriptLibraryInput request, string userName, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        ParseLibraryNames(name);
        if (request.Source is null || request.Source.Length > 8192) throw new CalculationRequestException(400, "脚本库不能超过 8192 个字符");
        await EnsureLibraryCompilesAsync(request.Source, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        ScriptLibrary library;
        if (id is int existing)
        {
            library = await db.ScriptLibraries.SingleOrDefaultAsync(x => x.Id == existing, ct) ?? throw new CalculationRequestException(404, "脚本库不存在");
            if (library.Revision != request.Revision) throw new CalculationRequestException(409, "脚本库已被修改，请重新读取后核对");
            if (library.Name != name) throw new CalculationRequestException(400, "脚本库名称不能修改");
            library.Source = request.Source; library.Revision++; library.UpdatedTs = ScadaTime.NowMs(); library.UserName = userName;
        }
        else
        {
            if (request.Revision != 0) throw new CalculationRequestException(400, "新建脚本库不能携带旧版本");
            if (await db.ScriptLibraries.CountAsync(ct) >= 64) throw new CalculationRequestException(409, "脚本库已达 64 个上限");
            if (await db.ScriptLibraries.AnyAsync(x => x.Name == name, ct)) throw new CalculationRequestException(409, "脚本库名称已存在");
            library = new ScriptLibrary { Name = name, Source = request.Source, PublishedSource = request.Source, PublishedRevision = 1, Revision = 1, UpdatedTs = ScadaTime.NowMs(), UserName = userName };
            db.ScriptLibraries.Add(library);
        }
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);
        return library;
    }

    public async Task<ScriptLibrary> PublishLibraryAsync(int id, long revision, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var library = await db.ScriptLibraries.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "脚本库不存在");
        if (library.Revision != revision) throw new CalculationRequestException(409, "脚本库已被修改，请重新读取后核对");
        await EnsureLibraryCompilesAsync(library.Source, ct);
        if (library.PublishedSource == library.Source && library.PublishedRevision > 0) return library;
        library.PublishedSource = library.Source; library.PublishedRevision = library.Revision; library.Revision++; library.UpdatedTs = ScadaTime.NowMs(); library.UserName = userName;
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);
        return library;
    }

    public async Task DeleteLibraryAsync(int id, long revision, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var library = await db.ScriptLibraries.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "脚本库不存在");
        if (library.Revision != revision) throw new CalculationRequestException(409, "脚本库已被修改，请重新读取后核对");
        var jobs = await db.CalcJobs.AsNoTracking().Select(x => new { x.Name, x.Libraries, x.PublishedLibraries }).ToListAsync(ct);
        var used = jobs.Where(x => SplitLibraryNames(x.Libraries).Contains(library.Name) || SplitLibraryNames(x.PublishedLibraries).Contains(library.Name)).Select(x => x.Name).Take(8).ToArray();
        if (used.Length > 0) throw new CalculationRequestException(409, $"脚本库仍被任务引用：{string.Join("、", used)}");
        db.ScriptLibraries.Remove(library);
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);
    }

    public async Task<ScriptJobReference[]> LibraryReferencesAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var library = await db.ScriptLibraries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "脚本库不存在");
        var jobs = await db.CalcJobs.AsNoTracking().Select(x => new { x.Id, x.Name, x.Libraries, x.PublishedLibraries }).ToListAsync(ct);
        return jobs.Where(x => SplitLibraryNames(x.Libraries).Contains(library.Name) || SplitLibraryNames(x.PublishedLibraries).Contains(library.Name))
            .Select(x => new ScriptJobReference(x.Id, x.Name)).ToArray();
    }

    public async Task<ScriptReferenceList> ReferencesAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var job = await db.CalcJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "计算任务不存在");
        var screens = await db.ProcessScreens.AsNoTracking().Select(x => new { x.Id, x.Name, x.DraftJson, x.PublishedJson, x.PublishedRevision }).ToListAsync(ct);
        var usedBy = screens.Where(x => MentionsJob(x.DraftJson, id) || MentionsJob(x.PublishedJson, id))
            .Select(x => new ScriptScreenReference(x.Id, x.Name, x.PublishedRevision is > 0)).ToArray();
        var catalog = await LoadCatalogAsync(db, ct);
        var names = ScriptTagNames.ForScript(catalog);
        var tags = new List<ScriptTagReference>();
        foreach (var tagId in CalcScript.ParseIds(job.ReadTags).Concat(CalcScript.ParseIds(job.WriteTags)).Distinct())
        {
            var tag = catalog.FirstOrDefault(item => item.Id == tagId);
            tags.Add(tag.Id == tagId
                ? new ScriptTagReference(ScriptTagNames.Key(tag), tag.Id, tag.Name, true)
                : new ScriptTagReference("#" + tagId, tagId, null, false));
        }
        foreach (var token in ScriptTagNames.QuotedArguments(job.Script))
            if (!names.ContainsKey(token)) tags.Add(new ScriptTagReference(token, null, null, false));
        return new ScriptReferenceList(usedBy, tags.ToArray());
    }

    private static bool MentionsJob(string? json, int id) =>
        !string.IsNullOrEmpty(json) && System.Text.RegularExpressions.Regex.IsMatch(json, $"\"jobId\"\\s*:\\s*{id}(?!\\d)");

    private static readonly char[] LibrarySeparators = [',', ' ', ';', '\n', '\t'];

    private static string[] SplitLibraryNames(string? text) =>
        (text ?? "").Split(LibrarySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] ParseLibraryNames(string? text)
    {
        var names = SplitLibraryNames(text);
        if (names.Length > 8) throw new TagExpressionException("一个任务最多引用 8 个脚本库");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new TagExpressionException("脚本库名称重复");
        foreach (var name in names)
            if (name.Length is 0 or > 64 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
                throw new TagExpressionException($"脚本库名称 {name} 只能包含字母、数字和下划线，且以字母开头");
        return names;
    }

    public async Task<CSharpScriptReply> CompileLibraryAsync(string? source, CancellationToken ct)
    {
        if (source is null || source.Length > 8192) throw new CalculationRequestException(400, "脚本库不能超过 8192 个字符");
        if (string.IsNullOrWhiteSpace(source))
            return new CSharpScriptReply("", false, [], new(), new(), ["脚本库不能为空"], Spans: [new ScriptDiagnostic("脚本库不能为空", "library.cs", 1, 1, 1, 2)]);
        return await engine.CompileCSharpAsync("await Task.CompletedTask;", source, ct);
    }

    private async Task EnsureLibraryCompilesAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new CalculationRequestException(400, "脚本库不能为空");
        var result = await engine.CompileCSharpAsync("await Task.CompletedTask;", source, ct);
        if (!result.Success) throw new CalculationRequestException(400, string.Join("\n", result.Diagnostics));
    }

    private static async Task<string> BundleLibrariesAsync(ConfigDbContext db, string? namesText, bool publishedOnly, CancellationToken ct)
    {
        var names = ParseLibraryNames(namesText);
        if (names.Length == 0) return "";
        var rows = await db.ScriptLibraries.AsNoTracking().Where(x => names.Contains(x.Name)).ToListAsync(ct);
        if (rows.Count != names.Length) throw new TagExpressionException("脚本库不存在");
        var ordered = names.Select(name => rows.Single(x => x.Name == name)).ToArray();
        if (publishedOnly && ordered.Any(x => x.PublishedRevision == 0)) throw new TagExpressionException("脚本库尚未发布");
        var text = string.Join("\n", ordered.Select(x => !publishedOnly && x.PublishedRevision == 0 ? x.Source : x.PublishedSource));
        if (text.Length > 16_384) throw new TagExpressionException("脚本库合计不能超过 16384 个字符");
        return text;
    }

    private static OperationLog AuditLog(CalcJob job, string action, string? before, string? after, int userId, string userName) => new()
    {
        UserId = userId, UserName = userName, Ts = ScadaTime.NowMs(), Action = $"{action}：{job.Name}", Result = "成功",
        TargetType = "CalcJob", TargetId = job.Id.ToString(),
        Details = Scada.Core.Security.AuditDiff.Compute(before is null ? null : JsonDocument.Parse(before).RootElement, after is null ? null : JsonDocument.Parse(after).RootElement)
    };
}
