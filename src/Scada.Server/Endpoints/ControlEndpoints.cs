using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record ControlCheckRequest(int TagId, double Value);
public sealed record ControlPointRequest(bool RequireConfirm, bool DualApproval, int ApprovalTimeoutSec, string? PermitExpression, string? PermitMessage,
    double? LimitMin, double? LimitMax, double? MaxStep, string? FeedbackExpression, int FeedbackTimeoutMs);
public sealed record DeviceModeRequest(DeviceControlMode Mode, string? Reason);
public sealed record LockoutRequest(int? DeviceId, int? TagId, ControlLockoutKind Kind, string? Reason);
public sealed record ReasonRequest(string? Reason);
public sealed record DecisionRequest(string? Comment);
public sealed record SequenceRequest(string Name, int AreaId, string? Description, bool DualApproval, List<ControlSequenceStep> Steps, long Revision = 0);
public sealed record SequenceStartRequest(bool Confirmed);

// 现场操作控制：控制点策略、就地/远程、挂牌、双人复核、顺控。除 /sequences/validate（只校验不保存）外按区域授权并写审计。
public static class ControlEndpoints
{
    public static void MapControlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/control").RequireAuthorization().AddEndpointFilter<AuditFailuresFilter>();

        group.MapPost("/check", async (ControlCheckRequest request, HttpContext http, ControlCommandService commands, CancellationToken ct) =>
        {
            if (!(await http.GetAccessAsync()).CanTag(Permission.Control, request.TagId)) return Results.Forbid();
            var check = await commands.CheckAsync(request.TagId, request.Value, Actor(http), ct);
            return check is null ? Results.NotFound() : Results.Ok(check);
        }).RequireAuthorization(AccessPolicies.Control);

