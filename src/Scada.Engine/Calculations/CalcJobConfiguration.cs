using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Calculations;

public sealed record CalcJobRequest(
    string Name, CalcJobKind Kind, bool Enabled = true, int AreaId = Area.DefaultId, int IntervalMs = 1000,
    int? InputTagId = null, AccumulatorMode Mode = AccumulatorMode.Pulse, double Factor = 1, double? CounterMax = null, double? MaxDelta = null,
    BadQualityPolicy BadQuality = BadQualityPolicy.Hold, int MaxGapSec = 60, double RunThreshold = 0.5, double? MaintenanceHours = null,
    CalcResetPeriod ResetPeriod = CalcResetPeriod.Day, string? ShiftStarts = null, string? TouJson = null,
    string? Script = null, string? ReadTags = null, string? WriteTags = null, string? Unit = null, long Revision = 0);

public sealed record CalcJobOutput(string Name, int TagId);
public sealed record CalcJobInfo(CalcJob Job, IReadOnlyList<CalcJobOutput> Outputs, CalcJobStatus? Status);

public sealed class CalcJobConfiguration(IDbContextFactory<ConfigDbContext> factory, CalcJobEngine engine)
{
    public const int MaxJobs = 500;
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CalcJobInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
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
        return await db.CalcJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<CalcJobSnapshot>> SnapshotsAsync(int id, int take, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.CalcJobSnapshots.AsNoTracking().Where(x => x.JobId == id).OrderByDescending(x => x.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    public static CalcJob Map(CalcJobRequest r, int id = 0) => new()
    {
        Id = id, Name = r.Name?.Trim() ?? "", Kind = r.Kind, Enabled = r.Enabled, AreaId = r.AreaId, IntervalMs = r.IntervalMs,
        InputTagId = r.Kind == CalcJobKind.Script ? null : r.InputTagId, Mode = r.Mode, Factor = r.Factor, CounterMax = r.CounterMax, MaxDelta = r.MaxDelta,
        BadQuality = r.BadQuality, MaxGapSec = r.MaxGapSec, RunThreshold = r.RunThreshold, MaintenanceHours = r.MaintenanceHours,
        ResetPeriod = r.Kind == CalcJobKind.Script ? CalcResetPeriod.None : r.ResetPeriod,
        ShiftStarts = string.IsNullOrWhiteSpace(r.ShiftStarts) ? "08:00,20:00" : r.ShiftStarts.Trim(),
        TouJson = string.IsNullOrWhiteSpace(r.TouJson) ? "[]" : r.TouJson.Trim(),
        Script = r.Kind == CalcJobKind.Script ? r.Script?.Trim() ?? "" : "",
        ReadTags = r.Kind == CalcJobKind.Script ? Normalize(r.ReadTags) : "", WriteTags = r.Kind == CalcJobKind.Script ? Normalize(r.WriteTags) : ""
    };

    private static string Normalize(string? ids) => string.Join(",", CalcScript.ParseIds(ids ?? "").Order());

    // 校验并返回输出名列表；任何问题抛 CalculationRequestException(400)。
    public async Task<IReadOnlyList<string>> ValidateAsync(ConfigDbContext db, CalcJob job, CancellationToken ct)
    {
        void Fail(string message) => throw new CalculationRequestException(400, message);
        if (job.Name.Length is 0 or > 80) Fail("名称须为 1～80 个字符");
        if (!Enum.IsDefined(job.Kind)) Fail("任务类型无效");
        if (!await db.Areas.AnyAsync(x => x.Id == job.AreaId, ct)) Fail("区域不存在");
        if (job.IntervalMs is < 200 or > 3_600_000) Fail("执行周期须为 200～3600000 毫秒");
        if (job.Kind == CalcJobKind.Script && job.IntervalMs < 1000) Fail("脚本任务执行周期不得小于 1000 毫秒");
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

        if (job.Kind != CalcJobKind.Script)
        {
            if (job.InputTagId is not int input) { Fail("请选择输入测点"); return []; }
            var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input, ct);
            if (tag is null) Fail($"输入测点 #{input} 不存在");
            else if (tag.Kind == TagKind.Derived && tag.JobId == job.Id) Fail("输入不能是本任务自己的输出");
            return CalcJobMath.Outputs(job.Kind);
        }

        CalcScript script = null!;
        try { script = CalcScript.Compile(job.Script, CalcScript.ParseIds(job.ReadTags), CalcScript.ParseIds(job.WriteTags), engine.Limits()); }
        catch (TagExpressionException ex) { Fail(ex.Message); }
        var reads = CalcScript.ParseIds(job.ReadTags).ToArray(); var writes = CalcScript.ParseIds(job.WriteTags).ToArray();
        if (reads.Length > 64 || writes.Length > 16) Fail("可读白名单最多 64 个、可写白名单最多 16 个测点");
        var known = await db.Tags.AsNoTracking().Where(x => reads.Contains(x.Id) || writes.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var id in reads) if (!known.ContainsKey(id)) Fail($"可读测点 #{id} 不存在");
        foreach (var id in writes)
        {
            if (!known.TryGetValue(id, out var tag)) Fail($"可写测点 #{id} 不存在");
            else if (tag.Kind != TagKind.Physical || !tag.Writable) Fail($"可写测点 #{id} 须为可写的现场测点；派生结果请用 @输出");
        }
        return script.Outputs;
    }

    public async Task<CalcJob> SaveAsync(int? id, CalcJobRequest request, int userId, string userName, CancellationToken ct)
    {
        var candidate = Map(request, id ?? 0);
        await using var db = await factory.CreateDbContextAsync(ct);
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
        var outputs = await ValidateAsync(db, candidate, ct);
        if (await db.CalcJobs.AnyAsync(x => x.Name == candidate.Name && x.Id != candidate.Id, ct)) throw new CalculationRequestException(409, "任务名称已存在");

        var revision = id is null ? 1 : checked(job.Revision + 1);
        var keepId = job.Id;
        db.Entry(job).CurrentValues.SetValues(candidate);
        if (id is null) db.CalcJobs.Add(job); else job.Id = keepId;
        job.Revision = revision; job.UpdatedTs = ScadaTime.NowMs();
        // 写现场测点的脚本以保存人的身份执行（端点已核对其 Control 权限）。
        if (job.Kind == CalcJobKind.Script && job.WriteTags.Length > 0) { job.RunAsUserId = userId; job.RunAsUserName = userName; }
        else { job.RunAsUserId = null; job.RunAsUserName = ""; }
        await db.SaveChangesAsync(ct);

        await SyncOutputsAsync(db, job, outputs, request.Unit, ct);
        db.OperationLogs.Add(AuditLog(job, id is null ? "创建计算任务" : "修改计算任务", before, JsonSerializer.Serialize(job, AuditJson), userId, userName));
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        engine.RequestReload();
        return job;
    }

    private static string? UnitOf(string output, string? unit) => output switch
    {
        "hours" or "periodHours" or "sinceMaintenance" => "h",
        "starts" or "periodStarts" => "次",
        "maintenanceDue" => null,
        _ => string.IsNullOrWhiteSpace(unit) ? null : unit.Trim()[..Math.Min(32, unit.Trim().Length)]
    };

    public static async Task SyncOutputsAsync(ConfigDbContext db, CalcJob job, IReadOnlyList<string> outputs, string? unit, CancellationToken ct)
    {
        var tags = await db.Tags.Where(x => x.Kind == TagKind.Derived && x.JobId == job.Id).ToListAsync(ct);
        foreach (var obsolete in tags.Where(x => !outputs.Contains(x.JobOutput)).ToList())
        {
            if (await db.TagDependencies.AnyAsync(x => x.InputTagId == obsolete.Id, ct))
                throw new CalculationRequestException(409, $"输出 {obsolete.JobOutput}（#{obsolete.Id}）仍被计算测点引用，不能移除");
            if (await db.CalcJobs.AnyAsync(x => x.InputTagId == obsolete.Id, ct))
                throw new CalculationRequestException(409, $"输出 {obsolete.JobOutput}（#{obsolete.Id}）仍被其他计算任务作为输入，不能移除");
            await CloseAlarmsAsync(db, obsolete.Id, "计算任务输出移除", ct);
            db.Tags.Remove(obsolete);
        }
        foreach (var name in outputs)
        {
            var tag = tags.FirstOrDefault(x => x.JobOutput == name);
            if (tag is null) { tag = new Tag { Kind = TagKind.Derived, JobId = job.Id, JobOutput = name, DeviceId = null, Writable = false, DataType = TagDataType.Double, Scale = 1, Offset = 0, ArchiveMode = ArchiveMode.OnChange }; db.Tags.Add(tag); }
            tag.Name = $"{job.Name}.{name}"; tag.AreaId = job.AreaId; tag.Unit = UnitOf(name, unit);
            tag.Description = $"计算任务「{job.Name}」输出 {name}";
        }
    }

    private static Task<int> CloseAlarmsAsync(ConfigDbContext db, int tagId, string reason, CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        return db.AlarmEvents.Where(x => x.TagId == tagId && x.EndTs == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.EndTs, now).SetProperty(x => x.EndReason, reason), ct);
    }

    public async Task DeleteAsync(int id, long revision, int userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.CalcJobs.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new CalculationRequestException(404, "计算任务不存在");
        if (revision != job.Revision) throw new CalculationRequestException(409, "计算任务已被修改，请重新读取后核对");
        await SyncOutputsAsync(db, job, [], null, ct);
        db.OperationLogs.Add(AuditLog(job, "删除计算任务", JsonSerializer.Serialize(job, AuditJson), null, userId, userName));
        db.CalcJobs.Remove(job);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        engine.RequestReload();
    }

    public async Task AuditAsync(CalcJob job, string action, int userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.OperationLogs.Add(AuditLog(job, action, null, null, userId, userName));
        await db.SaveChangesAsync(ct);
    }

    private static OperationLog AuditLog(CalcJob job, string action, string? before, string? after, int userId, string userName) => new()
    {
        UserId = userId, UserName = userName, Ts = ScadaTime.NowMs(), Action = $"{action}：{job.Name}", Result = "成功",
        TargetType = "CalcJob", TargetId = job.Id.ToString(),
        Details = Scada.Core.Security.AuditDiff.Compute(before is null ? null : JsonDocument.Parse(before).RootElement, after is null ? null : JsonDocument.Parse(after).RootElement)
    };
}
