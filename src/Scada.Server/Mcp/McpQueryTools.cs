using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.History;
using Scada.Server.Endpoints;
using Scada.Server.Services;
using static Scada.Server.Mcp.McpArgs;

namespace Scada.Server.Mcp;

// 只读查询：测点检索、实时值、历史、告警列表。全部跨区域可读（MCP 无登录用户），
// 但显式传入的测点/设备必须存在，宁可按 not_found 失败也不返回空结果。
[McpServerToolType]
public sealed class McpQueryTools(
    AccessModelProvider accessModels,
    IDbContextFactory<ConfigDbContext> factory,
    IRealtimeCache cache,
    ICollectorControl collector,
    HistoryQueryService history,
    AlarmService alarms)
{
    [McpServerTool(Name = "search_tags", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search SCADA tags across all areas by name/description, device, area, data type, unit or writability. areaId matches physical tags by their device's area and other tags by their explicit area. Returns at most 100 tags with areaId and writable, plus nextAfterId for paging. Does not read the field device.")]
    public async Task<McpCursorPage<McpTag>> SearchTags(
        CancellationToken cancellationToken,
        [Description("Optional name or description substring, at most 100 characters.")] string? keyword = null,
        [Description("Last tag ID from the previous page; start with 0.")] int afterId = 0,
        [Description("Page size, 1 to 100.")] int limit = 50,
        [Description("Optional device ID to restrict the search.")] int? deviceId = null,
        [Description("Optional area ID. Matches physical tags by device area and other tags by explicit area.")] int? areaId = null,
        [Description("Optional tag data type, for example Float, Int32 or Bool.")] TagDataType? dataType = null,
        [Description("Optional exact unit, up to 50 characters.")] string? unit = null,
        [Description("Optional writability flag from the tag configuration.")] bool? writable = null)
    {
        ValidatePage(afterId, limit);
        if (keyword?.Length > 100) throw new McpToolException(McpErrorCodes.InvalidArgument, "keyword must not exceed 100 characters.");
        if (unit is { Length: > 50 }) throw new McpToolException(McpErrorCodes.InvalidArgument, "unit must not exceed 50 characters.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Tags.AsNoTracking().Where(x => x.Id > afterId);
        if (deviceId is not null)
        {
            if (deviceId <= 0 || !await db.Devices.AnyAsync(x => x.Id == deviceId, cancellationToken))
                throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
            query = query.Where(x => x.DeviceId == deviceId);
        }
        if (areaId is int area)
        {
            if (area <= 0 || !await db.Areas.AnyAsync(x => x.Id == area, cancellationToken))
                throw new McpToolException(McpErrorCodes.NotFound, "Area does not exist.");
            query = query.Where(x =>
                (x.Kind == TagKind.Physical && db.Devices.Any(d => d.Id == x.DeviceId && d.AreaId == area))
                || (x.Kind != TagKind.Physical && x.AreaId == area));
        }
        if (dataType is not null) query = query.Where(x => x.DataType == dataType);
        if (unit is not null)
        {
            var exact = unit.Trim();
            query = query.Where(x => x.Unit == exact);
        }
        if (writable is not null) query = query.Where(x => x.Writable == writable);
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.Name.Contains(keyword) || (x.Description != null && x.Description.Contains(keyword)));
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1)
            .Select(x => new { x.Id, x.Name, x.DeviceId, x.Kind, x.DataType, x.Unit, x.Writable, x.Description, x.AreaId })
            .ToListAsync(cancellationToken);
        // 物理测点的区域来自设备，按页一次查出，避免每行相关子查询。
        var pageDeviceIds = rows.Where(x => x.Kind == TagKind.Physical && x.DeviceId is not null)
            .Select(x => x.DeviceId!.Value).Distinct().ToArray();
        var deviceAreas = pageDeviceIds.Length == 0
            ? new Dictionary<int, int>()
            : await db.Devices.AsNoTracking().Where(x => pageDeviceIds.Contains(x.Id))
                .Select(x => new { x.Id, x.AreaId }).ToDictionaryAsync(x => x.Id, x => x.AreaId, cancellationToken);
        return new(rows.Take(limit).Select(x => new McpTag(x.Id, x.Name, x.DeviceId,
                x.Kind == TagKind.Physical
                    ? (x.DeviceId is int id && deviceAreas.TryGetValue(id, out var area) ? area : (int?)null)
                    : x.AreaId,
                x.Kind.ToString(), x.DataType.ToString(), x.Unit, x.Writable, x.Description)).ToArray(),
            rows.Count > limit ? rows[limit - 1].Id : null, ScadaTime.NowMs(), false);
    }

    [McpServerTool(Name = "get_device_status", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List configured devices across all areas and cached collector status, at most 100 per page. A null status means no collector status is available, not that the device is online. No reconnects or probes are performed.")]
    public async Task<McpCursorPage<McpDeviceRow>> GetDeviceStatus(
        CancellationToken cancellationToken,
        [Description("Last device ID from the previous page; start with 0.")] int afterId = 0,
        [Description("Page size, 1 to 100.")] int limit = 50)
    {
        ValidatePage(afterId, limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Devices.AsNoTracking().Where(x => x.Id > afterId);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1)
            .Select(x => new { x.Id, x.Name, x.Enabled }).ToListAsync(cancellationToken);
        var statuses = collector.GetStatuses().ToDictionary(x => x.DeviceId);
        return new(rows.Take(limit).Select(x => new McpDeviceRow(x.Id, x.Name, x.Enabled, statuses.GetValueOrDefault(x.Id))).ToArray(),
            rows.Count > limit ? rows[limit - 1].Id : null, ScadaTime.NowMs(), false);
    }

    [McpServerTool(Name = "get_realtime", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read cached values for 1 to 100 configured tags across all areas. Returns quality and Unix millisecond timestamps; missingTagIds have no cached value and must not be interpreted as zero. Never triggers device I/O.")]
    public async Task<McpRealtimeResult> GetRealtime(
        [Description("Explicit positive tag IDs, at most 100. All must exist.")] int[] tagIds,
        CancellationToken cancellationToken)
    {
        var ids = ValidateIds(tagIds, 100);
        await factory.RequireTagsAsync(ids, cancellationToken);
        var values = cache.GetMany(ids);
        var present = values.Select(x => x.TagId).ToHashSet();
        return new(values.Select(x => new McpValue(x.TagId, x.Value, x.Quality.ToString(), x.Ts, x.SourceTs, x.Error)).ToArray(),
            ids.Where(id => !present.Contains(id)).ToArray(), ScadaTime.NowMs(), false);
    }

    [McpServerTool(Name = "query_history", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query history for 1 to 10 configured tags across all areas, up to 31 days. Returns bounded, possibly downsampled series. Timestamps are Unix milliseconds. Existing history concurrency, timeout and read budgets apply.")]
    public async Task<McpHistoryResult> QueryHistory(
        [Description("Explicit positive tag IDs, at most 10. All must exist.")] int[] tagIds,
        [Description("Inclusive start time in Unix milliseconds (UTC), non-negative.")] long from,
        [Description("Inclusive end time in Unix milliseconds (UTC), no more than 31 days after from.")] long to,
        CancellationToken cancellationToken,
        [Description("Total point budget across all tags, 20 to 1000.")] int maxPoints = 500)
    {
        var ids = ValidateIds(tagIds, 10);
        ValidateRange(from, to);
        if (maxPoints is < 20 or > 1000)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "maxPoints must be between 20 and 1000.");
        await factory.RequireTagsAsync(ids, cancellationToken);
        try
        {
            var result = await history.QueryAsync(ids, from, to, cancellationToken, maxPoints);
            return new(result.Resolution.ToString(), result.Downsampled, result.PointLimit,
                result.Series.Select(s => new McpHistorySeries(s.TagId,
                    s.Points.Select(p => new McpHistoryPoint(p.Ts, p.Value, p.Quality.ToString())).ToArray())).ToArray(),
                ScadaTime.NowMs(), false);
        }
        catch (HistoryQueryException ex)
        {
            throw McpErrors.Map(ex);
        }
    }

    [McpServerTool(Name = "query_alarms", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query alarms across all areas with paging, at most 100 per page. activeOnly=true includes all currently active alarms regardless of age. For history set activeOnly=false and supply a time range of at most 31 days. No acknowledgement or shelving is performed.")]
    public async Task<McpAlarmPage> QueryAlarms(
        CancellationToken cancellationToken,
        [Description("True for currently active alarms; false for alarm history.")] bool activeOnly = true,
        [Description("History start in Unix milliseconds (UTC); omit for active alarms.")] long? from = null,
        [Description("History end in Unix milliseconds (UTC); omit for active alarms.")] long? to = null,
        [Description("Number of rows to skip, 0 to 10000.")] int offset = 0,
        [Description("Page size, 1 to 100.")] int limit = 50)
    {
        ValidatePage(offset, limit);
        if (offset > 10000) throw new McpToolException(McpErrorCodes.InvalidArgument, "offset must not exceed 10000; narrow the time range.");
        if (activeOnly && (from is not null || to is not null))
            throw new McpToolException(McpErrorCodes.InvalidArgument, "Omit from and to when activeOnly is true.");
        if (!activeOnly)
        {
            if (from is null || to is null) throw new McpToolException(McpErrorCodes.InvalidArgument, "Alarm history requires from and to.");
            ValidateRange(from.Value, to.Value);
        }
        var access = new AccessContext(null, [new AccessGrant(Permission.View, null)], await accessModels.GetAsync(cancellationToken));
        var rows = await alarms.QueryAsync(from, to, activeOnly, limit + 1, cancellationToken, skip: offset);
        var capped = rows.Count > limit && offset + limit > 10000;
        return new(rows.Take(limit).Select(x => AlarmEndpoints.View(x, access)).ToArray(),
            rows.Count > limit && !capped ? offset + limit : null, capped, ScadaTime.NowMs());
    }

    public sealed record McpValue(int TagId, double Value, string Quality, long Ts, long? SourceTs, string? Error);
    public sealed record McpRealtimeResult(IReadOnlyList<McpValue> Values, int[] MissingTagIds, long DataAsOf, bool Truncated);
    public sealed record McpHistoryPoint(long Ts, double Value, string Quality);
    public sealed record McpHistorySeries(int TagId, IReadOnlyList<McpHistoryPoint> Points);
    public sealed record McpHistoryResult(string Resolution, bool Downsampled, int PointLimit, IReadOnlyList<McpHistorySeries> Series,
        long DataAsOf, bool Truncated);
    public sealed record McpAlarmPage(IReadOnlyList<object> Items, int? NextOffset, bool Truncated, long DataAsOf);
}
