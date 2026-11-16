using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Services;

/// <summary>
/// 区域主数据的唯一落地点：REST 与 MCP 共用名称校验、重名拒绝、默认区域保护与引用检查，
/// 并在每次变更后使访问模型失效。授权由各入口负责。
/// </summary>
public enum AreaCommandError
{
    None,
    InvalidName,
    DuplicateName,
    NotFound,
    DefaultArea,
    HasDevices,
    HasCalculatedTags,
    HasAssignments
}

public sealed class AreaCommandResult
{
    public bool Success { get; init; }
    public AreaCommandError Error { get; init; }
    public string? Message { get; init; }
    public Area? Area { get; init; }

    public static AreaCommandResult Failed(AreaCommandError error, string message, Area? area = null) =>
        new() { Error = error, Message = message, Area = area };

    public static AreaCommandResult Ok(Area area) => new() { Success = true, Area = area };
}

public sealed class AreaCommandService(AccessModelProvider accessModels)
{
    public const string InvalidNameMessage = "区域名称须为 1～100 个字符";
    public const string DuplicateNameMessage = "区域名称已存在";
    public const string DefaultAreaMessage = "默认区域不能删除";
    public const string HasDevicesMessage = "区域内仍有设备，请先移走设备";
    public const string HasCalculatedTagsMessage = "仍有计算测点指定了该区域";
    public const string HasAssignmentsMessage = "仍有用户授权限定在该区域，请先调整授权";

    public async Task<AreaCommandResult> CreateAsync(ConfigDbContext db, string? name, string? description, CancellationToken cancellationToken)
    {
        var clean = NormalizeName(name, out var invalid);
        if (invalid) return AreaCommandResult.Failed(AreaCommandError.InvalidName, InvalidNameMessage);
        var note = Clean(description);
        if (await db.Areas.AnyAsync(x => x.Name == clean, cancellationToken))
            return AreaCommandResult.Failed(AreaCommandError.DuplicateName, DuplicateNameMessage);
        var area = new Area { Name = clean, Description = note };
        db.Areas.Add(area);
        await db.SaveChangesAsync(cancellationToken);
        accessModels.Invalidate();
        return AreaCommandResult.Ok(area);
    }

    public async Task<AreaCommandResult> UpdateAsync(
        ConfigDbContext db, int areaId, string? name, bool updateName, string? description, bool updateDescription,
        CancellationToken cancellationToken)
    {
        var area = await db.Areas.FirstOrDefaultAsync(x => x.Id == areaId, cancellationToken);
        if (area is null) return AreaCommandResult.Failed(AreaCommandError.NotFound, "Area does not exist.");
        string clean;
        if (updateName)
        {
            clean = NormalizeName(name, out var invalid);
            if (invalid) return AreaCommandResult.Failed(AreaCommandError.InvalidName, InvalidNameMessage, area);
        }
        else clean = area.Name;
        var note = updateDescription ? Clean(description) : area.Description;
        if (await db.Areas.AnyAsync(x => x.Id != areaId && x.Name == clean, cancellationToken))
            return AreaCommandResult.Failed(AreaCommandError.DuplicateName, DuplicateNameMessage, area);
        area.Name = clean;
        area.Description = note;
        await db.SaveChangesAsync(cancellationToken);
        accessModels.Invalidate();
        return AreaCommandResult.Ok(area);
    }

    public async Task<AreaCommandResult> DeleteAsync(ConfigDbContext db, int areaId, CancellationToken cancellationToken)
    {
        if (areaId == Area.DefaultId) return AreaCommandResult.Failed(AreaCommandError.DefaultArea, DefaultAreaMessage);
        var area = await db.Areas.FirstOrDefaultAsync(x => x.Id == areaId, cancellationToken);
        if (area is null) return AreaCommandResult.Failed(AreaCommandError.NotFound, "Area does not exist.");
        if (await db.Devices.AnyAsync(x => x.AreaId == areaId, cancellationToken))
            return AreaCommandResult.Failed(AreaCommandError.HasDevices, HasDevicesMessage, area);
        if (await db.Tags.AnyAsync(x => x.AreaId == areaId, cancellationToken))
            return AreaCommandResult.Failed(AreaCommandError.HasCalculatedTags, HasCalculatedTagsMessage, area);
        if (await db.UserRoleAssignments.AnyAsync(x => x.AreaId == areaId, cancellationToken))
            return AreaCommandResult.Failed(AreaCommandError.HasAssignments, HasAssignmentsMessage, area);
        db.Areas.Remove(area);
        await db.SaveChangesAsync(cancellationToken);
        accessModels.Invalidate();
        return AreaCommandResult.Ok(area);
    }

    private static string NormalizeName(string? name, out bool invalid)
    {
        var clean = name?.Trim() ?? "";
        invalid = clean.Length is 0 or > 100;
        return clean;
    }

    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, 500)];
    }
}
