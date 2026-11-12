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

/// <summary>控制点、顺控、配方。</summary>
internal sealed class ProjectControlApplier(ProjectApplyContext context) : ProjectSection(context)
{
    internal async Task ApplyControlAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        var tagIds = (await Db.Tags.Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        string? CheckExpr(PlanEntry entry, string label, string? expr)
        {
            var error = Scada.Engine.Control.ControlCommandService.ValidateExpression(expr, tagIds.Contains);
            if (error is not null) Error(entry, $"{label}：{error}");
            return error;
        }
        foreach (var entry in Upserts(work, "controlPoints"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<ControlPoint>(ProjectFormat.Json)!;
            var tag = await Db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == incoming.TagId, ct);
            Need(Permission.Configure, entry, await TagAreasAsync(incoming.TagId, ct));
            if (tag is null || tag.Kind != TagKind.Physical || !tag.Writable) { Error(entry, "只有可写的采集测点可以配置控制策略"); continue; }
            if (CheckExpr(entry, "许可条件", incoming.PermitExpression) is not null || CheckExpr(entry, "反馈条件", incoming.FeedbackExpression) is not null) continue;
            if (incoming.ApprovalTimeoutSec is < 30 or > 3600 || incoming.FeedbackTimeoutMs is < 100 or > 600_000) { Error(entry, "复核有效期或反馈超时超出范围"); continue; }
            incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            var existing = await Db.ControlPoints.SingleOrDefaultAsync(x => x.TagId == incoming.TagId, ct);
            if (existing is null) Db.ControlPoints.Add(incoming);
            else { incoming.Id = existing.Id; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
        foreach (var entry in Upserts(work, "controlSequences"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            // 旧包中的线性步骤（无编号/类型）在此规范化，与旧顺控兼容。
            var steps = Scada.Engine.Control.SequenceGraph.Normalize(data["steps"]?.Deserialize<List<ControlSequenceStep>>(ProjectFormat.Json) ?? []); data.Remove("steps");
            var incoming = data.Deserialize<ControlSequence>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await Db.ControlSequences.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            var stepAreas = new List<int>(); foreach (var step in steps.Where(x => x.TagId is not null)) stepAreas.AddRange(await TagAreasAsync(step.TagId!.Value, ct));
            Need(Permission.Configure, entry, [incoming.AreaId, .. stepAreas, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            var stepTags = await Db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            if (Scada.Server.Endpoints.ControlEndpoints.ValidateSteps(steps, stepTags) is string stepError) { Error(entry, stepError); continue; }
            if (existing?.Id is int sequenceExistingId && await Runtime.ControlSequenceRuns.AnyAsync(x => x.SequenceId == sequenceExistingId && x.Status == ControlRunStatus.Running, ct)) { Error(entry, "顺控正在运行，不能修改"); continue; }
            incoming.StepsJson = JsonSerializer.Serialize(steps, Scada.Engine.Control.ControlWorkflowService.Json); incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            if (existing is null) { incoming.Revision = 1; Db.ControlSequences.Add(incoming); }
            else { incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
    }

    internal async Task ApplyRecipesAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "recipeTemplates"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var parameters = data["parameters"]?.Deserialize<List<RecipeParameter>>(ProjectFormat.Json) ?? []; data.Remove("parameters");
            var items = data["recipes"]?.AsArray().Select(x => (Name: x!["name"]?.GetValue<string>()?.Trim() ?? "", Description: x["description"]?.GetValue<string>(),
                Values: x["values"]?.Deserialize<List<RecipeValue>>(ProjectFormat.Json) ?? [])).ToList() ?? []; data.Remove("recipes");
            var incoming = data.Deserialize<RecipeTemplate>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await Db.RecipeTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            var paramAreas = new List<int>(); foreach (var p in parameters) paramAreas.AddRange(await TagAreasAsync(p.TagId, ct));
            Need(Permission.Configure, entry, [incoming.AreaId, .. paramAreas, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            var tagMap = await Db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            if (Scada.Server.Endpoints.RecipeEndpoints.ValidateParameters(parameters, tagMap) is string perror) { Error(entry, perror); continue; }
            var bad = false;
            foreach (var item in items)
            {
                if (item.Name.Length is 0 or > 100) { Error(entry, "配方名称须为 1～100 个字符"); bad = true; }
                var errors = Scada.Engine.Control.RecipeService.Validate(parameters, item.Values, tagMap);
                if (errors.Count > 0) { Error(entry, $"配方 {item.Name}：{string.Join("；", errors.Take(3))}"); bad = true; }
            }
            if (items.GroupBy(x => x.Name).FirstOrDefault(g => g.Count() > 1) is { } dupe) { Error(entry, $"配方 {dupe.Key} 重复"); bad = true; }
            if (bad) continue;
            if (existing?.Id is int templateExistingId && await Runtime.RecipeDownloads.AnyAsync(x => x.TemplateId == templateExistingId && (x.Status == RecipeDownloadStatus.Running || x.Status == RecipeDownloadStatus.Pending), ct))
            { Error(entry, "该模板的配方正在下发或待复核，不能修改"); continue; }
            incoming.ParametersJson = JsonSerializer.Serialize(parameters, Scada.Engine.Control.RecipeService.Json); incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            RecipeTemplate template;
            if (existing is null) { incoming.Revision = 1; template = Db.RecipeTemplates.Add(incoming).Entity; }
            else { incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; Db.Entry(existing).CurrentValues.SetValues(incoming); template = existing; }
            await SaveAsync(ct);
            // 配方：包中的值与当前版本不同则追加新版本（旧版本保留）；包里没有的配方删除（模板整体以包为准）。
            var current = await Db.Recipes.Where(x => x.TemplateId == template.Id).ToListAsync(ct);
            foreach (var gone in current.Where(x => items.All(i => i.Name != x.Name))) Db.Recipes.Remove(gone);
            foreach (var item in items)
            {
                var ordered = parameters.Select(p => new RecipeValue { Key = p.Key, Value = item.Values.First(v => v.Key == p.Key).Value }).ToList();
                var json = JsonSerializer.Serialize(ordered, Scada.Engine.Control.RecipeService.Json);
                var recipe = current.FirstOrDefault(x => x.Name == item.Name);
                if (recipe is not null)
                {
                    var last = await Db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.RecipeId == recipe.Id && x.Version == recipe.CurrentVersion, ct);
                    recipe.Description = item.Description;
                    if (last?.ValuesJson == json) continue;
                }
                else { recipe = Db.Recipes.Add(new Recipe { TemplateId = template.Id, Name = item.Name, Description = item.Description, CurrentVersion = 0 }).Entity; }
                recipe.CurrentVersion++; recipe.UpdatedTs = Now; recipe.UpdatedBy = UserName;
                await SaveAsync(ct);
                Db.RecipeVersions.Add(new RecipeVersion { RecipeId = recipe.Id, Version = recipe.CurrentVersion, ValuesJson = json, Note = "工程导入/回滚", Source = "project", CreatedTs = Now, CreatedBy = UserName });
            }
        }
        await SaveAsync(ct);
    }

}
