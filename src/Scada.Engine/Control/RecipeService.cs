using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Control;

public sealed record RecipeParameterCheck(string Key, string Name, int TagId, string TagName, string? Unit, double Target, double? Current, IReadOnlyList<string> Refusals, bool DualApproval);
public sealed record RecipeCheck(int RecipeId, int Version, bool DualApproval, IReadOnlyList<RecipeParameterCheck> Parameters)
{
    public IEnumerable<string> Refusals => Parameters.SelectMany(p => p.Refusals.Select(r => $"{p.Name}：{r}"));
    public bool Ok => Parameters.All(p => p.Refusals.Count == 0);
}

// 配方下发：整体预检查（全部参数通过才开始）→ 双人复核（按模板或参数测点策略，整批一次复核）→ 逐个经控制闭环写入并回读核对 →
// 任一参数失败立即停止，其余参数记为未下发，结果明确标记“部分下发”并列出已写入的参数及原值，不静默部分生效、不自动重发。
public sealed class RecipeService(IDbContextFactory<ConfigDbContext> factory, ControlCommandService commands, IConfigSnapshot config, IRealtimeCache cache, ILogger<RecipeService> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    // 同一时刻只允许一个“检查是否有正在下发 → 开始下发”的过程，避免同一模板并发下发。
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static List<RecipeParameter> Parameters(RecipeTemplate t) => JsonSerializer.Deserialize<List<RecipeParameter>>(t.ParametersJson, Json) ?? [];
    public static List<RecipeValue> Values(RecipeVersion v) => JsonSerializer.Deserialize<List<RecipeValue>>(v.ValuesJson, Json) ?? [];
    public static List<RecipeParameterResult> Results(RecipeDownload d) => JsonSerializer.Deserialize<List<RecipeParameterResult>>(d.ResultsJson, Json) ?? [];

    // 启动时：上次进程退出时仍在下发的记录标记为失败/部分下发（不自动续跑）。
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(stoppingToken);
            foreach (var d in await db.RecipeDownloads.Where(x => x.Status == RecipeDownloadStatus.Running).ToListAsync(stoppingToken))
            {
                if (_running.ContainsKey(d.Id)) continue;
                var results = Results(d);
                foreach (var r in results.Where(x => x.Status == "pending")) r.Status = "skipped";
                d.ResultsJson = JsonSerializer.Serialize(results, Json);
                d.Status = d.Written > 0 || results.Any(x => x.Status is "done" or "unknown") ? RecipeDownloadStatus.PartiallyApplied : RecipeDownloadStatus.Failed;
                d.EndedTs = ScadaTime.NowMs(); d.Message = Trim("服务重启，配方下发中断（不会自动续发）；" + Summary(results) + "；请核对现场后重新下发");
                db.OperationLogs.Add(Audit(d.RequestedByUserId, "system", $"配方下发中断 {d.RecipeName} v{d.Version}", d.Message, d.Id));
            }
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "恢复配方下发状态失败"); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var cts in _running.Values) cts.Cancel();
        try { await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); } catch (Exception ex) { logger.LogWarning(ex, "等待配方下发结束超时"); }
        await base.StopAsync(cancellationToken);
    }

    // 校验配方值：每个参数都必须有值、在模板上下限内、且能按测点编码。
    public static List<string> Validate(IReadOnlyList<RecipeParameter> parameters, IReadOnlyList<RecipeValue> values, IReadOnlyDictionary<int, Tag> tags)
    {
        var errors = new List<string>();
        var map = new Dictionary<string, double>();
        foreach (var v in values)
        {
            if (!map.TryAdd(v.Key, v.Value)) errors.Add($"参数 {v.Key} 重复");
            if (!double.IsFinite(v.Value)) errors.Add($"参数 {v.Key} 的值无效");
        }
        foreach (var k in map.Keys.Where(k => parameters.All(p => p.Key != k))) errors.Add($"模板中没有参数 {k}");
        foreach (var p in parameters)
        {
            if (!map.TryGetValue(p.Key, out var value)) { errors.Add($"参数 {p.Name} 缺少值"); continue; }
            if (p.Min is double min && value < min || p.Max is double max && value > max) errors.Add($"参数 {p.Name} 的值 {Fmt(value)} 超出范围 [{Fmt(p.Min)}, {Fmt(p.Max)}]");
            if (tags.TryGetValue(p.TagId, out var tag) && !CommandValuePolicy.TryGetRawValue(tag, value, out _, out var error)) errors.Add($"参数 {p.Name}：{error}");
        }
        return errors;
    }

    // 预检查：每个参数走控制闭环的完整检查（控制权、挂牌、限幅、变化率、许可），另加模板上下限。
    public async Task<RecipeCheck> CheckAsync(RecipeTemplate template, Recipe recipe, RecipeVersion version, ControlActor actor, CancellationToken ct)
    {
        var values = Values(version).ToDictionary(x => x.Key, x => x.Value);
        var list = new List<RecipeParameterCheck>();
        foreach (var p in Parameters(template))
        {
            var refusals = new List<string>();
            var has = values.TryGetValue(p.Key, out var target);
            if (!has) refusals.Add("配方中缺少该参数的值");
            if (has && (p.Min is double min && target < min || p.Max is double max && target > max)) refusals.Add($"超出配方范围 [{Fmt(p.Min)}, {Fmt(p.Max)}]");
            var check = has ? await commands.CheckAsync(p.TagId, target, actor, ct) : null;
            if (has && check is null) refusals.Add($"测点 #{p.TagId} 不存在");
            if (check is not null) refusals.AddRange(check.Refusals);
            list.Add(new(p.Key, p.Name, p.TagId, check?.TagName ?? (config.TryGetTag(p.TagId, out var t) ? t.Name : $"#{p.TagId}"), p.Unit, target, check?.Current, refusals, check?.DualApproval ?? false));
        }
        return new(recipe.Id, version.Version, template.DualApproval || list.Any(x => x.DualApproval), list);
    }

    // 发起下发：预检查不通过直接拒绝（不写任何参数）；需要复核时生成整批复核请求，否则立即开始。
    public async Task<RecipeDownload> RequestAsync(int recipeId, int versionNo, ControlActor actor, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await RequestCoreAsync(recipeId, versionNo, actor, ct); }
        finally { _gate.Release(); }
    }

    private async Task<RecipeDownload> RequestCoreAsync(int recipeId, int versionNo, ControlActor actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (template, recipe, version) = await LoadAsync(db, recipeId, versionNo, ct);
        if (await db.RecipeDownloads.AnyAsync(x => x.TemplateId == template.Id && (x.Status == RecipeDownloadStatus.Running), ct))
            throw new ControlWorkflowException("同一模板的配方正在下发，请等待结束", 409);
        var check = await CheckAsync(template, recipe, version, actor, ct);
        var download = New(template, recipe, version, actor, check);
        if (!check.Ok)
        {
            download.Status = RecipeDownloadStatus.Rejected; download.EndedTs = ScadaTime.NowMs();
            download.Message = Trim("预检查未通过，未下发任何参数：" + string.Join("；", check.Refusals));
            db.RecipeDownloads.Add(download);
            db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"配方下发被拒绝 {recipe.Name} v{version.Version}", download.Message, null));
            await db.SaveChangesAsync(ct);
            return download;
        }
        db.RecipeDownloads.Add(download);
        await db.SaveChangesAsync(ct);
        if (check.DualApproval)
        {
            if (actor.UserId is not int user) throw new ControlWorkflowException("双人复核需要登录用户发起");
            var request = new ControlRequest
            {
                Id = Guid.NewGuid(), Kind = "recipe", RecipeDownloadId = download.Id, Source = $"配方 {recipe.Name} v{version.Version}", RequestedByUserId = user, RequestedBy = actor.UserName,
                RequestedTs = ScadaTime.NowMs(), ExpiresTs = ScadaTime.NowMs() + 300_000, Status = ControlRequestStatus.Pending
            };
            db.ControlRequests.Add(request);
            download.ApprovalId = request.Id; download.Message = "等待另一位有权限的用户复核";
            db.OperationLogs.Add(Audit(user, actor.UserName, $"申请下发配方 {recipe.Name} v{version.Version}（待双人复核）", $"待复核，编号 {request.Id}", download.Id));
            await db.SaveChangesAsync(ct);
            return download;
        }
        await StartAsync(download.Id, actor, null, ct);
        await using var fresh = await factory.CreateDbContextAsync(ct);
        return await fresh.RecipeDownloads.AsNoTracking().SingleAsync(x => x.Id == download.Id, ct);
    }

    // 复核结束（批准/驳回/撤回/超时）后由控制复核流程调用。
    public async Task<RecipeDownload?> DecidedAsync(int downloadId, bool approved, ControlActor requester, string? approvedBy, string reason, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var download = await db.RecipeDownloads.SingleOrDefaultAsync(x => x.Id == downloadId, ct);
        if (download is null || download.Status != RecipeDownloadStatus.Pending) return download;
        if (!approved)
        {
            download.Status = RecipeDownloadStatus.Rejected; download.EndedTs = ScadaTime.NowMs(); download.Message = Trim($"{reason}，未下发任何参数");
            var results = Results(download); foreach (var r in results) r.Status = "skipped"; download.ResultsJson = JsonSerializer.Serialize(results, Json);
            await db.SaveChangesAsync(ct);
            return download;
        }
        download.ApprovedBy = approvedBy; await db.SaveChangesAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (await db.RecipeDownloads.AnyAsync(x => x.TemplateId == download.TemplateId && x.Status == RecipeDownloadStatus.Running, ct))
            {
                download.Status = RecipeDownloadStatus.Failed; download.EndedTs = ScadaTime.NowMs(); download.Message = "批准时同一模板的另一配方正在下发，未下发任何参数；请稍后重新发起";
                var results = Results(download); foreach (var r in results) r.Status = "skipped"; download.ResultsJson = JsonSerializer.Serialize(results, Json);
                await db.SaveChangesAsync(ct);
                throw new ControlWorkflowException(download.Message, 409);
            }
            await StartAsync(download.Id, requester, approvedBy, ct);
        }
        finally { _gate.Release(); }
        return download;
    }

    private async Task StartAsync(int downloadId, ControlActor actor, string? approvedBy, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var download = await db.RecipeDownloads.SingleAsync(x => x.Id == downloadId, ct);
        download.Status = RecipeDownloadStatus.Running; download.StartedTs = ScadaTime.NowMs(); download.Message = null;
        db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"开始下发配方 {download.RecipeName} v{download.Version}", approvedBy is null ? $"下发 #{download.Id}" : $"下发 #{download.Id}，复核人 {approvedBy}", download.Id));
        await db.SaveChangesAsync(ct);
        var cts = new CancellationTokenSource(); _running[download.Id] = cts;
        _tasks[download.Id] = Task.Run(() => RunAsync(download.Id, actor, approvedBy, cts.Token));
    }

    public async Task<bool> AbortAsync(int downloadId, ControlActor actor, CancellationToken ct)
    {
        if (!_running.TryGetValue(downloadId, out var cts)) return false;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"中止配方下发 #{downloadId}", "已请求中止（当前参数写完后停止）", downloadId));
            await db.SaveChangesAsync(ct);
        }
        cts.Cancel();
        return true;
    }

    public Task WaitAsync(int downloadId) => _tasks.TryGetValue(downloadId, out var task) ? task : Task.CompletedTask;

    private async Task RunAsync(int downloadId, ControlActor actor, string? approvedBy, CancellationToken ct)
    {
        RecipeDownload download; List<RecipeParameterResult> results;
        await using (var db = await factory.CreateDbContextAsync())
        {
            download = await db.RecipeDownloads.AsNoTracking().SingleAsync(x => x.Id == downloadId);
            results = Results(download);
        }
        var status = RecipeDownloadStatus.Completed; string? failure = null;
        try
        {
            // 复核等待期间现场可能变化：开始写入前对全部参数再做一次整体检查，任何一项不通过则一个都不写。
            var refusals = new List<string>();
            foreach (var r in results)
                if (await commands.CheckAsync(r.TagId, r.Target, actor, CancellationToken.None) is not { } check) refusals.Add($"{r.Name}：测点不存在");
                else refusals.AddRange(check.Refusals.Select(x => $"{r.Name}：{x}"));
            if (refusals.Count > 0)
            {
                status = RecipeDownloadStatus.Failed; failure = "下发前复查未通过，未下发任何参数：" + string.Join("；", refusals);
                foreach (var r in results) r.Status = "skipped";
            }
            else
            {
                var n = 0;
                foreach (var r in results)
                {
                    n++;
                    if (ct.IsCancellationRequested) { status = RecipeDownloadStatus.Aborted; failure = $"在第 {n} 个参数之前中止"; break; }
                    r.Before = cache.Get(r.TagId) is { Quality: TagQuality.Good } v ? v.Value : null;
                    var result = await commands.SubmitAsync(new ControlSubmission(r.TagId, r.Target, actor, ControlOrigin.Recipe, $"配方 {download.RecipeName} v{download.Version} 参数 {r.Name}",
                        Confirmed: true, PreApproved: approvedBy is not null, ApprovedBy: approvedBy), CancellationToken.None);
                    r.ReadBack = result.ReadBack;
                    if (result.Success && !ReadBackMatches(r.TagId, r.Target, result.ReadBack))
                        result = result with { Success = false, Outcome = GatewayCommandOutcome.Unknown, Message = $"回读值 {Fmt(result.ReadBack)} 与目标 {Fmt(r.Target)} 不一致" };
                    if (result.Success) { r.Status = "done"; r.Message = Trim(result.Message); download.Written++; }
                    else
                    {
                        r.Status = result.Outcome == GatewayCommandOutcome.Unknown ? "unknown" : "failed";
                        r.Message = Trim(result.Message); status = RecipeDownloadStatus.Failed; failure = $"参数“{r.Name}”{(r.Status == "unknown" ? "结果未知" : "失败")}：{result.Message}";
                    }
                    await SaveAsync(downloadId, results, download.Written, null, null);
                    if (status != RecipeDownloadStatus.Completed) break;
                }
            }
        }
        catch (Exception ex) { status = RecipeDownloadStatus.Failed; failure = "配方下发异常：" + ex.Message; logger.LogError(ex, "配方 {Name} 下发异常", download.RecipeName); }
        foreach (var r in results.Where(x => x.Status == "pending")) r.Status = "skipped";
        var touched = results.Any(x => x.Status is "done" or "unknown");
        if (status is RecipeDownloadStatus.Failed or RecipeDownloadStatus.Aborted && touched) status = RecipeDownloadStatus.PartiallyApplied;
        var message = status == RecipeDownloadStatus.Completed ? $"全部 {results.Count} 个参数已下发并回读一致"
            : status == RecipeDownloadStatus.PartiallyApplied ? $"部分下发：{failure}；{Summary(results)}；现场处于新旧配方混合状态，请核对后处理（不会自动重发或回退）"
            : failure;
        try
        {
            await SaveAsync(downloadId, results, download.Written, status, message);
            await using var db = await factory.CreateDbContextAsync();
            db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"配方下发结束 {download.RecipeName} v{download.Version}", $"{Label(status)}：{message}", downloadId));
            await db.SaveChangesAsync();
        }
        finally { _running.TryRemove(downloadId, out var cts); cts?.Dispose(); }
    }

    // 下发链路已按编码后的原始值做同点回读比对；这里再按工程值核对一次（容差为一个原始量化单位）。
    private bool ReadBackMatches(int tagId, double target, double? readBack)
    {
        if (readBack is not double rb) return true;
        var tolerance = config.TryGetTag(tagId, out var tag) && tag.Scale != 0 ? Math.Abs(tag.Scale) * 0.5 + 1e-9 : 1e-6;
        return Math.Abs(rb - target) <= Math.Max(tolerance, 1e-9 * Math.Abs(target));
    }

    // 上载：把参数测点的当前实时值存为配方的新版本（或新配方）。任一参数无有效值则拒绝。
    public List<RecipeValue> ReadLive(RecipeTemplate template, out List<string> missing)
    {
        missing = [];
        var values = new List<RecipeValue>();
        foreach (var p in Parameters(template))
        {
            if (cache.Get(p.TagId) is { Quality: TagQuality.Good } v && double.IsFinite(v.Value)) values.Add(new() { Key = p.Key, Value = v.Value });
            else missing.Add(p.Name);
        }
        return values;
    }

    public static async Task<(RecipeTemplate, Recipe, RecipeVersion)> LoadAsync(ConfigDbContext db, int recipeId, int versionNo, CancellationToken ct)
    {
        var recipe = await db.Recipes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recipeId, ct) ?? throw new ControlWorkflowException("配方不存在", 404);
        var template = await db.RecipeTemplates.AsNoTracking().SingleAsync(x => x.Id == recipe.TemplateId, ct);
        var version = await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.RecipeId == recipeId && x.Version == (versionNo > 0 ? versionNo : recipe.CurrentVersion), ct)
            ?? throw new ControlWorkflowException("配方版本不存在", 404);
        return (template, recipe, version);
    }

    private static RecipeDownload New(RecipeTemplate template, Recipe recipe, RecipeVersion version, ControlActor actor, RecipeCheck check) => new()
    {
        RecipeId = recipe.Id, RecipeName = recipe.Name, Version = version.Version, TemplateId = template.Id, AreaId = template.AreaId, Status = RecipeDownloadStatus.Pending,
        RequestedByUserId = actor.UserId, RequestedBy = actor.UserName, CreatedTs = ScadaTime.NowMs(),
        ResultsJson = JsonSerializer.Serialize(check.Parameters.Select(p => new RecipeParameterResult
        {
            Key = p.Key, Name = p.Name, TagId = p.TagId, Target = p.Target, Before = p.Current,
            Status = p.Refusals.Count > 0 ? "failed" : "pending", Message = p.Refusals.Count > 0 ? Trim(string.Join("；", p.Refusals)) : null
        }).ToList(), Json)
    };

    private async Task SaveAsync(int id, List<RecipeParameterResult> results, int written, RecipeDownloadStatus? status, string? message)
    {
        await using var db = await factory.CreateDbContextAsync();
        var d = await db.RecipeDownloads.SingleAsync(x => x.Id == id);
        d.ResultsJson = JsonSerializer.Serialize(results, Json); d.Written = written;
        if (status is RecipeDownloadStatus s) { d.Status = s; d.EndedTs = ScadaTime.NowMs(); d.Message = message is null ? null : Trim(message); }
        await db.SaveChangesAsync();
    }

    private static string Summary(List<RecipeParameterResult> results)
    {
        var done = results.Where(x => x.Status == "done").Select(x => $"{x.Name}（原值 {Fmt(x.Before)}→{Fmt(x.Target)}）").ToList();
        var unknown = results.Where(x => x.Status == "unknown").Select(x => x.Name).ToList();
        var text = $"已写入 {done.Count}/{results.Count}";
        if (done.Count > 0) text += "：" + string.Join("、", done);
        if (unknown.Count > 0) text += $"；结果未知 {string.Join("、", unknown)}";
        var skipped = results.Count(x => x.Status is "skipped" or "failed");
        if (skipped > 0) text += $"；未写入 {skipped} 个";
        return text;
    }

    public static string Label(RecipeDownloadStatus s) => s switch
    {
        RecipeDownloadStatus.Completed => "完成", RecipeDownloadStatus.PartiallyApplied => "部分下发", RecipeDownloadStatus.Aborted => "中止",
        RecipeDownloadStatus.Rejected => "未下发", RecipeDownloadStatus.Pending => "待复核", RecipeDownloadStatus.Running => "下发中", _ => "失败"
    };

    public static string Fmt(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";
    private static string Trim(string text) => text.Length > 1000 ? text[..1000] : text;

    private static OperationLog Audit(int? userId, string userName, string action, string result, int? downloadId) => new()
    {
        UserId = userId, UserName = userName, Ts = ScadaTime.NowMs(), Action = action.Length > 200 ? action[..200] : action, Result = Trim(result),
        TargetType = "RecipeDownload", TargetId = downloadId?.ToString(CultureInfo.InvariantCulture)
    };
}