        // ---- 控制点策略 ----
        group.MapGet("/points", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok((await db.ControlPoints.AsNoTracking().OrderBy(x => x.TagId).ToListAsync(ct)).Where(x => access.CanTag(Permission.View, x.TagId)));
        });
        group.MapPut("/points/{tagId:int}", async (int tagId, ControlPointRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, IConfigSnapshot config, CancellationToken ct) =>
        {
            if (!(await http.GetAccessAsync()).CanTag(Permission.Configure, tagId))
                return await Audit.ForbidAsync(http, $"配置控制策略：测点 #{tagId}", "无该测点区域的配置权限", "ControlPoint", tagId);
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tagId, ct);
            if (tag is null) return Results.NotFound();
            if (tag.Kind != TagKind.Physical || !tag.Writable) return EndpointHelpers.BadRequest("只有可写的采集测点可以配置控制策略");
            if (tag.DataType == TagDataType.String && (request.LimitMin is not null || request.LimitMax is not null || request.MaxStep is not null
                || !string.IsNullOrWhiteSpace(request.PermitExpression) || !string.IsNullOrWhiteSpace(request.FeedbackExpression)))
                return EndpointHelpers.BadRequest("字符串测点不支持限幅、变化率或许可/反馈条件");
            if (request.LimitMin is double a && request.LimitMax is double b && a > b) return EndpointHelpers.BadRequest("限幅下限不能大于上限");
            if (request.MaxStep is double step && (!double.IsFinite(step) || step <= 0)) return EndpointHelpers.BadRequest("单次变化限制须为正数");
            if (request.ApprovalTimeoutSec is < 30 or > 3600) return EndpointHelpers.BadRequest("复核有效期须为 30～3600 秒");
            if (request.FeedbackTimeoutMs is < 100 or > 600_000) return EndpointHelpers.BadRequest("反馈超时须为 100～600000 毫秒");
            var ids = (await db.Tags.Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            foreach (var (label, expr) in new[] { ("许可条件", request.PermitExpression), ("反馈条件", request.FeedbackExpression) })
                if (ControlCommandService.ValidateExpression(expr, ids.Contains) is string error) return EndpointHelpers.BadRequest($"{label}：{error}");
            var point = await db.ControlPoints.SingleOrDefaultAsync(x => x.TagId == tagId, ct);
            var before = point is null ? null : Copy(point);
            if (point is null) { point = new ControlPoint { TagId = tagId }; db.ControlPoints.Add(point); }
            point.RequireConfirm = request.RequireConfirm; point.DualApproval = request.DualApproval; point.ApprovalTimeoutSec = request.ApprovalTimeoutSec;
            point.PermitExpression = Blank(request.PermitExpression); point.PermitMessage = Blank(request.PermitMessage);
            point.LimitMin = request.LimitMin; point.LimitMax = request.LimitMax; point.MaxStep = request.MaxStep;
            point.FeedbackExpression = Blank(request.FeedbackExpression); point.FeedbackTimeoutMs = request.FeedbackTimeoutMs;
            point.UpdatedTs = ScadaTime.NowMs(); point.UpdatedBy = http.User.GetUserName();
            runtime.OperationLogs.Add(Audit.Change(http, $"配置控制策略：{tag.Name}", "ControlPoint", tagId, before, Copy(point)));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Ok(point);
        });
        group.MapDelete("/points/{tagId:int}", async (int tagId, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (!(await http.GetAccessAsync()).CanTag(Permission.Configure, tagId))
                return await Audit.ForbidAsync(http, $"删除控制策略：测点 #{tagId}", "无该测点区域的配置权限", "ControlPoint", tagId);
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var point = await db.ControlPoints.SingleOrDefaultAsync(x => x.TagId == tagId, ct);
            if (point is null) return Results.NotFound();
            db.ControlPoints.Remove(point);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除控制策略：测点 #{tagId}", "ControlPoint", tagId, Copy(point), null));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- 就地/远程 ----
        group.MapGet("/modes", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            return Results.Ok((await runtime.DeviceControlStates.AsNoTracking().ToListAsync(ct)).Where(x => access.CanDevice(Permission.View, x.DeviceId)));
        });
        group.MapPut("/modes/{deviceId:int}", async (int deviceId, DeviceModeRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (!(await http.GetAccessAsync()).CanDevice(Permission.Control, deviceId))
                return await Audit.ForbidAsync(http, $"切换就地/远程：设备 #{deviceId}", "无该设备区域的控制权限", "Device", deviceId);
            if (!Enum.IsDefined(request.Mode))
            {
                var modeOptions = string.Join("/", Enum.GetNames<DeviceControlMode>());
                return EndpointHelpers.BadRequest($"模式无效：{request.Mode}（可选 {modeOptions}）");
            }
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 200) return EndpointHelpers.BadRequest("请填写切换原因（最多 200 字）");
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null) return Results.NotFound();
            var state = await runtime.DeviceControlStates.SingleOrDefaultAsync(x => x.DeviceId == deviceId, ct);
            var before = state?.Mode ?? DeviceControlMode.Remote;
            if (state is null) { state = new DeviceControlState { DeviceId = deviceId }; runtime.DeviceControlStates.Add(state); }
            state.Mode = request.Mode; state.Reason = request.Reason.Trim(); state.ChangedTs = ScadaTime.NowMs(); state.ChangedBy = http.User.GetUserName();
            runtime.OperationLogs.Add(Audit.Change(http, $"切换控制权：{device.Name} → {(request.Mode == DeviceControlMode.Local ? "就地" : "远程")}", "Device", deviceId,
                new { mode = before.ToString() }, new { mode = request.Mode.ToString(), reason = state.Reason }));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Ok(state);
        }).RequireAuthorization(AccessPolicies.Control);

        // ---- 挂牌 ----
        group.MapGet("/lockouts", async (bool? all, int? before, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var query = runtime.ControlLockouts.AsNoTracking();
            if (all != true) query = query.Where(x => x.RemovedTs == null);
            if (before is int cursor) query = query.Where(x => x.Id < cursor);
            // 先按当前用户能看的设备/测点收窄，再取最近 500。先截再按权限过滤时，别的区域的新挂牌会把本区域更早的挤掉。
            if (access.AreasWith(Permission.View) is { } areas)
            {
                var allowed = areas.ToArray();
                var deviceIds = access.Model.DeviceAreas.Where(pair => allowed.Contains(pair.Value)).Select(pair => pair.Key).ToArray();
                var tagIds = access.Model.TagAreas.Where(pair => pair.Value.All(area => allowed.Contains(area))).Select(pair => pair.Key).ToArray();
                if (deviceIds.Length == 0 && tagIds.Length == 0)
                {
                    EndpointHelpers.MarkPage(http, false, null);
                    return Results.Ok(Array.Empty<ControlLockout>());
                }
                if (deviceIds.Length == 0)
                    query = query.Where(x => x.DeviceId == null && x.TagId != null && tagIds.Contains(x.TagId.Value));
                else if (tagIds.Length == 0)
                    query = query.Where(x => x.DeviceId != null && deviceIds.Contains(x.DeviceId.Value));
                else
                    query = query.Where(x => (x.DeviceId != null && deviceIds.Contains(x.DeviceId.Value)) || (x.DeviceId == null && x.TagId != null && tagIds.Contains(x.TagId.Value)));
            }
            var rows = await query.OrderByDescending(x => x.Id).Take(501).ToListAsync(ct);
            var more = rows.Count > 500;
            if (more) rows.RemoveAt(rows.Count - 1);
            EndpointHelpers.MarkPage(http, more, rows.Count > 0 ? rows[^1].Id : null);
            return Results.Ok(rows);
        });
        group.MapPost("/lockouts", async (LockoutRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if ((request.DeviceId is null) == (request.TagId is null)) return EndpointHelpers.BadRequest("请指定设备或测点之一");
            if (!Enum.IsDefined(request.Kind))
            {
                var kindOptions = string.Join("/", Enum.GetNames<ControlLockoutKind>());
                return EndpointHelpers.BadRequest($"挂牌类型无效：{request.Kind}（可选 {kindOptions}）");
            }
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 200) return EndpointHelpers.BadRequest("请填写挂牌原因（最多 200 字）");
            var access = await http.GetAccessAsync();
            var allowed = request.DeviceId is int d ? access.CanDevice(Permission.Control, d) : access.CanTag(Permission.Control, request.TagId!.Value);
            var target = request.DeviceId is int dd ? $"设备 #{dd}" : $"测点 #{request.TagId}";
            if (!allowed) return await Audit.ForbidAsync(http, $"挂牌：{target}", "无该区域的控制权限", "ControlLockout");
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            if (request.DeviceId is int id && !await db.Devices.AnyAsync(x => x.Id == id, ct) || request.TagId is int t && !await db.Tags.AnyAsync(x => x.Id == t, ct)) return Results.NotFound();
            var lockout = new ControlLockout
            {
                DeviceId = request.DeviceId, TagId = request.TagId, Kind = request.Kind, Reason = request.Reason.Trim(),
                PlacedByUserId = http.User.GetUserId(), PlacedBy = http.User.GetUserName(), PlacedTs = ScadaTime.NowMs()
            };
            runtime.ControlLockouts.Add(lockout);
            await runtime.SaveChangesAsync(ct);
            runtime.OperationLogs.Add(Audit.Change(http, $"挂牌（{(request.Kind == ControlLockoutKind.NoOperate ? "禁止操作" : "检修中")}）：{target}", "ControlLockout", lockout.Id, null, lockout));
            await runtime.SaveChangesAsync(ct);
            return Results.Created($"/api/control/lockouts/{lockout.Id}", lockout);
        }).RequireAuthorization(AccessPolicies.Control);
        group.MapPost("/lockouts/{id:int}/remove", async (int id, ReasonRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) return EndpointHelpers.BadRequest("请填写摘牌说明");
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var lockout = await runtime.ControlLockouts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (lockout is null) return Results.NotFound();
            if (lockout.RemovedTs is not null) return Results.Conflict(new { message = "已摘牌" });
            var access = await http.GetAccessAsync();
            var configure = lockout.DeviceId is int d ? access.CanDevice(Permission.Configure, d) : access.CanTag(Permission.Configure, lockout.TagId!.Value);
            var control = lockout.DeviceId is int dc ? access.CanDevice(Permission.Control, dc) : access.CanTag(Permission.Control, lockout.TagId!.Value);
            // 谁挂谁摘；挂牌人不在时由该区域的配置管理员摘牌。
            if (!(control && lockout.PlacedByUserId == http.User.GetUserId()) && !configure)
                return await Audit.ForbidAsync(http, $"摘牌 #{id}", "只有挂牌人或该区域的配置管理员可以摘牌", "ControlLockout", id);
            var before = new { lockout.RemovedTs, lockout.RemovedBy };
            lockout.RemovedTs = ScadaTime.NowMs(); lockout.RemovedBy = http.User.GetUserName(); lockout.RemoveReason = request.Reason.Trim()[..Math.Min(200, request.Reason.Trim().Length)];
            runtime.OperationLogs.Add(Audit.Change(http, $"摘牌 #{id}（挂牌人 {lockout.PlacedBy}）", "ControlLockout", id, before, new { lockout.RemovedTs, lockout.RemovedBy, lockout.RemoveReason }));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Ok(lockout);
        });

        // ---- 双人复核 ----
        group.MapGet("/requests", async (string? status, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlWorkflowService workflow, CancellationToken ct) =>
        {
            await workflow.ExpireAsync(ct);
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var query = runtime.ControlRequests.AsNoTracking();
            var filteredStatus = Enum.TryParse<ControlRequestStatus>(status, true, out var s);
            if (filteredStatus) query = query.Where(x => x.Status == s);
            // 先按能看的测点、顺控区域、配方区域收窄。先截再过滤时，别的区域的新请求会把本区域的挤掉。
            if (access.AreasWith(Permission.View) is { } areas)
            {
                var allowed = areas.ToArray();
                var tagIds = access.Model.TagAreas.Where(pair => pair.Value.All(area => allowed.Contains(area))).Select(pair => pair.Key).ToArray();
                var sequenceIds = await db.ControlSequences.AsNoTracking().Where(x => allowed.Contains(x.AreaId)).Select(x => x.Id).ToArrayAsync(ct);
                var visibleDownloads = runtime.RecipeDownloads.AsNoTracking().Where(x => allowed.Contains(x.AreaId)).Select(x => x.Id);
                IQueryable<ControlRequest>? visible = null;
                void Add(IQueryable<ControlRequest> part) => visible = visible is null ? part : visible.Concat(part);
                if (tagIds.Length > 0) Add(query.Where(x => x.TagId != null && tagIds.Contains(x.TagId.Value)));
                Add(query.Where(x => x.TagId == null && x.RecipeDownloadId != null && visibleDownloads.Contains(x.RecipeDownloadId.Value)));
                Add(query.Where(x => x.TagId == null && x.RecipeDownloadId == null && x.SequenceId != null && sequenceIds.Contains(x.SequenceId.Value)));
                query = visible ?? query.Where(x => false);
            }
            // 未指定状态时，待复核单独取出。否则本区域更新的已完成请求会把还没批的挤出最近 300 条。
            List<ControlRequest> list;
            if (filteredStatus)
                list = await query.OrderByDescending(x => x.RequestedTs).Take(300).ToListAsync(ct);
            else
            {
                var pending = await query.Where(x => x.Status == ControlRequestStatus.Pending).OrderByDescending(x => x.RequestedTs).Take(300).ToListAsync(ct);
                var rest = await query.Where(x => x.Status != ControlRequestStatus.Pending).OrderByDescending(x => x.RequestedTs).Take(300).ToListAsync(ct);
                list = pending.Concat(rest).OrderByDescending(x => x.RequestedTs).ToList();
            }
            var sequences = await db.ControlSequences.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var downloadIds = list.Where(x => x.RecipeDownloadId is not null).Select(x => x.RecipeDownloadId!.Value).ToList();
            var downloads = await runtime.RecipeDownloads.AsNoTracking().Where(x => downloadIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var uid = http.User.GetUserId();
            bool Visible(ControlRequest x) => x.TagId is int t ? access.CanTag(Permission.View, t)
                : x.RecipeDownloadId is int d ? downloads.TryGetValue(d, out var dl) && access.Has(Permission.View, dl.AreaId)
                : sequences.TryGetValue(x.SequenceId ?? 0, out var q) && access.Has(Permission.View, q.AreaId);
            return Results.Ok(list.Where(Visible).Select(x => new
            {
                request = x, sequenceName = x.SequenceId is int q && sequences.TryGetValue(q, out var seq) ? seq.Name : null,
                recipe = x.RecipeDownloadId is int d && downloads.TryGetValue(d, out var dl) ? RecipeEndpoints.DownloadView(dl) : null, mine = x.RequestedByUserId == uid
            }));
        });
        group.MapPost("/requests/{id:guid}/approve", (Guid id, DecisionRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlWorkflowService workflow, CancellationToken ct) =>
            DecideAsync(id, true, request, http, factory, runtimeFactory, workflow, ct)).RequireAuthorization(AccessPolicies.Control);
        group.MapPost("/requests/{id:guid}/reject", (Guid id, DecisionRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlWorkflowService workflow, CancellationToken ct) =>
            DecideAsync(id, false, request, http, factory, runtimeFactory, workflow, ct)).RequireAuthorization(AccessPolicies.Control);

        // ---- 顺控 ----
        group.MapGet("/sequences", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok((await db.ControlSequences.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)).Where(x => access.Has(Permission.View, x.AreaId)).Select(View));
        });
        // 只校验不保存：图形编辑器据此在节点上标出错误（stepIds 为错误涉及的步骤）。
        group.MapPost("/sequences/validate", async (SequenceRequest request, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (request.Steps is null || request.Steps.Count is 0 or > SequenceGraph.MaxSteps) return Results.Ok(new { ok = false, message = $"步骤须为 1～{SequenceGraph.MaxSteps} 个", stepIds = Array.Empty<string>() });
            await using var db = await factory.CreateDbContextAsync(ct);
            var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var steps = SequenceGraph.Normalize(request.Steps);
            var error = ValidateSteps(steps, tags);
            return Results.Ok(new { ok = error is null, message = error, stepIds = error is null ? [] : SequenceGraph.StepsInMessage(steps, error) });
        });
        group.MapPost("/sequences", (SequenceRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) => SaveSequenceAsync(null, request, http, factory, runtimeFactory, ct));
        group.MapPut("/sequences/{id:int}", (int id, SequenceRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) => SaveSequenceAsync(id, request, http, factory, runtimeFactory, ct));
        group.MapDelete("/sequences/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var sequence = await db.ControlSequences.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (sequence is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, sequence.AreaId)) return await Audit.ForbidAsync(http, $"删除顺控 {sequence.Name}", "无该区域的配置权限", "ControlSequence", id);
            if (await runtime.ControlSequenceRuns.AnyAsync(x => x.SequenceId == id && x.Status == ControlRunStatus.Running, ct)) return Results.Conflict(new { message = "顺控正在运行" });
            db.ControlSequences.Remove(sequence);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除顺控 {sequence.Name}", "ControlSequence", id, View(sequence), null));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        group.MapPost("/sequences/{id:int}/start", async (int id, SequenceStartRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlWorkflowService workflow, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var sequence = await db.ControlSequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (sequence is null) return Results.NotFound();
            if (await CanRunAsync(http, sequence) is string denied) return await Audit.ForbidAsync(http, $"执行顺控 {sequence.Name}", denied, "ControlSequence", id);
            if (!request.Confirmed) return EndpointHelpers.BadRequest("执行顺控前须确认");
            try
            {
                if (sequence.DualApproval)
                {
                    var pending = await workflow.RequestSequenceAsync(sequence, Actor(http), ct);
                    return Results.Ok(new { pending = true, approvalId = pending.Id, runId = (int?)null });
                }
                var run = await workflow.StartAsync(sequence, Actor(http), null, ct);
                return Results.Ok(new { pending = false, approvalId = (Guid?)null, runId = (int?)run.Id });
            }
            catch (ControlWorkflowException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        }).RequireAuthorization(AccessPolicies.Control);
        group.MapGet("/runs", async (int? sequenceId, int? before, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var query = runtime.ControlSequenceRuns.AsNoTracking();
            if (sequenceId is int s) query = query.Where(x => x.SequenceId == s);
            if (access.AreasWith(Permission.View) is { } areas) { var allowed = areas.ToArray(); query = query.Where(x => allowed.Contains(x.AreaId)); }
            if (before is int cursor) query = query.Where(x => x.Id < cursor);
            var rows = await query.OrderByDescending(x => x.Id).Take(201).ToListAsync(ct);
            var more = rows.Count > 200;
            if (more) rows.RemoveAt(rows.Count - 1);
            EndpointHelpers.MarkPage(http, more, rows.Count > 0 ? rows[^1].Id : null);
            return Results.Ok(rows.Select(RunView));
        });
        group.MapGet("/runs/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var run = await runtime.ControlSequenceRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return run is null || !(await http.GetAccessAsync()).Has(Permission.View, run.AreaId) ? Results.NotFound() : Results.Ok(RunView(run));
        });
        group.MapPost("/runs/{id:int}/abort", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlWorkflowService workflow, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var run = await runtime.ControlSequenceRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Control, run.AreaId)) return await Audit.ForbidAsync(http, $"中止顺控运行 #{id}", "无该区域的控制权限", "ControlSequenceRun", id);
            return await workflow.AbortAsync(id, Actor(http), ct) ? Results.Accepted() : Results.Conflict(new { message = "运行已结束" });
        }).RequireAuthorization(AccessPolicies.Control);
    }

    public static ControlActor Actor(HttpContext http) => new(http.User.GetUserId(), http.User.GetUserName());

    private static async Task<string?> CanRunAsync(HttpContext http, ControlSequence sequence)
    {
        var access = await http.GetAccessAsync();
        if (!access.Has(Permission.Control, sequence.AreaId)) return "无该顺控所在区域的控制权限";
        var missing = ControlWorkflowService.Steps(sequence).Where(x => x.TagId is not null).Select(x => x.TagId!.Value).Distinct().FirstOrDefault(t => !access.CanTag(Permission.Control, t));
        return missing != 0 ? $"无步骤测点 #{missing} 所在区域的控制权限" : null;
    }

    private static async Task<IResult> DecideAsync(Guid id, bool approve, DecisionRequest body, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlWorkflowService workflow, CancellationToken ct)
    {
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var request = await runtime.ControlRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (request is null) return Results.NotFound();
            var access = await http.GetAccessAsync();
            string? denied = null;
            if (request.TagId is int tag && !access.CanTag(Permission.Control, tag)) denied = "无该测点区域的控制权限";
            if (request.RecipeDownloadId is int did && await runtime.RecipeDownloads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == did, ct) is { } download
                && await db.RecipeTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == download.TemplateId, ct) is { } template
                && await RecipeEndpoints.CanDownloadAsync(http, template) is string recipeDenied) denied = recipeDenied;
            if (request.SequenceId is int sid && await db.ControlSequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sid, ct) is { } sequence && await CanRunAsync(http, sequence) is string why) denied = why;
            if (denied is not null) return await Audit.ForbidAsync(http, $"{(approve ? "批准" : "驳回")}控制申请 {id}", denied, "ControlRequest", id);
        }
        try
        {
            var (request, result, run) = await workflow.DecideAsync(id, approve, Actor(http), body.Comment, ct);
            return Results.Ok(new { request, result, runId = run?.Id });
        }
        catch (ControlWorkflowException ex)
        {
            if (ex.Status == 403) return await Audit.ForbidAsync(http, $"批准控制申请 {id}", ex.Message, "ControlRequest", id);
            return Results.Json(new { message = ex.Message }, statusCode: ex.Status);
        }
    }

    private static async Task<IResult> SaveSequenceAsync(int? id, SequenceRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return EndpointHelpers.BadRequest("名称须为 1～100 个字符");
        if (request.Steps is null || request.Steps.Count is 0 or > SequenceGraph.MaxSteps) return EndpointHelpers.BadRequest($"步骤须为 1～{SequenceGraph.MaxSteps} 个");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        if (!await db.Areas.AnyAsync(x => x.Id == request.AreaId, ct)) return EndpointHelpers.BadRequest("区域不存在");
        var existing = id is int sid ? await db.ControlSequences.SingleOrDefaultAsync(x => x.Id == sid, ct) : null;
        if (id is not null && existing is null) return Results.NotFound();
        var action = existing is null ? $"新建顺控 {name}" : $"修改顺控 {existing.Name}";
        if (!access.Has(Permission.Configure, request.AreaId) || existing is not null && !access.Has(Permission.Configure, existing.AreaId))
            return await Audit.ForbidAsync(http, action, "无该区域的配置权限", "ControlSequence", id);
        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var steps = SequenceGraph.Normalize(request.Steps);
        var deniedTag = steps.Where(x => x.TagId is not null).Select(x => x.TagId!.Value).FirstOrDefault(t => tags.ContainsKey(t) && !access.CanTag(Permission.Configure, t));
        if (deniedTag != 0) return await Audit.ForbidAsync(http, action, $"步骤测点 #{deniedTag} 不在可配置区域", "ControlSequence", id);
        if (ValidateSteps(steps, tags) is string stepError) return EndpointHelpers.BadRequest(stepError);
        if (await db.ControlSequences.AnyAsync(x => x.Name == name && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("名称已存在");
        var before = existing is null ? null : View(existing);
        var sequence = existing ?? db.ControlSequences.Add(new ControlSequence()).Entity;
        if (existing is not null)
        {
            if (existing.Revision != request.Revision) return Results.Conflict(new { message = "顺控已被他人修改，请刷新" });
            if (await runtime.ControlSequenceRuns.AnyAsync(x => x.SequenceId == existing.Id && x.Status == ControlRunStatus.Running, ct)) return Results.Conflict(new { message = "顺控正在运行，不能修改" });
            sequence.Revision++;
        }
        sequence.Name = name; sequence.AreaId = request.AreaId; sequence.Description = Blank(request.Description); sequence.DualApproval = request.DualApproval;
        sequence.StepsJson = JsonSerializer.Serialize(steps, ControlWorkflowService.Json); sequence.UpdatedTs = ScadaTime.NowMs(); sequence.UpdatedBy = http.User.GetUserName();
        await db.SaveChangesAsync(ct);
        runtime.OperationLogs.Add(Audit.Change(http, action, "ControlSequence", sequence.Id, before, View(sequence)));
        await runtime.SaveChangesAsync(ct);
        return existing is null ? Results.Created($"/api/control/sequences/{sequence.Id}", View(sequence)) : Results.Ok(View(sequence));
    }

    // 顺控步骤图校验（接口与工程导入共用）。
    public static string? ValidateSteps(IReadOnlyList<ControlSequenceStep> steps, IReadOnlyDictionary<int, Tag> tags) =>
        SequenceGraph.Validate(steps, expr => ControlCommandService.ValidateExpression(expr, tags.ContainsKey), step =>
        {
            if (!tags.TryGetValue(step.TagId!.Value, out var tag) || tag.Kind != TagKind.Physical || !tag.Writable) return "测点不存在或不可写";
            return CommandValuePolicy.TryGetRawValue(tag, step.Value, out _, out var error) ? null : error;
        });

    private static object View(ControlSequence x) => new { x.Id, x.Name, x.AreaId, x.Description, x.DualApproval, x.Revision, x.UpdatedTs, x.UpdatedBy, steps = ControlWorkflowService.Steps(x) };
    private static object RunView(ControlSequenceRun x) => new
    {
        x.Id, x.SequenceId, x.SequenceName, x.AreaId, status = x.Status.ToString(), x.StartedTs, x.EndedTs, x.StartedBy, x.ApprovedBy, x.Message,
        steps = JsonSerializer.Deserialize<List<ControlStepResult>>(x.StepsJson, ControlWorkflowService.Json)
    };
    private static ControlPoint Copy(ControlPoint p) => new()
    {
        Id = p.Id, TagId = p.TagId, RequireConfirm = p.RequireConfirm, DualApproval = p.DualApproval, ApprovalTimeoutSec = p.ApprovalTimeoutSec, PermitExpression = p.PermitExpression,
        PermitMessage = p.PermitMessage, LimitMin = p.LimitMin, LimitMax = p.LimitMax, MaxStep = p.MaxStep, FeedbackExpression = p.FeedbackExpression, FeedbackTimeoutMs = p.FeedbackTimeoutMs
    };
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
