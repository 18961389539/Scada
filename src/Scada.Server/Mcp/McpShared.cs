using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;

namespace Scada.Server.Mcp;

// MCP 各工具类共用的入参校验与文本规整。工具方法按领域拆到 Mcp*Tools 类后，
// 这些纯函数保持一处实现，避免每个类各抄一份导致边界值逐渐走偏。
internal static class McpArgs
{
    internal static int[] ValidateIds(int[]? ids, int max)
    {
        if (ids is null || ids.Length == 0 || ids.Length > max || ids.Any(id => id <= 0))
            throw new McpToolException(McpErrorCodes.InvalidArgument, $"tagIds must contain 1 to {max} positive IDs.");
        return ids.Distinct().ToArray();
    }

    internal static void ValidatePage(int start, int limit)
    {
        if (start < 0 || limit is < 1 or > 100)
            throw new McpToolException(McpErrorCodes.InvalidArgument,
                "Paging requires a non-negative cursor/offset and a limit between 1 and 100.");
    }

    internal static void ValidateRange(long from, long to)
    {
        if (from < 0 || to > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() || to < from
            || to - from > (long)TimeSpan.FromDays(31).TotalMilliseconds)
            throw new McpToolException(McpErrorCodes.InvalidArgument,
                "Time range must use non-negative Unix milliseconds, be ordered, and not exceed 31 days.");
    }

    internal static void ValidateWindow(long from, long to, int days = 31)
    {
        ValidateRange(from, to);
        if (from == to || to - from > (long)TimeSpan.FromDays(days).TotalMilliseconds
            || to > DateTimeOffset.MaxValue.AddDays(-2).ToUnixTimeMilliseconds())
            throw new McpToolException(McpErrorCodes.InvalidArgument,
                $"Time window must be non-empty, within the supported calendar, and no longer than {days} days.");
    }

    internal static async Task RequireTagsAsync(this IDbContextFactory<ConfigDbContext> factory, int[] ids, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Tags.CountAsync(x => ids.Contains(x.Id), ct) != ids.Length)
            throw new McpToolException(McpErrorCodes.NotFound, "One or more requested tags do not exist.");
    }
}

internal static class McpText
{
    internal static string RequiredText(string? value, int max, string name)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) throw new McpToolException(McpErrorCodes.InvalidArgument, $"{name} is required.");
        if (text.Length > max) throw new McpToolException(McpErrorCodes.InvalidArgument, $"{name} must not exceed {max} characters.");
        return text;
    }

    internal static string? OptionalText(string? value, int max, string name)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text?.Length > max) throw new McpToolException(McpErrorCodes.InvalidArgument, $"{name} must not exceed {max} characters.");
        return text;
    }
}

// MCP 没有登录用户，所有写操作以固定身份执行；审计与领域记录都用这一个名字。
internal static class McpActors
{
    internal const string McpActorName = "mcp:lan";
    internal static readonly ControlActor McpActor = new(null, McpActorName);
}

internal static class McpAuditLog
{
    internal static OperationLog Audit(string action, string targetType, int targetId, string result,
        string? oldValue, string? newValue, long ts) => new()
    {
        UserName = McpActors.McpActorName, Ts = ts, Action = action, TargetType = targetType,
        TargetId = targetId.ToString(System.Globalization.CultureInfo.InvariantCulture), Result = result,
        OldValue = oldValue, NewValue = newValue
    };
}

// 区域目录同时服务 list_areas 工具与 scada://areas 资源，避免两处计数口径分叉。
internal static class McpAreaCatalog
{
    internal static async Task<McpAreaList> ListAsync(IDbContextFactory<ConfigDbContext> factory, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var areas = await db.Areas.AsNoTracking().OrderBy(x => x.Id).Take(101).ToListAsync(cancellationToken);
        var deviceCounts = await db.Devices.AsNoTracking().GroupBy(x => x.AreaId)
            .Select(g => new { AreaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AreaId, x => x.Count, cancellationToken);
        var physicalCounts = await (from t in db.Tags.AsNoTracking()
                                    join d in db.Devices.AsNoTracking() on t.DeviceId equals (int?)d.Id
                                    where t.Kind == TagKind.Physical
                                    group d by d.AreaId into g
                                    select new { AreaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AreaId, x => x.Count, cancellationToken);
        var explicitCounts = await db.Tags.AsNoTracking()
            .Where(x => x.Kind != TagKind.Physical && x.AreaId != null)
            .GroupBy(x => x.AreaId!.Value)
            .Select(g => new { AreaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AreaId, x => x.Count, cancellationToken);
        var items = areas.Take(100).Select(x => new McpArea(x.Id, x.Name, x.Description,
            deviceCounts.GetValueOrDefault(x.Id),
            physicalCounts.GetValueOrDefault(x.Id) + explicitCounts.GetValueOrDefault(x.Id),
            x.Id == Area.DefaultId)).ToArray();
        return new(items, areas.Count > 100, ScadaTime.NowMs());
    }
}

// 跨工具类共享的返回结构：同一份数据在不同工具里必须保持同样的字段名。
public sealed record McpCursorPage<T>(IReadOnlyList<T> Items, int? NextAfterId, long DataAsOf, bool Truncated);

public sealed record McpTag(int Id, string Name, int? DeviceId, int? AreaId, string Kind, string DataType,
    string? Unit, bool Writable, string? Description);

public sealed record McpDeviceRow(int Id, string Name, bool Enabled, CollectorDeviceStatus? Status);

public sealed record McpArea(int Id, string Name, string? Description, int DeviceCount, int TagCount, bool IsDefault);

public sealed record McpAreaList(IReadOnlyList<McpArea> Items, bool Truncated, long DataAsOf);
