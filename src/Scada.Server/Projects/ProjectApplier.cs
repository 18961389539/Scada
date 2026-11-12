using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Scripting;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Engine.Reports;
using Scada.Server.Endpoints;
using Scada.Server.Services;

using ExpressionMap = System.Collections.Generic.Dictionary<Scada.Core.Entities.Tag, (Scada.Server.Projects.PlanEntry Entry, string Expression)>;

namespace Scada.Server.Projects;

public sealed class ProjectApplyException(IReadOnlyList<string> errors, IReadOnlyList<string> forbidden)
    : Exception(forbidden.Count > 0 ? "权限不足：" + string.Join("；", forbidden.Take(5)) : "校验未通过：" + string.Join("；", errors.Take(5)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
    public IReadOnlyList<string> Forbidden { get; } = forbidden;
}

// 在调用方的事务内按依赖顺序执行计划（区域→角色→设备→测点→计算任务→计算表达式→报警规则→报表模板→对象模板→画面→删除）。
// 任一校验/权限问题都会累计并在结束时抛出，由调用方回滚整个事务，保证全部成功或全部不变。
public sealed class ProjectApplier : ProjectSection
{
    public ProjectApplier(ConfigDbContext db, CalcJobConfiguration jobs, AccessContext? access, int? userId, string userName,
        IDbContextFactory<RuntimeDbContext> runtimeFactory, Scada.Core.Options.ScadaSettings? settings = null,
        Scada.Core.Interfaces.IProtocolDriverAvailability? drivers = null)
        : base(new ProjectApplyContext(db, jobs, access, userId, userName, runtimeFactory, settings, drivers))
    {
    }

    public IReadOnlyList<string> Warnings => Ctx.Warnings;

    public async Task ClosePendingAlarmsAsync(CancellationToken ct)
    {
        if (PendingAlarmCloses.Count == 0) return;
        await using var operational = await Ctx.RuntimeFactory.CreateDbContextAsync(ct);
        foreach (var (tagId, reason) in PendingAlarmCloses)
            await operational.AlarmEvents.Where(x => x.TagId == tagId && x.EndTs == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndTs, Now).SetProperty(x => x.EndReason, reason), ct);
    }

    public async Task ApplyAsync(ProjectPlan plan, CancellationToken ct)
    {
        // 工程导入一次会改上千行；逐行审计既会淹没真正的操作记录，也会把审计写在事务中途。
        // 导入本身由 ProjectService 记一条汇总审计，这里抑制拦截器的逐实体留痕。
        using var _ = Scada.Core.Security.AuditContext.Suppress();
        await using var runtimeDb = await Ctx.RuntimeFactory.CreateDbContextAsync(ct);
        Ctx.AttachRuntime(runtimeDb);
        try
        {
            var master = new ProjectMasterDataApplier(Ctx);
            var calc = new ProjectCalcApplier(Ctx);
            var content = new ProjectContentApplier(Ctx);
            var control = new ProjectControlApplier(Ctx);
            var production = new ProjectProductionApplier(Ctx);
            var delete = new ProjectDeleteApplier(Ctx);
            var work = plan.Effective.ToList();
            await LoadAsync(ct);

            // 顺序即依赖：区域→角色→设备→测点→脚本库→计算任务→表达式→报警规则→报表→模板→图库→画面→布局
            // →控制→配方→生产→信号盘→删除（受引用约束的子表先删）→主数据删除→发布校验→依赖图重建。
            await master.ApplyAreasAsync(work, ct);
            await master.ApplyRolesAsync(work, ct);
            await master.ApplyDevicesAsync(work, ct);
            var expressions = await master.ApplyTagsAsync(work, ct);
            await master.ApplyScriptLibrariesAsync(work, ct);
            await calc.ApplyCalcJobsAsync(work, ct);
            await calc.ApplyCalcExpressionsAsync(expressions, ct);
            var rules = await RuleKeysAsync(ct);
            await calc.ApplyAlarmRulesAsync(work, rules, ct);
            await content.ApplyReportTemplatesAsync(work, ct);
            await content.ApplyObjectTemplatesAsync(work, ct);
            await content.ApplyHmiLibraryAsync(work, ct);
            await content.ApplyScreensAsync(work, ct);
            await content.ApplyHmiLayoutsAsync(work, ct);
            await control.ApplyControlAsync(work, ct);
            await control.ApplyRecipesAsync(work, ct);
            await production.ApplyProductionAsync(work, ct);
            await production.ApplyAnnunciatorsAsync(work, ct);
            await delete.DeleteUpsertDependentsAsync(work, ct);
            await delete.DeleteProductionAsync(work, ct);
            await delete.DeleteHmiAsync(work, rules, ct);
            await delete.DeleteCalcAsync(work, ct);
            await delete.DeleteTagsAsync(work, ct);
            await delete.DeleteMasterDataAsync(work, ct);
            await ValidatePublishedScreensAsync(work, ct);
            await RebuildDependenciesAsync(ct);

            if (Errors.Count > 0 || Forbidden.Count > 0) throw new ProjectApplyException(Errors, Forbidden.Distinct().ToList());
        }
        finally { Ctx.DetachRuntime(); }
    }

    private async Task ValidatePublishedScreensAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        // 画面也可能被本次工程操作取消发布或删除，固化图元和运行布局不能留下失效引用。
        if (work.Any(x => x.Section is "screens" or "hmiLayouts"))
        {
            var finalPublishedScreens = (await Db.ProcessScreens.AsNoTracking().Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            var finalTags = await Db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            foreach (var screen in await Db.ProcessScreens.AsNoTracking().Where(x => x.PublishedJson != null).ToListAsync(ct))
            {
                var document = JsonSerializer.Deserialize<ScreenDocument>(screen.PublishedJson!, ScreenJson)!;
                foreach (var issue in ScreenValidator.Validate(document, finalTags, finalPublishedScreens, publishedDocument: true, expandedDocument: true))
                    Errors.Add($"画面 {screen.PublishedName ?? screen.Name} 发布，节点 {issue.NodeId}：{issue.Message}");
            }
            foreach (var layout in await Db.HmiLayouts.AsNoTracking().Where(x => x.PublishedJson != null).ToListAsync(ct))
            {
                try
                {
                    var document = JsonSerializer.Deserialize<HmiLayoutDocument>(layout.PublishedJson!, ScreenJson);
                    if (HmiLayoutValidator.Validate(document, finalPublishedScreens) is string error) Errors.Add($"运行布局 {layout.PublishedName ?? layout.Name} 发布：{error}");
                }
                catch (JsonException ex) { Errors.Add($"运行布局 {layout.Name} 发布内容无效：{ex.Message}"); }
            }
        }
    }

    private async Task RebuildDependenciesAsync(CancellationToken ct)
    {
        // 计算依赖图整体校验（循环、缺失引用、层数），并重建依赖表。
        var all = await Db.Tags.AsNoTracking().ToListAsync(ct);
        try
        {
            var graph = CalculationGraph.Compile(all);
            await Db.TagDependencies.ExecuteDeleteAsync(ct);
            Db.TagDependencies.AddRange(graph.SelectMany(n => n.Expression.Dependencies.Select(input => new TagDependency { TagId = n.Tag.Id, InputTagId = input })));
            await SaveAsync(ct);
        }
        catch (TagExpressionException ex) { Errors.Add($"计算测点：{ex.Message}"); }
    }
}
