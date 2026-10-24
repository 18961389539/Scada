using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Reports;
using Scada.Server.Auth;
using Scada.Server.Reports;

namespace Scada.Server.Endpoints;

public sealed record ReportTemplateRequest(string Name, ReportPeriod Period, int AreaId, string? ShiftStarts, List<ReportColumn>? Columns,
    bool ScheduleEnabled, int? ScheduleDelayMin, int? EmailChannelId, string? EmailRecipients, long Revision = 0);
public sealed record ReportGenerateRequest(long? At);

// 报表模板与归档：模板按区域 Configure 管理（差分审计）；生成需 Configure；查看/下载需区域 View。
// 已生成报表只追加不修改：无修改/删除接口，下载时校验 SHA256。
public static class ReportTemplateEndpoints
{
    private static object View(ReportTemplate t) => new
    {
        t.Id, t.Name, t.Period, t.AreaId, t.ShiftStarts, columns = ReportTemplates.Columns(t.ColumnsJson), t.ScheduleEnabled, t.ScheduleDelayMin,
        t.EmailChannelId, t.EmailRecipients, t.Revision, t.UpdatedTs
    };
    private static object RunView(ReportRun r) => RunView(r, null);
    private static object RunView(ReportRun r, string? approvalStatus) => new
    {
        r.Id, r.TemplateId, r.TemplateName, r.TemplateRevision, r.AreaId, r.PeriodKey, r.FromTs, r.ToTs, r.Version, r.Trigger, r.GeneratedBy, r.GeneratedTs, r.XlsxSha256, r.PdfSha256, r.PurgedTs,
        signed = r.XlsxSignature is not null && r.PdfSignature is not null, r.SignerThumbprint, r.SignerSubject, r.SignedTs, r.SignatureError, approvalStatus
    };

