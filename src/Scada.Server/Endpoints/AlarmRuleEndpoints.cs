using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;
using Scada.Engine.Alarms;

namespace Scada.Server.Endpoints;

public sealed record AlarmRuleRequest(
    int TagId,
    AlarmRuleType Type,
    double Threshold,
    AlarmLevel Level,
    int DelaySec,
    double RecoverDeadband,
    bool Enabled,
    string? Message,
    int OffDelaySec = 0,
    int? SuppressTagId = null,
    AlarmSuppressMode SuppressMode = AlarmSuppressMode.None,
    string? Cause = null,
    string? Action = null,
    AlarmActiveWhen ActiveWhen = AlarmActiveWhen.On,
    AlarmCategory Category = AlarmCategory.Process,
    AlarmConsequence Consequence = AlarmConsequence.Major,
    int ResponseTimeSec = 600,
    bool AutoPriority = false,
    string? MessageEn = null,
    string? CauseEn = null,
    string? ActionEn = null,
    int? BitIndex = null,
    string? Expression = null);

public sealed record AlarmShelveRequest(int Minutes, string? Reason);
public sealed record AlarmUnshelveRequest(string? Reason);
public sealed record AlarmOutOfServiceRequest(bool OutOfService, string? Reason);
public sealed record AlarmRuleSimulateRequest(
    double Value,
    TagQuality Quality = TagQuality.Good,
    double? PreviousValue = null,
    Dictionary<int, double>? References = null,
    Dictionary<int, TagQuality>? ReferenceQualities = null);

public static class AlarmRuleEndpoints
{
    public static void MapAlarmRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alarm-rules").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (
            int? tagId,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var query = db.AlarmRules.AsNoTracking();
            if (tagId is int id)
            {
                query = query.Where(x => x.TagId == id);
            }

