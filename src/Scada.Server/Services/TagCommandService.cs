using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Services;

/// <summary>
/// 测点写入的唯一落地点：REST 端点与 MCP 工具共用同一套顺序与判据。
/// Excel 导入、工程导入与模板实例化按名字合并或批量生成，不走本服务的落盘；
/// 采集档位预算则与这里一样，按 <see cref=ScanCadence.EffectiveRate/> 计入设备默认周期。
///
/// 之前「协议校验 / 采集档位预算 / 同设备重名」在三处各写一遍，且端点先查重名再校验、
/// MCP 先校验再查重名——同一条非法请求在两个入口给出不同提示。规则只有一份之后，
/// 新增约束不会出现「界面拦住了、MCP 还能写进去」的缺口。
///
/// 授权与返回形态不属于本服务：区域权限、HTTP 状态码、MCP 的 success=false / 异常
/// 由各入口自己决定（两者鉴权模型不同）。
/// </summary>
public sealed class TagCommandService(ConfigReloader reloader, TagUsageService usage)
{
    public const string DuplicateNameMessage = "同一设备下测点名称已存在";

    public async Task<TagCommandResult> CreateAsync(
        ConfigDbContext db, Tag tag, Device device, CancellationToken cancellationToken)
    {
        var (error, issues) = await InspectIssuesAsync(db, tag, device, null, cancellationToken);
        if (error != TagCommandError.None) return TagCommandResult.Failed(error, issues);

        db.Tags.Add(tag);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TagCommandResult.Failed(TagCommandError.DuplicateName,
                [new ValidationIssue("tag.name.duplicate", nameof(Tag.Name), DuplicateNameMessage)]);
        }

