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

/// <summary>删除段：按依赖逆序清理，受引用约束的子表先删。</summary>
internal sealed class ProjectDeleteApplier(ProjectApplyContext context) : ProjectSection(context)
{
    internal async Task DeleteUpsertDependentsAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Deletes(work, "recipeTemplates"))
        {
            var template = await Db.RecipeTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [template.AreaId]);
            if (await Runtime.RecipeDownloads.AnyAsync(x => x.TemplateId == template.Id && (x.Status == RecipeDownloadStatus.Running || x.Status == RecipeDownloadStatus.Pending), ct))
            { Error(entry, "该模板的配方正在下发或待复核，不能删除"); continue; }
            Db.RecipeTemplates.Remove(template); // 下发记录保留
        }

        foreach (var entry in Deletes(work, "controlSequences"))
        {
            var sequence = await Db.ControlSequences.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [sequence.AreaId]);
            if (await Runtime.ControlSequenceRuns.AnyAsync(x => x.SequenceId == sequence.Id && x.Status == ControlRunStatus.Running, ct)) { Error(entry, "顺控正在运行，不能删除"); continue; }
            Db.ControlSequences.Remove(sequence); // 运行记录保留
        }
        foreach (var entry in Deletes(work, "controlPoints"))
        {
            var tag = await FindTagAsync(entry.FinalKey, ct); if (tag is null) continue;
            var point = await Db.ControlPoints.SingleOrDefaultAsync(x => x.TagId == tag.Id, ct); if (point is null) continue;
            Need(Permission.Configure, entry, await AreasOfAsync(tag, ct)); Db.ControlPoints.Remove(point);
        }
        await SaveAsync(ct);
    }

    internal async Task DeleteProductionAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Deletes(work, "productionLines"))
        {
            var line = await Db.ProductionLines.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [line.AreaId]);
            Db.ProductionLines.Remove(line);
        }
        foreach (var entry in Deletes(work, "productionShifts"))
        {
            var parts = entry.FinalKey.Split('/', 2);
            ProductionShift? shift = null;
            if (parts.Length == 2)
            {
                var area = await Db.Areas.AsNoTracking().SingleOrDefaultAsync(x => x.Name == parts[0], ct);
                if (area is not null) shift = await Db.ProductionShifts.SingleOrDefaultAsync(x => x.AreaId == area.Id && x.Name == parts[1], ct);
            }
            shift ??= await Db.ProductionShifts.SingleOrDefaultAsync(x => x.Name == entry.FinalKey, ct);
            if (shift is null) continue;
            Need(Permission.Configure, entry, [shift.AreaId]);
            Db.ProductionShifts.Remove(shift);
        }
        foreach (var entry in Deletes(work, "downtimeReasons"))
        {
            var reason = await Db.DowntimeReasons.SingleAsync(x => x.Code == entry.FinalKey, ct);
            if (reason.AreaId is int a) Need(Permission.Configure, entry, [a]); else NeedGlobal(Permission.Configure, entry);
            Db.DowntimeReasons.Remove(reason);
        }

        foreach (var entry in Deletes(work, "annunciatorPanels"))
        {
            var panel = await Db.AnnunciatorPanels.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (panel.AreaId is int a) Need(Permission.Configure, entry, [a]); else NeedGlobal(Permission.Configure, entry);
            Db.AnnunciatorPanels.Remove(panel);
        }
        foreach (var entry in Deletes(work, "productionProducts"))
        {
            var product = await Db.ProductionProducts.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (await Db.ProductionLines.AnyAsync(x => x.ProductId == product.Id, ct)) { Error(entry, "仍有产线引用该产品"); continue; }
            if (product.AreaId is int a) Need(Permission.Configure, entry, [a]); else NeedGlobal(Permission.Configure, entry);
            Db.ProductionProducts.Remove(product);
        }
    }

    internal async Task DeleteHmiAsync(IReadOnlyList<PlanEntry> work, Dictionary<string, int> rules, CancellationToken ct)
    {
        // 删除（仅回滚/镜像）：按依赖逆序。
        foreach (var entry in Deletes(work, "hmiLayouts"))
        {
            NeedGlobal(Permission.Configure, entry);
            var layout = await Db.HmiLayouts.SingleOrDefaultAsync(x => x.Name == entry.FinalKey, ct); if (layout is not null) Db.HmiLayouts.Remove(layout);
        }
        foreach (var entry in Deletes(work, "screens"))
        {
            NeedGlobal(Permission.Configure, entry);
            var screen = await Db.ProcessScreens.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (await Db.ObjectInstances.AnyAsync(x => x.ScreenId == screen.Id, ct)) { Error(entry, "画面仍属于模板对象，不能删除"); continue; }
            Db.ProcessScreens.Remove(screen);
        }
        await SaveAsync(ct);
        foreach (var entry in Deletes(work, "hmiLibrary"))
        {
            NeedGlobal(Permission.Configure, entry);
            var item = await Db.HmiLibrary.SingleOrDefaultAsync(x => x.Key == entry.FinalKey, ct); if (item is not null) Db.HmiLibrary.Remove(item);
        }
        foreach (var entry in Deletes(work, "reportTemplates"))
        {
            var report = await Db.ReportTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [report.AreaId]); Db.ReportTemplates.Remove(report); // 已生成报表保留
        }
        foreach (var entry in Deletes(work, "alarmRules"))
        {
            if (!rules.TryGetValue(entry.FinalKey, out var id)) continue;
            var rule = await Db.AlarmRules.SingleAsync(x => x.Id == id, ct);
            Need(Permission.Configure, entry, await TagAreasAsync(rule.TagId, ct)); Db.AlarmRules.Remove(rule);
        }
        await SaveAsync(ct);
    }

    internal async Task DeleteCalcAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Deletes(work, "calcJobs"))
        {
            var job = await Db.CalcJobs.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [job.AreaId]);
            try
            {
                foreach (var id in await CalcJobConfiguration.SyncOutputsAsync(Db, job, [], null, ct))
                    PendingAlarmCloses.Add((id, "计算任务输出移除"));
                Db.CalcJobs.Remove(job);
            }
            catch (CalculationRequestException ex) { Error(entry, ex.Message); }
        }
        await SaveAsync(ct);
        foreach (var entry in Deletes(work, "scriptLibraries"))
        {
            NeedGlobal(Permission.Configure, entry);
            var library = await Db.ScriptLibraries.SingleOrDefaultAsync(x => x.Name == entry.FinalKey, ct);
            if (library is null) continue;
            var names = await Db.CalcJobs.AsNoTracking().Select(x => new { x.Name, x.Libraries, x.PublishedLibraries }).ToListAsync(ct);
            if (names.Any(x => x.Libraries.Split(',', ' ', ';').Contains(library.Name) || x.PublishedLibraries.Split(',', ' ', ';').Contains(library.Name)))
            { Error(entry, "脚本库仍被计算任务引用"); continue; }
            Db.ScriptLibraries.Remove(library);
        }
        await SaveAsync(ct);
    }

    internal async Task DeleteTagsAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Deletes(work, "tags").OrderBy(x => x.FinalKey.StartsWith('@') ? 0 : 1))
        {
            var tag = await FindTagAsync(entry.FinalKey, ct); if (tag is null) continue;
            Need(Permission.Configure, entry, await AreasOfAsync(tag, ct));
            PendingAlarmCloses.Add((tag.Id, "工程回滚删除测点"));
            await Db.TagDependencies.Where(x => x.TagId == tag.Id).ExecuteDeleteAsync(ct);
            Db.Tags.Remove(tag);
        }
        await SaveAsync(ct);
    }

    internal async Task DeleteMasterDataAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Deletes(work, "devices"))
        {
            var device = await Db.Devices.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [device.AreaId]);
            if (await Db.ObjectInstances.AnyAsync(x => x.DeviceId == device.Id, ct)) { Error(entry, "设备仍属于模板对象，不能删除"); continue; }
            if (await Db.Tags.AnyAsync(x => x.DeviceId == device.Id, ct)) { Error(entry, "设备下仍有测点"); continue; }
            Db.Devices.Remove(device);
        }
        foreach (var entry in Deletes(work, "objectTemplates"))
        {
            NeedGlobal(Permission.Configure, entry);
            var template = await Db.ObjectTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (await Db.ObjectInstances.AnyAsync(x => x.TemplateId == template.Id, ct)) { Error(entry, "模板仍有对象实例，不能删除"); continue; }
            Db.ObjectTemplates.Remove(template);
        }
        foreach (var entry in Deletes(work, "roles"))
        {
            NeedGlobal(Permission.ManageUsers, entry);
            var role = await Db.AccessRoles.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (await Db.UserRoleAssignments.AnyAsync(x => x.RoleId == role.Id, ct)) { Error(entry, "角色仍分配给用户，不能删除"); continue; }
            Db.AccessRoles.Remove(role);
        }
        await SaveAsync(ct);
        foreach (var entry in Deletes(work, "areas"))
        {
            NeedGlobal(Permission.Configure, entry);
            var area = await Db.Areas.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (area.Id == Area.DefaultId) { Error(entry, "默认区域不能删除"); continue; }
            if (await Db.Devices.AnyAsync(x => x.AreaId == area.Id, ct) || await Db.Tags.AnyAsync(x => x.AreaId == area.Id, ct) || await Db.CalcJobs.AnyAsync(x => x.AreaId == area.Id, ct)
                || await Db.ReportTemplates.AnyAsync(x => x.AreaId == area.Id, ct) || await Db.UserRoleAssignments.AnyAsync(x => x.AreaId == area.Id, ct) || await Db.NotificationRoutes.AnyAsync(x => x.AreaId == area.Id, ct))
            { Error(entry, "区域仍被设备、测点、计算任务、报表模板、用户授权或通知路由使用"); continue; }
            Db.Areas.Remove(area);
        }
        await SaveAsync(ct);
    }

}