    public static void MapReportTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/report-templates").RequireAuthorization().AddEndpointFilter<AuditFailuresFilter>();
        templates.MapGet("", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok((await db.ReportTemplates.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)).Where(x => access.Has(Permission.View, x.AreaId)).Select(View));
        });
        templates.MapPost("", (ReportTemplateRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            SaveAsync(null, request, http, factory, ct)).RequireAuthorization(AccessPolicies.Configure);
        templates.MapPut("/{id:int}", (int id, ReportTemplateRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            SaveAsync(id, request, http, factory, ct)).RequireAuthorization(AccessPolicies.Configure);
        templates.MapDelete("/{id:int}", async (int id, long revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var template = await db.ReportTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (template is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, template.AreaId))
                return await Audit.ForbidAsync(http, $"删除报表模板 #{id}", "无权配置该区域的报表", "ReportTemplate", id);
            if (template.Revision != revision) return Results.Conflict(new { message = "模板已被修改，请重新读取后核对" });
            db.OperationLogs.Add(Audit.Change(http, $"删除报表模板：{template.Name}", "ReportTemplate", id, View(template), null));
            db.ReportTemplates.Remove(template); await db.SaveChangesAsync(ct);
            return Results.NoContent(); // 已生成报表保留
        }).RequireAuthorization(AccessPolicies.Configure);
        templates.MapPost("/{id:int}/generate", async (int id, ReportGenerateRequest? request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, ReportArchiveService archive, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var template = await db.ReportTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (template is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, template.AreaId))
                return await Audit.ForbidAsync(http, $"生成报表 #{id}", "无权生成该区域的报表", "ReportTemplate", id);
            // 默认生成上一个完整周期。
            var at = request?.At ?? ReportPeriods.Previous(template.Period, template.ShiftStarts, ScadaTime.NowMs(), archive.Zone).From;
            try
            {
                var run = await archive.GenerateAsync(template, at, "manual", http.User.GetUserId(), http.User.GetUserName(), ct);
                return Results.Created($"/api/report-runs/{run.Id}", RunView(run));
            }
            catch (ReportTemplateException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        }).RequireAuthorization(AccessPolicies.Configure);

        var runs = app.MapGroup("/api/report-runs").RequireAuthorization();
        runs.MapGet("", async (int? templateId, int? take, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.ReportRuns.AsNoTracking();
            if (templateId is int t) query = query.Where(x => x.TemplateId == t);
            var list = await query.OrderByDescending(x => x.Id).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync(ct);
            var visible = list.Where(x => access.Has(Permission.View, x.AreaId)).ToList();
            var ids = visible.Select(x => x.Id).ToList();
            var approvals = (await db.ReportApprovals.AsNoTracking().Where(x => ids.Contains(x.RunId)).ToListAsync(ct)).ToLookup(x => x.RunId);
            return Results.Ok(visible.Select(r => RunView(r, ReportSigningEndpoints.Status([.. approvals[r.Id]]))));
        });
        runs.MapGet("/{id:long}", async (long id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null || !(await http.GetAccessAsync()).Has(Permission.View, run.AreaId)) return Results.NotFound();
            return Results.Ok(new { run = RunView(run), table = JsonDocument.Parse(run.DataJson).RootElement });
        });
        runs.MapGet("/{id:long}/download", async (long id, string? format, HttpContext http, IDbContextFactory<ConfigDbContext> factory, ReportArchiveService archive, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var run = await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null || !(await http.GetAccessAsync()).Has(Permission.View, run.AreaId)) return Results.NotFound();
            if (run.PurgedTs is not null) return Results.Problem("归档文件已按保留期删除，仅保留记录与数据快照", statusCode: 410);
            var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
            var (bytes, exists, hashOk, signature) = await archive.ReadCheckedAsync(run, pdf ? "pdf" : "xlsx", ct);
            if (bytes is null)
            {
                var reason = !exists ? "归档文件缺失" : !hashOk ? "SHA256 校验不一致" : "数字签名无效：" + signature.Error;
                await Audit.FailedAsync(factory, http, $"下载报表 #{id}", reason, ct, "ReportRun", id);
                return Results.Problem(reason + "，可能已被篡改", statusCode: 409);
            }
            // 验证结果随响应头返回（未签名的旧报表照常下载，但标明 unsigned）。
            http.Response.Headers["X-Report-Signature"] = signature.Present ? "valid" : "unsigned";
            return Results.File(bytes, pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                FileName(run, pdf ? "pdf" : "xlsx"));
        });
        ReportSigningEndpoints.Map(runs);
    }

    internal static string FileName(ReportRun run, string ext) => $"{run.TemplateName}_{run.PeriodKey.Replace(':', '-').Replace(' ', '_')}_v{run.Version}.{ext}";

    private static async Task<IResult> SaveAsync(int? id, ReportTemplateRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        var action = id is null ? $"创建报表模板：{request.Name}" : $"修改报表模板 #{id}";
        await using var db = await factory.CreateDbContextAsync(ct);
        ReportTemplate? existing = null;
        if (id is int current)
        {
            existing = await db.ReportTemplates.SingleOrDefaultAsync(x => x.Id == current, ct);
            if (existing is null) return Results.NotFound();
            if (!access.Has(Permission.Configure, existing.AreaId)) return await Audit.ForbidAsync(http, action, "无权配置该区域的报表", "ReportTemplate", current);
            if (existing.Revision != request.Revision) return Results.Conflict(new { message = "模板已被修改，请重新读取后核对" });
        }
        if (!await db.Areas.AnyAsync(x => x.Id == request.AreaId, ct)) return EndpointHelpers.BadRequest("区域不存在");
        if (!access.Has(Permission.Configure, request.AreaId)) return await Audit.ForbidAsync(http, action, $"无权配置区域 #{request.AreaId}", "Area", request.AreaId);
        var columns = request.Columns ?? [];
        var candidate = new ReportTemplate
        {
            Name = request.Name?.Trim() ?? "", Period = request.Period, AreaId = request.AreaId, ShiftStarts = string.IsNullOrWhiteSpace(request.ShiftStarts) ? "08:00,20:00" : request.ShiftStarts.Trim(),
            ColumnsJson = JsonSerializer.Serialize(columns, ReportTemplates.Json), ScheduleEnabled = request.ScheduleEnabled, ScheduleDelayMin = request.ScheduleDelayMin ?? ReportTemplate.DefaultScheduleDelayMin,
            EmailChannelId = request.EmailChannelId, EmailRecipients = request.EmailRecipients?.Trim() ?? ""
        };
        try { ReportTemplates.Validate(candidate, columns); }
        catch (ReportTemplateException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        // 列引用的测点须可查看；计算任务须存在且其区域可查看。
        var hidden = columns.Where(c => c.TagId is int t && c.Kind <= ReportColumnKind.TagSum && !access.CanTag(Permission.View, t)).Select(c => c.TagId!.Value).Distinct().ToArray();
        if (hidden.Length > 0) return await Audit.ForbidAsync(http, action, $"引用了无权查看的测点：{string.Join(",", hidden)}", "ReportTemplate", id);
        var tagIds = columns.Where(c => c.TagId is int && c.Kind <= ReportColumnKind.TagSum).Select(c => c.TagId!.Value).Distinct().ToArray();
        var missingTags = tagIds.Except(await db.Tags.Where(x => tagIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToArray();
        if (missingTags.Length > 0) return EndpointHelpers.BadRequest($"测点不存在：{string.Join(",", missingTags)}");
        foreach (var column in columns.Where(c => c.Kind == ReportColumnKind.CalcSnapshot))
        {
            var job = await db.CalcJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == column.JobId, ct);
            if (job is null) return EndpointHelpers.BadRequest($"计算任务 #{column.JobId} 不存在");
            if (!access.Has(Permission.View, job.AreaId)) return await Audit.ForbidAsync(http, action, $"无权查看计算任务 #{job.Id}", "ReportTemplate", id);
        }
        if (candidate.EmailChannelId is int channelId)
        {
            var channel = await db.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == channelId, ct);
            if (channel is null || channel.Type != NotificationChannelType.Email) return EndpointHelpers.BadRequest("报表投递须选择邮件通道");
        }
        if (await db.ReportTemplates.AnyAsync(x => x.Name == candidate.Name && x.Id != (id ?? 0), ct)) return Results.Conflict(new { message = "模板名称已存在" });

        var before = existing is null ? null : View(existing);
        var template = existing ?? new ReportTemplate();
        template.Name = candidate.Name; template.Period = candidate.Period; template.AreaId = candidate.AreaId; template.ShiftStarts = candidate.ShiftStarts;
        template.ColumnsJson = candidate.ColumnsJson; template.ScheduleEnabled = candidate.ScheduleEnabled; template.ScheduleDelayMin = candidate.ScheduleDelayMin;
        if (candidate.ScheduleEnabled && (existing is null || !existing.ScheduleEnabled)) template.ScheduleSinceTs = ScadaTime.NowMs();
        template.EmailChannelId = candidate.EmailChannelId; template.EmailRecipients = candidate.EmailRecipients;
        template.Revision = existing is null ? 1 : existing.Revision + 1; template.UpdatedTs = ScadaTime.NowMs();
        if (existing is null) db.ReportTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        db.OperationLogs.Add(Audit.Change(http, existing is null ? $"创建报表模板：{template.Name}" : $"修改报表模板：{template.Name}", "ReportTemplate", template.Id, before, View(template)));
        await db.SaveChangesAsync(ct);
        return existing is null ? Results.Created($"/api/report-templates/{template.Id}", View(template)) : Results.Ok(View(template));
    }
}