        await ReloadAsync(cancellationToken);
        return TagCommandResult.Succeeded(tag);
    }

    /// <summary>按 <paramref name="updated"/> 覆盖可编辑字段；<c>Id</c>/<c>Kind</c> 与计算字段不参与合并。</summary>
    public async Task<TagCommandResult> UpdateAsync(
        ConfigDbContext db, Tag tag, Tag updated, Device device, CancellationToken cancellationToken, long? expectedRevision = null)
    {
        if (tag.Kind != TagKind.Physical)
            return TagCommandResult.Failed(TagCommandError.NotPhysical,
                [new ValidationIssue("tag.kind.notPhysical", nameof(Tag.Kind), "请通过计算测点或计算任务配置修改此测点")]);

        var (error, issues) = await InspectIssuesAsync(db, updated, device, tag.Id, cancellationToken);
        if (error != TagCommandError.None) return TagCommandResult.Failed(error, issues);

        var before = tag.Clone();
        if (!Scada.Core.Configuration.ConfigRevision.TryAccept(tag.CalculationRevision, expectedRevision, out var revisionIssue))
            return TagCommandResult.Failed(TagCommandError.Conflict, [revisionIssue!.Value]);
        tag.ApplyEditableFrom(updated);
        tag.CalculationRevision = Scada.Core.Configuration.ConfigRevision.Next(tag.CalculationRevision);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TagCommandResult.Failed(TagCommandError.DuplicateName,
                [new ValidationIssue("tag.name.duplicate", nameof(Tag.Name), DuplicateNameMessage)]);
        }

        await ReloadAsync(cancellationToken);
        return TagCommandResult.Succeeded(tag);
    }

    public async Task<TagCommandResult> DeleteAsync(
        ConfigDbContext db, Tag tag, CancellationToken cancellationToken)
    {
        if (tag.Kind != TagKind.Physical)
            return TagCommandResult.Failed(TagCommandError.NotPhysical,
                [new ValidationIssue("tag.kind.notPhysical", nameof(Tag.Kind), "请通过计算测点或计算任务配置删除此测点")]);

        if (await db.TagDependencies.AnyAsync(x => x.InputTagId == tag.Id, cancellationToken))
            return TagCommandResult.Failed(TagCommandError.Referenced,
                [new ValidationIssue("tag.referenced.calculation", null, "测点仍被计算定义引用，请先修改依赖定义")]);

        var references = await usage.ScanAsync(db, tag, null, cancellationToken);
        if (references.Unreadable)
            return TagCommandResult.Failed(TagCommandError.Referenced,
                [new ValidationIssue("tag.referenced.unreadable", null, "存在无法解析的画面或模板，无法确认测点引用，暂不能删除")]);
        if (references.References.Count > 0)
        {
            var summary = string.Join("、", references.References
                .GroupBy(reference => reference.Category)
                .OrderBy(group => group.Key)
                .Select(group => $"{CategoryLabel(group.Key)} {group.Count()}"));
            return TagCommandResult.Failed(TagCommandError.Referenced,
                [new ValidationIssue("tag.referenced.configurations", null,
                    $"测点仍被 {references.References.Count} 处配置引用：{summary}。请先使用“替换测点”或修改这些配置")]);
        }

        db.Tags.Remove(tag);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TagCommandResult.Failed(TagCommandError.Conflict,
                [new ValidationIssue("tag.conflict", null, "测点引用已变化，请重新读取后修改依赖定义")]);
        }

        await ReloadAsync(cancellationToken);
        return TagCommandResult.Succeeded(tag);
    }

    /// <summary>
    /// 校验顺序固定：协议与字段 → 采集档位预算 → 同设备重名。批量建点复用同一判据，
    /// 只是它先按名字数组做一次批量查询，避免 N 次重名查询。
    /// </summary>
    public async Task<(TagCommandError Error, IReadOnlyList<ValidationIssue> Issues)> InspectIssuesAsync(
        ConfigDbContext db, Tag tag, Device device, int? excludeTagId, CancellationToken cancellationToken)
    {
        var issues = ConfigurationValidator.TagIssues(tag, device);
        if (issues.Count > 0)
            return (TagCommandError.Validation, issues);

        // 与 ConfigurationValidator.TagIssues(tag, device) 用同一个设备：调用方保证 tag.DeviceId 就是 device.Id，
        // 这里显式取 device.Id，避免两者不一致时档位预算和重名查到别的设备上。
        var deviceId = device.Id;
        if (await ValidateLaneBudgetAsync(db, device, [tag.ScanRateMs], excludeTagId, cancellationToken) is string laneError)
            return (TagCommandError.LaneBudgetExceeded,
                [new ValidationIssue("tag.scanRate.laneBudget", nameof(Tag.ScanRateMs), laneError)]);

        var duplicate = excludeTagId is int exclude
            ? await db.Tags.AnyAsync(x => x.Id != exclude && x.DeviceId == deviceId && x.Name == tag.Name, cancellationToken)
            : await db.Tags.AnyAsync(x => x.DeviceId == deviceId && x.Name == tag.Name, cancellationToken);
        return duplicate
            ? (TagCommandError.DuplicateName,
                [new ValidationIssue("tag.name.duplicate", nameof(Tag.Name), DuplicateNameMessage)])
            : (TagCommandError.None, []);
    }

    /// <summary>兼容入口：MCP 与工程导入仍只消费首条文案。</summary>
    public async Task<(TagCommandError Error, string Message)> InspectAsync(
        ConfigDbContext db, Tag tag, Device device, int? excludeTagId, CancellationToken cancellationToken)
    {
        var (error, issues) = await InspectIssuesAsync(db, tag, device, excludeTagId, cancellationToken);
        return (error, issues.Count > 0 ? issues[0].Message : string.Empty);
    }

    // 档位数量是设备级约束：超限时快慢点混跑会退化成相互打断的密集轮询。
    // 与采集器一致，按有效周期计档：未填周期的物理测点跟随设备默认，也占一档。
    public async Task<string?> ValidateLaneBudgetAsync(
        ConfigDbContext db, Device device, IEnumerable<int?> candidates, int? excludeTagId, CancellationToken cancellationToken)
    {
        var query = db.Tags.AsNoTracking().Where(x => x.DeviceId == device.Id && x.Kind == TagKind.Physical);
        if (excludeTagId is int exclude) query = query.Where(x => x.Id != exclude);
        var existing = await query.Select(x => x.ScanRateMs).ToListAsync(cancellationToken);
        return ScanCadence.ValidateLaneBudget(device, existing.Concat(candidates));
    }

    // 审计由 AuditInterceptor 在保存后自动落（Tag 实现了 IAuditedEntity），
    // 这里只剩「通知运行时重载」：漏掉重载的后果是配置改了但采集器还按旧点表跑。
    private Task ReloadAsync(CancellationToken cancellationToken) => reloader.ReloadAsync(cancellationToken);

    private static string CategoryLabel(string category) => category switch
    {
        "screenDraft" => "画面草稿",
        "screenPublished" => "已发布画面",
        "template" => "图库模板",
        "alarmRule" => "报警规则",
        "sequence" => "顺控流程",
        "recipe" => "配方模板",
        "calculatedTag" => "计算测点",
        _ => category
    };
}

public enum TagCommandError
{
    None = 0,
    Validation,
    LaneBudgetExceeded,
    DuplicateName,
    NotPhysical,
    Referenced,
    Conflict
}

public sealed record TagCommandResult(
    bool Success, TagCommandError Error, string Message, Tag? Tag,
    IReadOnlyList<ValidationIssue> Issues)
{
    public static TagCommandResult Succeeded(Tag tag) => new(true, TagCommandError.None, string.Empty, tag, []);

    public static TagCommandResult Failed(TagCommandError error, IReadOnlyList<ValidationIssue> issues) =>
        new(false, error, issues.Count > 0 ? issues[0].Message : string.Empty, null, issues);

    public static TagCommandResult Failed(TagCommandError error, string message) =>
        Failed(error, [new ValidationIssue(CodeFor(error), null, message)]);

    private static string CodeFor(TagCommandError error) => error switch
    {
        TagCommandError.Validation => "tag.validation",
        TagCommandError.LaneBudgetExceeded => "tag.scanRate.laneBudget",
        TagCommandError.DuplicateName => "tag.name.duplicate",
        TagCommandError.NotPhysical => "tag.kind.notPhysical",
        TagCommandError.Referenced => "tag.referenced",
        TagCommandError.Conflict => "tag.conflict",
        _ => "tag.request"
    };
}