            var access = await http.GetAccessAsync();
            var rules = await query.OrderBy(x => x.TagId).ThenBy(x => x.Type).ToListAsync(cancellationToken);
            return Results.Ok(rules.Where(x => access.CanTag(Permission.View, x.TagId)).ToList());
        });

        group.MapPost("", async (
            AlarmRuleRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointHelpers.RequireTagsAsync(http, [request.TagId], Permission.Configure, $"创建报警规则：测点 #{request.TagId}") is { } denied) return denied;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var rule = MapRule(request);
            var error = EndpointHelpers.ValidateAlarmRule(rule);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            if (await EndpointHelpers.ValidateAlarmRuleTargetAsync(db, rule, null, cancellationToken) is { } targetError)
            {
                return EndpointHelpers.BadRequest(targetError);
            }

            if (await CheckSuppressTagAsync(http, db, rule, cancellationToken) is { } suppressError) return suppressError;
            if (await CheckExpressionTagsAsync(http, db, rule, $"创建报警规则：测点 #{rule.TagId}", cancellationToken) is { } expressionError) return expressionError;

            db.AlarmRules.Add(rule);
            await db.SaveChangesAsync(cancellationToken);
            runtime.OperationLogs.Add(Audit.Change(http, $"创建报警规则：测点 #{rule.TagId}", "AlarmRule", rule.Id, null, rule));
            await db.SaveChangesAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Created($"/api/alarm-rules/{rule.Id}", rule);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id,
            AlarmRuleRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireTagsAsync(http, new[] { rule.TagId, request.TagId }.Distinct(), Permission.Configure, $"修改报警规则 #{id}") is { } denied) return denied;
            var before = Copy(rule);
            var updated = MapRule(request);
            var error = EndpointHelpers.ValidateAlarmRule(updated);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            if (await EndpointHelpers.ValidateAlarmRuleTargetAsync(db, updated, id, cancellationToken) is { } targetError)
            {
                return EndpointHelpers.BadRequest(targetError);
            }

            if (await CheckSuppressTagAsync(http, db, updated, cancellationToken) is { } suppressError) return suppressError;
            if (await CheckExpressionTagsAsync(http, db, updated, $"修改报警规则 #{id}", cancellationToken) is { } expressionError) return expressionError;

            rule.TagId = updated.TagId;
            rule.Type = updated.Type;
            rule.ActiveWhen = updated.ActiveWhen;
            rule.Threshold = updated.Threshold;
            rule.Level = updated.Level;
            rule.Category = updated.Category;
            rule.Consequence = updated.Consequence;
            rule.ResponseTimeSec = updated.ResponseTimeSec;
            rule.AutoPriority = updated.AutoPriority;
            rule.DelaySec = updated.DelaySec;
            rule.RecoverDeadband = updated.RecoverDeadband;
            rule.Enabled = updated.Enabled;
            rule.Message = updated.Message;
            rule.MessageEn = updated.MessageEn;
            rule.Cause = updated.Cause;
            rule.CauseEn = updated.CauseEn;
            rule.Action = updated.Action;
            rule.ActionEn = updated.ActionEn;
            rule.OffDelaySec = updated.OffDelaySec;
            rule.BitIndex = updated.BitIndex;
            rule.Expression = updated.Expression;
            rule.SuppressTagId = updated.SuppressTagId;
            rule.SuppressMode = updated.SuppressMode;
            runtime.OperationLogs.Add(Audit.Change(http, $"修改报警规则 #{rule.Id}", "AlarmRule", rule.Id, before, rule));

            await db.SaveChangesAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(rule);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireTagsAsync(http, [rule.TagId], Permission.Configure, $"删除报警规则 #{id}") is { } denied) return denied;
            db.AlarmRules.Remove(rule);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除报警规则 #{rule.Id}", "AlarmRule", rule.Id, rule, null));
            await db.SaveChangesAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);

        // 停用（out of service）：维修/仪表故障期间不评估；必须写原因，走差分审计。
        group.MapPost("/{id:int}/out-of-service", async (
            int id,
            AlarmOutOfServiceRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (rule is null) return Results.NotFound();
            var action = request.OutOfService ? $"停用报警规则 #{id}" : $"恢复报警规则 #{id}";
            if (await EndpointHelpers.RequireTagsAsync(http, [rule.TagId], Permission.Configure, action) is { } denied) return denied;
            if (request.OutOfService && string.IsNullOrWhiteSpace(request.Reason)) return EndpointHelpers.BadRequest("停用必须填写原因");
            if (request.Reason is { Length: > 500 }) return EndpointHelpers.BadRequest("原因不能超过 500 字");
            var before = Copy(rule);
            rule.OutOfService = request.OutOfService;
            rule.OutOfServiceReason = request.OutOfService ? request.Reason!.Trim() : null;
            runtime.OperationLogs.Add(Audit.Change(http, action, "AlarmRule", rule.Id, before, rule));
            await db.SaveChangesAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(rule);
        }).RequireAuthorization(AccessPolicies.Configure);

        // 暂挂（shelve）：操作员动作，需报警所在区域的确认报警权限；限时、必须写原因。
        group.MapPost("/{id:int}/shelve", async (
            int id,
            AlarmShelveRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            AlarmShelfService shelf,
            Microsoft.Extensions.Options.IOptions<Scada.Core.Options.ScadaSettings> options,
            CancellationToken cancellationToken) =>
        {
            int tagId;
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
                var rule = await db.AlarmRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (rule is null) return Results.NotFound();
                tagId = rule.TagId;
            }
            if (await EndpointHelpers.RequireTagsAsync(http, [tagId], Permission.AckAlarm, $"暂挂报警规则 #{id}") is { } denied) return denied;
            var result = await shelf.ShelveAsync(id, request.Minutes, request.Reason, http.User.GetUserName(), options.Value.AlarmShelveMaxMinutes, cancellationToken);
            if (!result.Success) return EndpointHelpers.BadRequest(result.Error!);
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
                runtime.OperationLogs.Add(Audit.Change(http, $"暂挂报警规则 #{id}", "AlarmShelve", result.Shelve!.Id, null, result.Shelve));
                await db.SaveChangesAsync(cancellationToken);
                await runtime.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(result.Shelve);
        }).RequireAuthorization(AccessPolicies.AckAlarm);

        group.MapPost("/{id:int}/unshelve", async (
            int id,
            AlarmUnshelveRequest? request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            AlarmShelfService shelf,
            CancellationToken cancellationToken) =>
        {
            AlarmShelve? current;
            int tagId;
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
                var rule = await db.AlarmRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (rule is null) return Results.NotFound();
                tagId = rule.TagId;
                current = await db.AlarmShelves.AsNoTracking().Where(x => x.RuleId == id && x.ClearedTs == null).OrderByDescending(x => x.StartTs).FirstOrDefaultAsync(cancellationToken);
            }
            if (await EndpointHelpers.RequireTagsAsync(http, [tagId], Permission.AckAlarm, $"解除暂挂 #{id}") is { } denied) return denied;
            var note = string.IsNullOrWhiteSpace(request?.Reason) ? "手动解除暂挂" : $"手动解除暂挂：{request!.Reason!.Trim()}";
            if (!await shelf.UnshelveAsync(id, http.User.GetUserName(), note, cancellationToken)) return EndpointHelpers.BadRequest("该规则未处于暂挂状态");
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
                var after = await db.AlarmShelves.AsNoTracking().FirstOrDefaultAsync(x => x.Id == current!.Id, cancellationToken);
                runtime.OperationLogs.Add(Audit.Change(http, $"解除暂挂 #{id}", "AlarmShelve", current?.Id, current, after));
                await db.SaveChangesAsync(cancellationToken);
                await runtime.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(new { success = true });
        }).RequireAuthorization(AccessPolicies.AckAlarm);

        // 导出：只导出当前用户有配置权限的测点规则；xlsx（默认）或 csv，导出动作记审计。
        group.MapGet("/export", async (string? format, int? tagId, HttpContext http, IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            AlarmRuleExcelService excel, CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var query = db.AlarmRules.AsNoTracking().AsQueryable();
            if (tagId is int id) query = query.Where(x => x.TagId == id);
            var rules = (await query.OrderBy(x => x.TagId).ThenBy(x => x.Type).ThenBy(x => x.Id).ToListAsync(cancellationToken))
                .Where(x => access.CanTag(Permission.Configure, x.TagId)).ToList();
            var tagIds = rules.Select(x => x.TagId).Distinct().ToList();
            var tagNames = await db.Tags.AsNoTracking().Where(x => tagIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
            if (!csv && !string.Equals(format ?? "xlsx", "xlsx", StringComparison.OrdinalIgnoreCase)) return EndpointHelpers.BadRequest("格式须为 xlsx 或 csv");
            var bytes = csv ? excel.ExportCsv(rules, tagNames) : excel.ExportXlsx(rules, tagNames);
            runtime.OperationLogs.Add(new OperationLog
            {
                UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(),
                Action = "导出报警规则", Result = "成功", TargetType = "AlarmRule",
                Details = System.Text.Json.JsonSerializer.Serialize(new { format = csv ? "csv" : "xlsx", tagId, count = rules.Count })
            });
            await db.SaveChangesAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
            var name = $"alarm-rules-{DateTime.Now:yyyyMMddHHmmss}.{(csv ? "csv" : "xlsx")}";
            return csv
                ? Results.File(bytes, "text/csv;charset=utf-8", name)
                : Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }).RequireAuthorization(AccessPolicies.Configure);

        // 导入：与测点导入一致，要求全部区域配置权限；逐行校验，成功行提交后整库热加载并写审计。
        group.MapPost("/import", async (IFormFile file, HttpContext http, IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            AlarmRuleExcelService excel, ConfigReloader reloader, CancellationToken cancellationToken) =>
        {
            if (!(await http.GetAccessAsync()).HasGlobal(Permission.Configure))
                return await Audit.ForbidAsync(http, "导入报警规则", "导入报警规则需要全部区域的配置权限");
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not (".xlsx" or ".csv")) return EndpointHelpers.BadRequest("仅支持 .xlsx 或 .csv 文件");
            if (file.Length == 0 || file.Length > 20 * 1024 * 1024) return EndpointHelpers.BadRequest("文件为空或超过 20 MB");
            await using var stream = file.OpenReadStream();
            var result = await excel.ImportAsync(stream, file.FileName, contextFactory, runtimeFactory, http.User.GetUserId(), http.User.GetUserName(), cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
                runtime.OperationLogs.Add(new OperationLog
                {
                    UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(),
                    Action = "导入报警规则", Result = "成功", TargetType = "AlarmRule",
                    Details = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        file = file.FileName, result.Created, result.Updated, result.Skipped,
                        errors = result.Errors.Take(10).ToArray()
                    })
                });
                await db.SaveChangesAsync(cancellationToken);
                await runtime.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(result);
        }).DisableAntiforgery().RequireAuthorization(AccessPolicies.Configure);

        // 规则模拟：按给定测点值/质量预览条件是否成立与中英文文案，不改变任何规则状态。
        group.MapPost("/{id:int}/simulate", async (int id, AlarmRuleSimulateRequest request, HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rule = await db.AlarmRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (rule is null) return Results.NotFound();
            if (await EndpointHelpers.RequireTagsAsync(http, [rule.TagId], Permission.View, $"模拟报警规则 #{id}") is { } denied) return denied;
            var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rule.TagId, cancellationToken);
            if (tag is null) return Results.NotFound();
            if (!Enum.IsDefined(request.Quality)) return EndpointHelpers.BadRequest("质量值无效");
            if (!double.IsFinite(request.Value) || request.PreviousValue is double previous && !double.IsFinite(previous)) return EndpointHelpers.BadRequest("输入值无效");
            var device = tag.DeviceId is int deviceId
                ? await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, cancellationToken)
                : null;
            var evaluated = rule.EvaluatedValue(request.Value);
            var previousEvaluated = request.PreviousValue is double prev ? rule.EvaluatedValue(prev) : (double?)null;
            bool? condition;
            string? indeterminate = null;
            var references = new List<object>();
            switch (rule.Type)
            {
                case AlarmRuleType.BadQuality:
                    condition = request.Quality != TagQuality.Good;
                    break;
                case AlarmRuleType.Expression:
                    if (string.IsNullOrWhiteSpace(rule.Expression)) return EndpointHelpers.BadRequest("规则没有填写复合条件表达式");
                    Scada.Core.Calculations.TagExpression expression;
                    try { expression = Scada.Core.Calculations.TagExpression.Compile(rule.Expression); }
                    catch (Scada.Core.Calculations.TagExpressionException ex) { return EndpointHelpers.BadRequest($"表达式无效：{ex.Message}"); }
                    var dependencies = expression.Dependencies.ToArray();
                    var referenceTags = await db.Tags.AsNoTracking().Where(x => dependencies.Contains(x.Id))
                        .Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, cancellationToken);
                    var values = new Dictionary<int, double>();
                    foreach (var dependency in dependencies)
                    {
                        if (!referenceTags.TryGetValue(dependency, out var referenceTag))
                            return EndpointHelpers.BadRequest($"表达式引用测点不存在：{dependency}");
                        if (request.References is null || !request.References.TryGetValue(dependency, out var referenceValue))
                            return EndpointHelpers.BadRequest($"请填写引用测点 #{dependency} 的模拟值");
                        var quality = request.ReferenceQualities?.GetValueOrDefault(dependency) ?? TagQuality.Good;
                        references.Add(new { tagId = dependency, tagName = referenceTag.Name, value = referenceValue, quality });
                        if (quality != TagQuality.Good)
                        {
                            indeterminate = $"引用测点 #{dependency} 质量为 {quality}，无法判定";
                            continue;
                        }

                        values[dependency] = referenceValue;
                    }

                    if (indeterminate is not null) { condition = null; break; }
                    try { condition = expression.Evaluate(idValue => values.GetValueOrDefault(idValue)) != 0; }
                    catch (Scada.Core.Calculations.TagExpressionException ex) { return EndpointHelpers.BadRequest($"表达式无法计算：{ex.Message}"); }
                    break;
                case AlarmRuleType.StateChange:
                case AlarmRuleType.Discrete when rule.GetEffectiveActiveWhen() == AlarmActiveWhen.Change:
                    condition = previousEvaluated is double oldValue && oldValue != evaluated;
                    break;
                case AlarmRuleType.Discrete:
                    condition = rule.GetEffectiveActiveWhen() == AlarmActiveWhen.On ? evaluated >= 0.5 : evaluated < 0.5;
                    break;
                case AlarmRuleType.High:
                case AlarmRuleType.HighHigh:
                    condition = evaluated > rule.Threshold;
                    break;
                case AlarmRuleType.Low:
                case AlarmRuleType.LowLow:
                    condition = evaluated < rule.Threshold;
                    break;
                default:
                    condition = false;
                    break;
            }

            var message = AlarmMessageBuilder.Build(tag, device, rule, request.Value, request.PreviousValue, request.Quality);
            var messageEn = AlarmMessageBuilder.BuildEn(tag, device, rule, request.Value, request.PreviousValue, request.Quality);
            var changeType = rule.Type == AlarmRuleType.StateChange
                || rule.Type == AlarmRuleType.Discrete && rule.GetEffectiveActiveWhen() == AlarmActiveWhen.Change;
            return Results.Ok(new
            {
                triggered = condition,
                recovered = changeType ? (bool?)false : condition is bool active ? !active : null,
                indeterminate,
                evaluatedValue = evaluated,
                bitIndex = rule.BitIndex,
                expression = rule.Expression,
                message,
                messageEn,
                references
            });
        }).RequireAuthorization(AccessPolicies.Configure);
    }

    public static void MapAlarmShelveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alarm-shelves", async (bool? history, HttpContext http, IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var query = db.AlarmShelves.AsNoTracking();
            if (history != true) query = query.Where(x => x.ClearedTs == null);
            var access = await http.GetAccessAsync();
            // 先按当前用户能看的规则收窄，再取最近 1000。先截再过滤时，别的区域的新暂挂会把本区域仍在生效的挤掉。
            if (access.AreasWith(Permission.View) is { } areas)
            {
                var allowed = areas.ToArray();
                var tagIds = access.Model.TagAreas.Where(pair => pair.Value.All(area => allowed.Contains(area))).Select(pair => pair.Key).ToArray();
                if (tagIds.Length == 0) return Results.Ok(Array.Empty<object>());
                query = query.Where(x => db.AlarmRules.Any(r => r.Id == x.RuleId && tagIds.Contains(r.TagId)));
            }
            var shelves = await query.OrderByDescending(x => x.StartTs).Take(1000).ToListAsync(ct);
            var ruleTags = await db.AlarmRules.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.TagId, ct);
            return Results.Ok(shelves.Where(x => ruleTags.TryGetValue(x.RuleId, out var tag) && access.CanTag(Permission.View, tag))
                .Select(x => new { x.Id, x.RuleId, tagId = ruleTags[x.RuleId], x.Reason, x.User, x.StartTs, x.UntilTs, x.ClearedTs, x.ClearedBy }));
        }).RequireAuthorization(AccessPolicies.View);
    }

    private static async Task<IResult?> CheckSuppressTagAsync(HttpContext http, ConfigDbContext db, AlarmRule rule, CancellationToken ct)
    {
        if (rule.SuppressTagId is not int conditionId) return null;
        if (!await db.Tags.AnyAsync(x => x.Id == conditionId, ct)) return EndpointHelpers.BadRequest($"条件测点不存在：{conditionId}");
        // 条件测点至少要能查看，防止借抑制条件探测无权区域的测点。
        return await EndpointHelpers.RequireTagsAsync(http, [conditionId], Permission.View, $"报警规则引用条件测点 #{conditionId}");
    }

    private static async Task<IResult?> CheckExpressionTagsAsync(HttpContext http, ConfigDbContext db, AlarmRule rule, string action, CancellationToken ct)
    {
        if (rule.Type != AlarmRuleType.Expression || string.IsNullOrWhiteSpace(rule.Expression)) return null;
        Scada.Core.Calculations.TagExpression expression;
        try { expression = Scada.Core.Calculations.TagExpression.Compile(rule.Expression); }
        catch (Scada.Core.Calculations.TagExpressionException) { return null; } // 结构校验已给出可读错误
        var references = expression.Dependencies.Where(id => id != rule.TagId).ToArray();
        if (references.Length == 0) return null;
        var existing = await db.Tags.AsNoTracking().Where(x => references.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        var unknown = references.Except(existing).Take(20).ToArray();
        if (unknown.Length > 0) return EndpointHelpers.BadRequest($"表达式引用测点不存在：{string.Join(",", unknown)}");
        // 引用测点至少要能查看，防止借表达式探测无权区域的测点。
        return await EndpointHelpers.RequireTagsAsync(http, references, Permission.View, $"{action}（表达式引用测点）");
    }

    private static AlarmRule Copy(AlarmRule r) => new()
    {
        Id = r.Id, TagId = r.TagId, Type = r.Type, ActiveWhen = r.ActiveWhen, Threshold = r.Threshold, Level = r.Level,
        Category = r.Category, Consequence = r.Consequence, ResponseTimeSec = r.ResponseTimeSec, AutoPriority = r.AutoPriority,
        DelaySec = r.DelaySec,
        RecoverDeadband = r.RecoverDeadband, Enabled = r.Enabled, Message = r.Message, Cause = r.Cause, Action = r.Action,
        MessageEn = r.MessageEn, CauseEn = r.CauseEn, ActionEn = r.ActionEn, BitIndex = r.BitIndex, Expression = r.Expression,
        OffDelaySec = r.OffDelaySec,
        SuppressTagId = r.SuppressTagId, SuppressMode = r.SuppressMode, OutOfService = r.OutOfService, OutOfServiceReason = r.OutOfServiceReason
    };

    private static AlarmRule MapRule(AlarmRuleRequest request)
    {
        var rule = new AlarmRule
        {
            TagId = request.TagId,
            Type = request.Type,
            ActiveWhen = AlarmRuleText.NormalizeActiveWhen(request.Type, request.ActiveWhen),
            Threshold = request.Threshold,
            Level = request.Level,
            Category = request.Category,
            Consequence = request.Consequence,
            ResponseTimeSec = request.ResponseTimeSec,
            AutoPriority = request.AutoPriority,
            DelaySec = request.DelaySec,
            RecoverDeadband = request.RecoverDeadband,
            Enabled = request.Enabled,
            Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            MessageEn = string.IsNullOrWhiteSpace(request.MessageEn) ? null : request.MessageEn.Trim(),
            Cause = string.IsNullOrWhiteSpace(request.Cause) ? null : request.Cause.Trim(),
            CauseEn = string.IsNullOrWhiteSpace(request.CauseEn) ? null : request.CauseEn.Trim(),
            Action = string.IsNullOrWhiteSpace(request.Action) ? null : request.Action.Trim(),
            ActionEn = string.IsNullOrWhiteSpace(request.ActionEn) ? null : request.ActionEn.Trim(),
            OffDelaySec = request.OffDelaySec,
            BitIndex = request.Type == AlarmRuleType.Discrete ? request.BitIndex : null,
            Expression = request.Type == AlarmRuleType.Expression ? (string.IsNullOrWhiteSpace(request.Expression) ? null : request.Expression.Trim()) : null,
            SuppressTagId = request.SuppressMode == AlarmSuppressMode.None ? null : request.SuppressTagId,
            SuppressMode = request.SuppressMode
        };
        rule.ApplyAutoPriority();
        return rule;
    }
}
