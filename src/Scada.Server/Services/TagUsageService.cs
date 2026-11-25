using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Server.Endpoints;
using Scada.Server.Screens;

namespace Scada.Server.Services;

public sealed record TagUsageRef(string Category, int Id, string Name, string? Detail, bool Editable, bool RequiresRepublish = false);
public sealed record TagUsageView(int TagId, string TagName, IReadOnlyList<TagUsageRef> References, bool Unreadable);
public sealed record TagRebindResult(int Screens, int Templates, int AlarmRules, int Sequences, int Recipes, IReadOnlyList<TagUsageRef> PublishedScreens)
{
    public int Replaced => Screens + Templates + AlarmRules + Sequences + Recipes;
}

public sealed class TagRebindException(string message, bool forbidden = false) : Exception(message)
{
    public bool Forbidden { get; } = forbidden;
}

/// <summary>
/// 测点反向引用扫描与批量替换。画面发布版本保持不可变：替换只改草稿，
/// 已发布画面通过 RequiresRepublish 返回，由组态人员重新发布后才解除引用。
/// </summary>
public sealed class TagUsageService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> WriteControlTypes = ["switch", "slider", "spinner", "input", "jog", "pulse", "selector", "knob"];

    public async Task<TagUsageView> ScanAsync(ConfigDbContext db, Tag tag, AccessContext? access, CancellationToken ct)
    {
        var references = new List<TagUsageRef>();
        var unreadable = false;

        foreach (var screen in await db.ProcessScreens.AsNoTracking().ToListAsync(ct))
        {
            var draft = TryParse(screen.DraftJson);
            if (draft is null) unreadable = true;
            else if (ScreenReferenceCount(draft, tag.Id) is int draftCount && draftCount > 0)
                references.Add(new("screenDraft", screen.Id, screen.Name, draftCount.ToString(), access is null || ScreenAreaRules.CanEdit(access, screen.AreaId)));

            if (screen.PublishedJson is null) continue;
            var published = TryParse(screen.PublishedJson);
            if (published is null) { unreadable = true; continue; }
            if (ScreenReferenceCount(published, tag.Id) is int publishedCount && publishedCount > 0)
                references.Add(new("screenPublished", screen.Id, screen.PublishedName ?? screen.Name, publishedCount.ToString(),
                    access is null || ScreenAreaRules.CanEdit(access, screen.AreaId), true));
        }

        foreach (var item in await db.HmiLibrary.AsNoTracking().Where(x => x.Kind == "template").ToListAsync(ct))
        {
            var document = TryParse(item.DocumentJson);
            if (document is null) { unreadable = true; continue; }
            if (ScreenReferenceCount(document, tag.Id) > 0)
                references.Add(new("template", item.Id, item.Name, item.Key, access is null || access.HasGlobal(Permission.Configure)));
        }

        foreach (var rule in await db.AlarmRules.AsNoTracking().ToListAsync(ct))
        {
            if (!RuleReferencesTag(rule, tag.Id)) continue;
            references.Add(new("alarmRule", rule.Id, rule.Message ?? $"规则 #{rule.Id}", $"{rule.Type} · {rule.Level}",
                access is null || access.CanTag(Permission.Configure, rule.TagId)));
        }

        foreach (var sequence in await db.ControlSequences.AsNoTracking().ToListAsync(ct))
        {
            var steps = ParseSteps(sequence.StepsJson);
            if (steps is null) { unreadable = true; continue; }
            var count = steps.Count(step => step.TagId == tag.Id || ExpressionReferences(step.ConfirmExpression, tag.Id) || ExpressionReferences(step.Expression, tag.Id));
            if (count > 0)
                references.Add(new("sequence", sequence.Id, sequence.Name, count.ToString(), access is null || access.Has(Permission.Configure, sequence.AreaId)));
        }

        foreach (var template in await db.RecipeTemplates.AsNoTracking().ToListAsync(ct))
        {
            var parameters = ParseParameters(template.ParametersJson);
            if (parameters is null) { unreadable = true; continue; }
            var count = parameters.Count(parameter => parameter.TagId == tag.Id);
            if (count > 0)
                references.Add(new("recipe", template.Id, template.Name, count.ToString(), access is null || access.Has(Permission.Configure, template.AreaId)));
        }

        var calculatedIds = await db.TagDependencies.AsNoTracking().Where(x => x.InputTagId == tag.Id).Select(x => x.TagId).Distinct().ToListAsync(ct);
        if (calculatedIds.Count > 0)
        {
            var calculated = await db.Tags.AsNoTracking().Where(x => calculatedIds.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            foreach (var item in calculated)
                references.Add(new("calculatedTag", item.Id, item.Name, null, access is null || access.CanTag(Permission.Configure, item.Id)));
        }

        return new(tag.Id, tag.Name, references
            .OrderBy(reference => CategoryOrder(reference.Category))
            .ThenBy(reference => reference.Name)
            .ToList(), unreadable);
    }

    /// <summary>对一台设备下的全部测点做与单点删除相同的引用扫描，只遍历一次画面与规则。</summary>
    public async Task<(IReadOnlyList<TagUsageRef> References, bool Unreadable)> ScanManyAsync(ConfigDbContext db, IReadOnlyCollection<int> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0) return ([], false);
        var wanted = tagIds as ISet<int> ?? tagIds.ToHashSet();
        var references = new List<TagUsageRef>();
        var unreadable = false;

        foreach (var screen in await db.ProcessScreens.AsNoTracking().ToListAsync(ct))
        {
            var draft = TryParse(screen.DraftJson);
            if (draft is null) unreadable = true;
            else
            {
                foreach (var tagId in wanted)
                    if (ScreenReferenceCount(draft, tagId) is int draftCount && draftCount > 0)
                        references.Add(new("screenDraft", screen.Id, screen.Name, $"#{tagId} x{draftCount}", true));
            }
            if (screen.PublishedJson is null) continue;
            var published = TryParse(screen.PublishedJson);
            if (published is null) { unreadable = true; continue; }
            foreach (var tagId in wanted)
                if (ScreenReferenceCount(published, tagId) is int publishedCount && publishedCount > 0)
                    references.Add(new("screenPublished", screen.Id, screen.PublishedName ?? screen.Name, $"#{tagId} x{publishedCount}", true, true));
        }

        foreach (var item in await db.HmiLibrary.AsNoTracking().Where(x => x.Kind == "template").ToListAsync(ct))
        {
            var document = TryParse(item.DocumentJson);
            if (document is null) { unreadable = true; continue; }
            foreach (var tagId in wanted)
                if (ScreenReferenceCount(document, tagId) > 0)
                    references.Add(new("template", item.Id, item.Name, $"#{tagId} {item.Key}", true));
        }

        foreach (var rule in await db.AlarmRules.AsNoTracking().ToListAsync(ct))
        {
            foreach (var tagId in wanted)
            {
                if (!RuleReferencesTag(rule, tagId)) continue;
                references.Add(new("alarmRule", rule.Id, rule.Message ?? $"规则 #{rule.Id}", $"#{tagId} · {rule.Type} · {rule.Level}", true));
            }
        }

        foreach (var sequence in await db.ControlSequences.AsNoTracking().ToListAsync(ct))
        {
            var steps = ParseSteps(sequence.StepsJson);
            if (steps is null) { unreadable = true; continue; }
            foreach (var tagId in wanted)
            {
                var count = steps.Count(step => step.TagId == tagId || ExpressionReferences(step.ConfirmExpression, tagId) || ExpressionReferences(step.Expression, tagId));
                if (count > 0)
                    references.Add(new("sequence", sequence.Id, sequence.Name, $"#{tagId} x{count}", true));
            }
        }

        foreach (var template in await db.RecipeTemplates.AsNoTracking().ToListAsync(ct))
        {
            var parameters = ParseParameters(template.ParametersJson);
            if (parameters is null) { unreadable = true; continue; }
            foreach (var tagId in wanted)
            {
                var count = parameters.Count(parameter => parameter.TagId == tagId);
                if (count > 0)
                    references.Add(new("recipe", template.Id, template.Name, $"#{tagId} x{count}", true));
            }
        }

        var calculated = await db.TagDependencies.AsNoTracking()
            .Where(x => wanted.Contains(x.InputTagId))
            .Select(x => new { x.InputTagId, x.TagId })
            .ToListAsync(ct);
        if (calculated.Count > 0)
        {
            var names = await db.Tags.AsNoTracking().Where(x => calculated.Select(c => c.TagId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            foreach (var item in calculated)
                references.Add(new("calculatedTag", item.TagId, names.GetValueOrDefault(item.TagId, $"#{item.TagId}"), $"#{item.InputTagId}", true));
        }

        return (references
            .OrderBy(reference => CategoryOrder(reference.Category))
            .ThenBy(reference => reference.Name)
            .ToList(), unreadable);
    }


    public async Task<TagRebindResult> RebindAsync(ConfigDbContext db, Tag source, Tag target, AccessContext access, string user, CancellationToken ct)
    {
        if (source.Id == target.Id) throw new TagRebindException("目标测点不能与当前测点相同");
        if ((source.DataType == Scada.Core.Enums.TagDataType.String) != (target.DataType == Scada.Core.Enums.TagDataType.String))
            throw new TagRebindException("字符串测点与数值测点不能互相替换，请选择数据类型兼容的目标测点");
        var usage = await ScanAsync(db, source, access, ct);
        if (usage.Unreadable) throw new TagRebindException("存在无法解析的画面或模板，无法安全替换引用；请先修复配置");
        var calculated = usage.References.Where(reference => reference.Category == "calculatedTag").ToList();
        if (calculated.Count > 0)
            throw new TagRebindException($"测点仍被计算测点引用，表达式需手动修改：{NameList(calculated)}");
        var forbidden = usage.References.Where(reference => !reference.Editable).ToList();
        if (forbidden.Count > 0)
            throw new TagRebindException($"以下引用不在你的配置权限内，无法批量替换：{NameList(forbidden)}", forbidden: true);

        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var now = ScadaTime.NowMs();
        int screens = 0, templates = 0, alarmRules = 0, sequences = 0, recipes = 0;

        var screenIds = usage.References.Where(reference => reference.Category == "screenDraft").Select(reference => reference.Id).ToHashSet();
        if (screenIds.Count > 0)
        {
            var rows = await db.ProcessScreens.Where(x => screenIds.Contains(x.Id)).ToListAsync(ct);
            if (rows.Count != screenIds.Count) throw new TagRebindException("画面已被其他用户修改或删除，请刷新后重试");
            foreach (var screen in rows)
            {
                var document = TryParse(screen.DraftJson) ?? throw new TagRebindException($"画面「{screen.Name}」内容无法解析");
                var (count, write) = ReplaceScreenReferences(document, source.Id, target.Id);
                if (count == 0) continue;
                if (write && !target.Writable) throw new TagRebindException($"目标测点 #{target.Id} 不可写，不能替换画面「{screen.Name}」中的控制引用");
                if (await ScreenAreaRules.MismatchAsync(db, screen.AreaId, document, ct) is string mismatch) throw new TagRebindException($"画面「{screen.Name}」：{mismatch}");
                screen.DraftJson = JsonSerializer.Serialize(document, Json);
                screen.Revision++;
                screen.DraftDirty = true;
                screen.UpdatedTs = now;
                screen.UpdatedBy = user;
                screens++;
            }
        }

        var templateIds = usage.References.Where(reference => reference.Category == "template").Select(reference => reference.Id).ToHashSet();
        if (templateIds.Count > 0)
        {
            var rows = await db.HmiLibrary.Where(x => templateIds.Contains(x.Id)).ToListAsync(ct);
            if (rows.Count != templateIds.Count) throw new TagRebindException("图库模板已被其他用户修改或删除，请刷新后重试");
            foreach (var template in rows)
            {
                var document = TryParse(template.DocumentJson) ?? throw new TagRebindException($"图库模板「{template.Name}」内容无法解析");
                var (count, write) = ReplaceScreenReferences(document, source.Id, target.Id);
                if (count == 0) continue;
                if (write && !target.Writable) throw new TagRebindException($"目标测点 #{target.Id} 不可写，不能替换模板「{template.Name}」中的控制引用");
                template.DocumentJson = JsonSerializer.Serialize(document, Json);
                template.Revision++;
                template.UpdatedTs = now;
                template.UpdatedBy = user;
                templates++;
            }
        }

        var ruleIds = usage.References.Where(reference => reference.Category == "alarmRule").Select(reference => reference.Id).ToHashSet();
        if (ruleIds.Count > 0)
        {
            var rows = await db.AlarmRules.Where(x => ruleIds.Contains(x.Id)).ToListAsync(ct);
            if (rows.Count != ruleIds.Count) throw new TagRebindException("报警规则已被其他用户修改或删除，请刷新后重试");
            foreach (var rule in rows)
            {
                if (rule.TagId == source.Id) rule.TagId = target.Id;
                if (rule.SuppressTagId == source.Id) rule.SuppressTagId = target.Id;
                rule.Expression = ReplaceExpression(rule.Expression, source.Id, target.Id);
                if (AlarmRuleCommandService.Validate(rule) is string error) throw new TagRebindException($"报警规则 #{rule.Id}：{error}");
                if (await AlarmRuleCommandService.ValidateTargetAsync(db, rule, rule.Id, ct) is string targetError) throw new TagRebindException($"报警规则 #{rule.Id}：{targetError}");
                alarmRules++;
            }
        }

        var sequenceIds = usage.References.Where(reference => reference.Category == "sequence").Select(reference => reference.Id).ToHashSet();
        if (sequenceIds.Count > 0)
        {
            var rows = await db.ControlSequences.Where(x => sequenceIds.Contains(x.Id)).ToListAsync(ct);
            if (rows.Count != sequenceIds.Count) throw new TagRebindException("顺控流程已被其他用户修改或删除，请刷新后重试");
            foreach (var sequence in rows)
            {
                var steps = ParseSteps(sequence.StepsJson) ?? throw new TagRebindException($"顺控「{sequence.Name}」步骤无法解析");
                var changed = false;
                foreach (var step in steps)
                {
                    if (step.TagId == source.Id) { step.TagId = target.Id; changed = true; }
                    step.ConfirmExpression = ReplaceExpression(step.ConfirmExpression, source.Id, target.Id);
                    step.Expression = ReplaceExpression(step.Expression, source.Id, target.Id);
                }
                if (!changed) continue;
                if (!target.Writable) throw new TagRebindException($"目标测点 #{target.Id} 不可写，不能替换顺控「{sequence.Name}」中的写入步骤");
                if (ControlEndpoints.ValidateSteps(steps, tags) is string error) throw new TagRebindException($"顺控「{sequence.Name}」：{error}");
                sequence.StepsJson = JsonSerializer.Serialize(steps, ControlWorkflowService.Json);
                sequence.Revision++;
                sequence.UpdatedTs = now;
                sequence.UpdatedBy = user;
                sequences++;
            }
        }

        var recipeIds = usage.References.Where(reference => reference.Category == "recipe").Select(reference => reference.Id).ToHashSet();
        if (recipeIds.Count > 0)
        {
            var rows = await db.RecipeTemplates.Where(x => recipeIds.Contains(x.Id)).ToListAsync(ct);
            if (rows.Count != recipeIds.Count) throw new TagRebindException("配方模板已被其他用户修改或删除，请刷新后重试");
            foreach (var template in rows)
            {
                var parameters = ParseParameters(template.ParametersJson) ?? throw new TagRebindException($"配方模板「{template.Name}」参数无法解析");
                var changed = false;
                foreach (var parameter in parameters) if (parameter.TagId == source.Id) { parameter.TagId = target.Id; changed = true; }
                if (!changed) continue;
                if (!target.Writable) throw new TagRebindException($"目标测点 #{target.Id} 不可写，不能替换配方模板「{template.Name}」中的参数");
                if (RecipeEndpoints.ValidateParameters(parameters, tags) is string error) throw new TagRebindException($"配方模板「{template.Name}」：{error}");
                template.ParametersJson = JsonSerializer.Serialize(parameters, RecipeService.Json);
                template.Revision++;
                template.UpdatedTs = now;
                template.UpdatedBy = user;
                recipes++;
            }
        }

        var published = usage.References.Where(reference => reference.Category == "screenPublished").ToList();
        return new(screens, templates, alarmRules, sequences, recipes, published);
    }

    private static int ScreenReferenceCount(ScreenDocument document, int tagId)
    {
        var count = 0;
        foreach (var node in document.Nodes ?? [])
        {
            if (node is null) continue;
            if (node.TagId == tagId) count++;
            if (node.AlarmTagId == tagId) count++;
            if (node.Tags?.Contains(tagId) == true) count++;
            if (node.Action?.TagId == tagId) count++;
            if (node.Animation?.TagId == tagId) count++;
            if (node.Commands is not null) count += node.Commands.Count(command => command?.TagId == tagId);
            if (node.Bindings is not null) count += node.Bindings.Count(binding => binding?.TagId == tagId);
        }
        return count;
    }

    // 返回替换数量，并标记是否涉及控制/写入引用：目标测点不可写时不能替换这些引用。
    private static (int Count, bool Write) ReplaceScreenReferences(ScreenDocument document, int oldTagId, int newTagId)
    {
        var count = 0;
        var write = false;
        foreach (var node in document.Nodes ?? [])
        {
            if (node is null) continue;
            if (node.TagId == oldTagId) { node.TagId = newTagId; count++; }
            if (node.AlarmTagId == oldTagId) { node.AlarmTagId = newTagId; count++; }
            if (node.Tags is not null)
                for (var index = 0; index < node.Tags.Count; index++)
                    if (node.Tags[index] == oldTagId) { node.Tags[index] = newTagId; count++; }
            if (node.Action?.TagId == oldTagId)
            {
                node.Action.TagId = newTagId;
                count++;
                if (node.Action.Type == "command" || WriteControlTypes.Contains(node.Type) || node.Type == "reset" && node.ResetMode == "tag") write = true;
            }
            if (node.Animation?.TagId == oldTagId) { node.Animation.TagId = newTagId; count++; }
            if (node.Commands is not null)
                for (var index = 0; index < node.Commands.Count; index++)
                {
                    var command = node.Commands[index];
                    if (command is null || command.TagId != oldTagId) continue;
                    node.Commands[index] = command with { TagId = newTagId };
                    count++;
                    write = true;
                }
            if (node.Bindings is not null)
                foreach (var binding in node.Bindings)
                    if (binding?.TagId == oldTagId) { binding.TagId = newTagId; count++; }
        }
        return (count, write);
    }

    private static bool RuleReferencesTag(AlarmRule rule, int tagId) =>
        rule.TagId == tagId || rule.SuppressTagId == tagId || ExpressionReferences(rule.Expression, tagId);

    private static bool ExpressionReferences(string? expression, int tagId)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        try { return TagExpression.Compile(expression).Dependencies.Contains(tagId); }
        catch { return false; }
    }

    private static string? ReplaceExpression(string? expression, int oldTagId, int newTagId)
    {
        if (!ExpressionReferences(expression, oldTagId)) return expression;
        return Regex.Replace(expression!, $@"#{oldTagId}(?![0-9])", $"#{newTagId}");
    }

    private static List<ControlSequenceStep>? ParseSteps(string json)
    {
        try { return JsonSerializer.Deserialize<List<ControlSequenceStep>>(json, ControlWorkflowService.Json); }
        catch (JsonException) { return null; }
    }

    private static List<RecipeParameter>? ParseParameters(string json)
    {
        try { return JsonSerializer.Deserialize<List<RecipeParameter>>(json, RecipeService.Json); }
        catch (JsonException) { return null; }
    }

    private static ScreenDocument? TryParse(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ScreenDocument>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static string NameList(IEnumerable<TagUsageRef> references) =>
        string.Join("、", references.Select(reference => reference.Name).Distinct().Take(5));

    private static int CategoryOrder(string category) => category switch
    {
        "screenDraft" => 0,
        "screenPublished" => 1,
        "template" => 2,
        "alarmRule" => 3,
        "sequence" => 4,
        "recipe" => 5,
        _ => 6
    };
}
