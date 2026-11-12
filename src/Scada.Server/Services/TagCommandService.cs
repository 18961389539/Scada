using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Services;

/// <summary>
/// 测点写入的唯一落地点：REST 端点与 MCP 工具共用同一套顺序与判据。
/// Excel 导入不走这里——它按名字合并已有测点（upsert），不存在「拒绝重名」这个动作。
///
/// 之前「协议校验 / 采集档位预算 / 同设备重名」在三处各写一遍，且端点先查重名再校验、
/// MCP 先校验再查重名——同一条非法请求在两个入口给出不同提示。规则只有一份之后，
/// 新增约束不会出现「界面拦住了、MCP 还能写进去」的缺口。
///
/// 授权与返回形态不属于本服务：区域权限、HTTP 状态码、MCP 的 success=false / 异常
/// 由各入口自己决定（两者鉴权模型不同）。
/// </summary>
public sealed class TagCommandService(ConfigReloader reloader)
{
    public const string DuplicateNameMessage = "同一设备下测点名称已存在";

    public async Task<TagCommandResult> CreateAsync(
        ConfigDbContext db, Tag tag, Device device, CancellationToken cancellationToken)
    {
        var (error, message) = await InspectAsync(db, tag, device, null, cancellationToken);
        if (error != TagCommandError.None) return TagCommandResult.Failed(error, message);

        db.Tags.Add(tag);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TagCommandResult.Failed(TagCommandError.DuplicateName, DuplicateNameMessage);
        }

        await ReloadAsync(cancellationToken);
        return TagCommandResult.Succeeded(tag);
    }

    /// <summary>按 <paramref name="updated"/> 覆盖可编辑字段；<c>Id</c>/<c>Kind</c> 与计算字段不参与合并。</summary>
    public async Task<TagCommandResult> UpdateAsync(
        ConfigDbContext db, Tag tag, Tag updated, Device device, CancellationToken cancellationToken)
    {
        if (tag.Kind != TagKind.Physical)
            return TagCommandResult.Failed(TagCommandError.NotPhysical, "请通过计算测点或计算任务配置修改此测点");

        var (error, message) = await InspectAsync(db, updated, device, tag.Id, cancellationToken);
        if (error != TagCommandError.None) return TagCommandResult.Failed(error, message);

        var before = tag.Clone();
        tag.ApplyEditableFrom(updated);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TagCommandResult.Failed(TagCommandError.DuplicateName, DuplicateNameMessage);
        }

        await ReloadAsync(cancellationToken);
        return TagCommandResult.Succeeded(tag);
    }

    public async Task<TagCommandResult> DeleteAsync(
        ConfigDbContext db, Tag tag, CancellationToken cancellationToken)
    {
        if (tag.Kind != TagKind.Physical)
            return TagCommandResult.Failed(TagCommandError.NotPhysical, "请通过计算测点或计算任务配置删除此测点");

        if (await db.TagDependencies.AnyAsync(x => x.InputTagId == tag.Id, cancellationToken))
            return TagCommandResult.Failed(TagCommandError.Referenced, "测点仍被计算定义引用，请先修改依赖定义");

        db.Tags.Remove(tag);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TagCommandResult.Failed(TagCommandError.Conflict, "测点引用已变化，请重新读取后修改依赖定义");
        }

        await ReloadAsync(cancellationToken);
        return TagCommandResult.Succeeded(tag);
    }

    /// <summary>
    /// 校验顺序固定：协议与字段 → 采集档位预算 → 同设备重名。批量建点复用同一判据，
    /// 只是它先按名字数组做一次批量查询，避免 N 次重名查询。
    /// </summary>
    public async Task<(TagCommandError Error, string Message)> InspectAsync(
        ConfigDbContext db, Tag tag, Device device, int? excludeTagId, CancellationToken cancellationToken)
    {
        if (ConfigurationValidator.Tag(tag, device) is string invalid)
            return (TagCommandError.Validation, invalid);

        // 与 ConfigurationValidator.Tag(tag, device) 用同一个设备：调用方保证 tag.DeviceId 就是 device.Id，
        // 这里显式取 device.Id，避免两者不一致时档位预算和重名查到别的设备上。
        var deviceId = device.Id;
        if (await ValidateLaneBudgetAsync(db, deviceId, [tag.ScanRateMs], excludeTagId, cancellationToken) is string laneError)
            return (TagCommandError.LaneBudgetExceeded, laneError);

        var duplicate = excludeTagId is int exclude
            ? await db.Tags.AnyAsync(x => x.Id != exclude && x.DeviceId == deviceId && x.Name == tag.Name, cancellationToken)
            : await db.Tags.AnyAsync(x => x.DeviceId == deviceId && x.Name == tag.Name, cancellationToken);
        return duplicate
            ? (TagCommandError.DuplicateName, DuplicateNameMessage)
            : (TagCommandError.None, string.Empty);
    }

    // 档位数量是设备级约束：超限时快慢点混跑会退化成相互打断的密集轮询。
    public async Task<string?> ValidateLaneBudgetAsync(
        ConfigDbContext db, int deviceId, IEnumerable<int?> candidates, int? excludeTagId, CancellationToken cancellationToken)
    {
        var query = db.Tags.AsNoTracking().Where(x => x.DeviceId == deviceId && x.ScanRateMs != null);
        if (excludeTagId is int exclude) query = query.Where(x => x.Id != exclude);
        var existing = await query.Select(x => x.ScanRateMs).ToListAsync(cancellationToken);
        return ScanCadence.ValidateLaneBudget(existing.Concat(candidates));
    }

    // 审计由 AuditInterceptor 在保存后自动落（Tag 实现了 IAuditedEntity），
    // 这里只剩「通知运行时重载」：漏掉重载的后果是配置改了但采集器还按旧点表跑。
    private Task ReloadAsync(CancellationToken cancellationToken) => reloader.ReloadAsync(cancellationToken);
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

public sealed record TagCommandResult(bool Success, TagCommandError Error, string Message, Tag? Tag)
{
    public static TagCommandResult Succeeded(Tag tag) => new(true, TagCommandError.None, string.Empty, tag);
    public static TagCommandResult Failed(TagCommandError error, string message) => new(false, error, message, null);
}
