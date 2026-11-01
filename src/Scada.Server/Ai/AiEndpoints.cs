using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.History;
using Scada.Engine.Reports;
using Scada.Server.Auth;
using Scada.Server.Endpoints;

namespace Scada.Server.Ai;

/// <summary>
/// 面向大模型的只读 API（/api/ai/*）。
///
/// 与 MCP 的关系：两者是同一份数据的两种暴露方式。MCP 面向支持 MCP 协议的客户端；
/// 这里走普通 HTTP + OpenAPI，适合模型直接用 Function Calling 或任意 HTTP 客户端调用。
/// 两者共用同一套权限模型与数据来源，行为保持一致。
///
/// 安全约束（与本文件的设计前提一一对应）：
/// 1. 本组只注册 GET，不存在任何写入端点——模型无法通过本组改变系统状态。
/// 2. 每个响应都按调用者的区域权限过滤，越权对象直接不返回，不泄露存在性。
/// 3. 时间跨度、点数、条数有硬上限，避免大模型一次拉爆上下文或拖垮服务端。
/// 4. 全部响应带 dataAsOf 时间戳，让模型知道数据的时效性。
/// </summary>
public static class AiEndpoints
{
    public const string Policy = "AiRead";

    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value;
        if (!options.Enabled) return;

        // 独立路由组：与内部 /api/* 隔离，避免前端误用；鉴权走 External API 策略（API 密钥或 JWT）。
        var group = app.MapGroup("/api/ai")
            .RequireAuthorization(Policy)
            .AddEndpointFilter<AiReadOnlyGuard>()
            .AddEndpointFilter<AiRateLimitFilter>()
            .AddEndpointFilter<AiAuditFilter>()
            .WithTags("AI Read API");

        // 工具清单本身不含现场数据，允许匿名获取：模型接入方先读清单再决定要哪把密钥。
        group.MapGet("/tools", () => Results.Ok(AiToolManifest.Build(options)))
            .WithSummary("列出可供大模型调用的只读工具定义")
            .WithDescription("Returns OpenAI-style function definitions for every read-only endpoint in this group. Use them directly as Function Calling tools.")
            .AllowAnonymous();

        group.MapGet("/devices", async (
            int? areaId,
            bool? online,
            string? keyword,
            int? limit,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory,
            ICollectorControl collector,
            IConfigSnapshot config,
            CancellationToken ct) =>
        {
            if (await AiScopeGuard.RequireAsync(http, ExternalApiScope.ReadTags) is { } scopeDenied) return scopeDenied;
            var access = await http.GetAccessAsync();
            var take = Math.Clamp(limit ?? 100, 1, 500);
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.Devices.AsNoTracking().AsQueryable();
            if (areaId is int area) query = query.Where(x => x.AreaId == area);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                query = query.Where(x => x.Name.Contains(kw));
            }

            var rows = await query.OrderBy(x => x.Id).Take(take)
                .Select(x => new { x.Id, x.Name, x.AreaId, x.Enabled, x.Protocol, x.Host, x.Port })
                .ToListAsync(ct);

            var statuses = collector.GetStatuses().ToDictionary(x => x.DeviceId);
            var areas = config.Areas.ToDictionary(x => x.Id, x => x.Name);
            var visible = rows
                .Where(x => access.CanDevice(Permission.View, x.Id))
                .Select(x =>
                {
                    statuses.TryGetValue(x.Id, out var st);
                    var isOnline = st?.Online ?? false;
                    return new
                    {
                        id = x.Id,
                        name = x.Name,
                        areaId = x.AreaId,
                        areaName = areas.GetValueOrDefault(x.AreaId),
                        enabled = x.Enabled,
                        protocol = x.Protocol.ToString(),
                        endpoint = $"{x.Host}:{x.Port}",
                        // null 表示采集器没有上报状态，既不等于在线也不等于离线。
                        online = st is null ? (bool?)null : isOnline,
                        lastPollTs = st?.LastPollTs,
                        lastCycleMs = st?.LastCycleMs,
                        tagCount = st?.TagCount,
                        commFailures = st?.CommFailures,
                        reconnectAttempts = st?.ReconnectAttempts,
                        error = st?.Error,
                        hasStatus = st is not null
                    };
                })
                .Where(x => online is null || x.online == online)
                .ToList();

            return Results.Ok(new { items = visible, count = visible.Count, dataAsOf = ScadaTime.NowMs() });
        })
            .WithSummary("查询设备状态列表")
            .WithDescription("Lists configured devices with cached collector status (online, last poll time, cycle time, communication failures). Devices outside the caller's areas are omitted entirely. A null online value means no collector status is available, not that the device is offline.")
            .Produces<AiDevicesResponse>();

        group.MapGet("/realtime", async (
            string? tagIds,
            string? names,
            int? deviceId,
            HttpContext http,
            IRealtimeCache cache,
            IConfigSnapshot config,
            CancellationToken ct) =>
        {
            if (await AiScopeGuard.RequireAsync(http, ExternalApiScope.ReadTags) is { } scopeDenied) return scopeDenied;
            var access = await http.GetAccessAsync();
            var ids = ResolveTagIds(tagIds, names, deviceId, config);

            if (ids.Length == 0)
            {
                return Results.Ok(new { items = Array.Empty<object>(), count = 0, dataAsOf = ScadaTime.NowMs() });
            }

            if (ids.Length > 200)
            {
                return EndpointHelpers.BadRequest("单次最多查询 200 个测点");
            }

            // 显式指定的测点必须全部可见：宁可整体拒绝，也不返回部分后再让模型误判。
            var denied = ids.Where(id => !access.CanTag(Permission.View, id)).ToArray();
            if (denied.Length > 0)
            {
                return EndpointHelpers.BadRequest($"无权访问测点：{string.Join(",", denied.Take(20))}");
            }

            var values = cache.GetMany(ids);
            var present = values.Select(x => x.TagId).ToHashSet();
            var items = ids.Select(id =>
            {
                config.TryGetTag(id, out var tag);
                var hit = values.FirstOrDefault(x => x.TagId == id);
                return new
                {
                    tagId = id,
                    name = tag?.Name ?? $"#{id}",
                    unit = tag?.Unit,
                    description = tag?.Description,
                    writable = tag?.Writable ?? false,
                    // 缓存里没有这个测点时不能返回 0，必须显式标记缺失。
                    hasValue = hit is not null,
                    value = hit?.Value,
                    quality = hit?.Quality.ToString(),
                    ts = hit?.Ts,
                    sourceTs = hit?.SourceTs,
                    error = hit?.Error
                };
            }).ToList();

            return Results.Ok(new
            {
                items,
                count = items.Count,
                missingTagIds = ids.Where(id => !present.Contains(id)).ToArray(),
                dataAsOf = ScadaTime.NowMs()
            });
        })
            .WithSummary("读取测点当前值")
            .WithDescription("Reads cached current values for up to 200 tags selected by tagIds, names or deviceId. Returns quality and timestamps. Tags without a cached value are listed in missingTagIds and must not be read as zero. Never triggers device I/O.")
            .Produces<AiRealtimeResponse>();

        group.MapGet("/trends", async (
            string? tagIds,
            string? names,
            string? from,
            string? to,
            int? maxPoints,
            HttpContext http,
            HistoryQueryService history,
            IConfigSnapshot config,
            CancellationToken ct) =>
        {
            if (await AiScopeGuard.RequireAsync(http, ExternalApiScope.ReadHistory) is { } scopeDenied) return scopeDenied;
            var access = await http.GetAccessAsync();
            var ids = ResolveTagIds(tagIds, names, null, config);
            if (ids.Length == 0) return EndpointHelpers.BadRequest("请指定 tagIds 或 names");
            if (ids.Length > 10) return EndpointHelpers.BadRequest("单次最多查询 10 个测点");

            var denied = ids.Where(id => !access.CanTag(Permission.View, id)).ToArray();
            if (denied.Length > 0)
            {
                return EndpointHelpers.BadRequest($"无权访问测点：{string.Join(",", denied.Take(20))}");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var toMs = ParseTime(to, now);
            var fromMs = ParseTime(from, toMs - (long)TimeSpan.FromHours(24).TotalMilliseconds);
            if (toMs < fromMs) return EndpointHelpers.BadRequest("结束时间不能早于开始时间");

            var maxRangeDays = Math.Clamp(app.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value.MaxRangeDays, 1, 366);
            if (toMs - fromMs > (long)TimeSpan.FromDays(maxRangeDays).TotalMilliseconds)
            {
                return EndpointHelpers.BadRequest($"查询跨度不能超过 {maxRangeDays} 天");
            }

            var limit = Math.Clamp(maxPoints ?? 500, 20, 1000);
            try
            {
                var result = await history.QueryAsync(ids, fromMs, toMs, ct, limit);
                var series = result.Series.Select(s =>
                {
                    config.TryGetTag(s.TagId, out var tag);
                    return new
                    {
                        tagId = s.TagId,
                        name = tag?.Name ?? $"#{s.TagId}",
                        unit = tag?.Unit,
                        points = s.Points.Select(p => new { ts = p.Ts, value = p.Value, quality = p.Quality.ToString() }).ToArray()
                    };
                }).ToList();
                return Results.Ok(new
                {
                    series,
                    resolution = result.Resolution.ToString(),
                    downsampled = result.Downsampled,
                    pointLimit = result.PointLimit,
                    from = fromMs,
                    to = toMs,
                    dataAsOf = ScadaTime.NowMs()
                });
            }
            catch (HistoryQueryException ex)
            {
                return Results.Json(new { message = ex.Message, code = ex.Code.ToString() }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        })
            .WithSummary("查询历史趋势")
            .WithDescription("Queries history for 1 to 10 tags over a bounded window (default 24 hours, max 31 days). Returns possibly downsampled series with Unix millisecond timestamps and a dataAsOf marker. Exceeding the history concurrency or read budget returns HTTP 429.")
            .Produces<AiTrendsResponse>();

        group.MapGet("/alarms", async (
            string? from,
            string? to,
            string? state,
            string? level,
            string? keyword,
            int? tagId,
            int? offset,
            int? limit,
            HttpContext http,
            AlarmService alarms,
            IDbContextFactory<ConfigDbContext> factory,
            IConfigSnapshot config,
            CancellationToken ct) =>
        {
            if (await AiScopeGuard.RequireAsync(http, ExternalApiScope.ReadAlarms) is { } scopeDenied) return scopeDenied;
            var access = await http.GetAccessAsync();
            var options = app.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var toMs = ParseTime(to, now);
            var fromMs = ParseTime(from, toMs - (long)TimeSpan.FromDays(7).TotalMilliseconds);
            if (toMs < fromMs) return EndpointHelpers.BadRequest("结束时间不能早于开始时间");

            var maxRangeDays = Math.Clamp(options.MaxRangeDays, 1, 366);
            if (toMs - fromMs > (long)TimeSpan.FromDays(maxRangeDays).TotalMilliseconds)
            {
                return EndpointHelpers.BadRequest($"查询跨度不能超过 {maxRangeDays} 天");
            }

            AlarmListState? listState = null;
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<AlarmListState>(state, true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return EndpointHelpers.BadRequest($"报警状态筛选无效：{state}（可选 {string.Join("/", Enum.GetNames<AlarmListState>())}）");
                }
                listState = parsed;
            }

            AlarmLevel? alarmLevel = null;
            if (!string.IsNullOrWhiteSpace(level))
            {
                if (!Enum.TryParse<AlarmLevel>(level, true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return EndpointHelpers.BadRequest($"报警等级筛选无效：{level}（可选 {string.Join("/", Enum.GetNames<AlarmLevel>())}）");
                }
                alarmLevel = parsed;
            }

            if (tagId is <= 0) return EndpointHelpers.BadRequest("测点 ID 必须大于 0");

            var take = Math.Clamp(limit ?? 50, 1, options.MaxAlarmRows);
            var skip = Math.Max(offset ?? 0, 0);

            // 与前端列表同一套可见性口径：区域受限的调用者只能看到自己区域的报警。
            var restricted = !access.HasGlobal(Permission.View);
            var filter = new AlarmQueryFilter(
                State: listState,
                Level: alarmLevel,
                Keyword: keyword,
                TagId: tagId,
                VisibleTagIds: restricted ? access.Model.TagAreas.Keys.Where(id => access.CanTag(Permission.View, id)).ToArray() : null,
                VisibleDeviceIds: restricted ? access.Model.DeviceAreas.Keys.Where(id => access.CanDevice(Permission.View, id)).ToArray() : null,
                IncludeSystemAlarms: access.HasAny(Permission.View));

            var total = await alarms.CountAsync(fromMs, toMs, false, ct, filter: filter);
            var rows = await alarms.QueryAsync(fromMs, toMs, false, take, ct, skip: skip, filter: filter);

            // 规则的“原因/处置”随报警一起返回：模型解释报警时能直接引用运维指引，不必再去查规则表。
            var ruleIds = rows.Where(x => x.RuleId is int).Select(x => x.RuleId!.Value).Distinct().ToArray();
            var guidance = new Dictionary<int, (string? Cause, string? Action)>();
            if (ruleIds.Length > 0)
            {
                await using var ruleDb = await factory.CreateDbContextAsync(ct);
                var rows2 = await ruleDb.AlarmRules.AsNoTracking()
                    .Where(x => ruleIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.Cause, x.Action })
                    .ToListAsync(ct);
                foreach (var r in rows2) guidance[r.Id] = (r.Cause, r.Action);
            }

            var items = rows.Select(x =>
            {
                config.TryGetTag(x.TagId ?? 0, out var tag);
                var cause = x.RuleId is int rid && guidance.TryGetValue(rid, out var g) ? g.Cause : null;
                var action = x.RuleId is int rid2 && guidance.TryGetValue(rid2, out var g2) ? g2.Action : null;
                return new
                {
                    id = x.Id,
                    tagId = x.TagId,
                    tagName = tag?.Name,
                    deviceId = x.DeviceId,
                    level = x.Level.ToString(),
                    state = AlarmStates.Of(x),
                    value = x.Value,
                    startTs = x.StartTs,
                    endTs = x.EndTs,
                    ackTs = x.AckTs,
                    ackUser = x.AckUser,
                    message = x.Message,
                    cause,
                    action,
                    shelved = x.Shelved,
                    suppressed = x.Suppressed
                };
            }).ToList();

            return Results.Ok(new { items, total, offset = skip, limit = take, dataAsOf = ScadaTime.NowMs() });
        })
            .WithSummary("查询报警历史")
            .WithDescription("Queries alarm history over a bounded window (default last 7 days) with optional state, level, keyword and tag filters. Only alarms in the caller's areas are returned. Read-only: no acknowledgement, shelving or suppression is performed.")
            .Produces<AiAlarmsResponse>();

        group.MapGet("/reports", async (
            string? type,
            string? from,
            string? to,
            string? tagIds,
            string? names,
            HttpContext http,
            ReportService reports,
            IConfigSnapshot config,
            CancellationToken ct) =>
        {
            if (await AiScopeGuard.RequireAsync(http, ExternalApiScope.ReadHistory) is { } scopeDenied) return scopeDenied;
            var access = await http.GetAccessAsync();
            var reportType = string.Equals(type, "monthly", StringComparison.OrdinalIgnoreCase) ? "monthly" : "daily";

            var ids = ResolveTagIds(tagIds, names, null, config);
            if (ids.Length == 0) return EndpointHelpers.BadRequest("请指定 tagIds 或 names");
            if (ids.Length > 50) return EndpointHelpers.BadRequest("单次最多统计 50 个测点");

            var denied = ids.Where(id => !access.CanTag(Permission.View, id)).ToArray();
            if (denied.Length > 0)
            {
                return EndpointHelpers.BadRequest($"无权访问测点：{string.Join(",", denied.Take(20))}");
            }

            var now = DateTimeOffset.UtcNow;
            var defaultFrom = reportType == "monthly"
                ? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
                : new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
            var defaultTo = reportType == "monthly" ? defaultFrom.AddMonths(1) : defaultFrom.AddDays(1);

            var fromMs = ParseTime(from, defaultFrom.ToUnixTimeMilliseconds());
            var toMs = ParseTime(to, defaultTo.ToUnixTimeMilliseconds());
            if (toMs <= fromMs) return EndpointHelpers.BadRequest("结束时间必须晚于开始时间");

            var maxRangeDays = Math.Clamp(app.ServiceProvider.GetRequiredService<IOptions<AiOptions>>().Value.MaxRangeDays, 1, 366);
            if (toMs - fromMs > (long)TimeSpan.FromDays(maxRangeDays).TotalMilliseconds)
            {
                return EndpointHelpers.BadRequest($"统计跨度不能超过 {maxRangeDays} 天");
            }

            var report = await reports.BuildAsync(reportType, ids, fromMs, toMs, ct);
            var series = report.Series.Select(s =>
            {
                config.TryGetTag(s.TagId, out var tag);
                return new
                {
                    tagId = s.TagId,
                    name = tag?.Name ?? $"#{s.TagId}",
                    unit = tag?.Unit,
                    points = s.Points.Select(p => new { ts = p.Ts, avg = p.Avg, min = p.Min, max = p.Max, count = p.Count }).ToArray()
                };
            }).ToList();

            return Results.Ok(new
            {
                type = report.Type,
                from = report.From,
                to = report.To,
                series,
                dataAsOf = ScadaTime.NowMs()
            });
        })
            .WithSummary("生成统计报表")
            .WithDescription("Builds a daily or monthly aggregate report (avg/min/max/count) for up to 50 tags. Returns the computed series as JSON; no file is written and nothing is emailed.")
            .Produces<AiReportsResponse>();

        group.MapGet("/tags", async (
            string? keyword,
            int? deviceId,
            int? areaId,
            int? limit,
            HttpContext http,
            IConfigSnapshot config,
            CancellationToken ct) =>
        {
            if (await AiScopeGuard.RequireAsync(http, ExternalApiScope.ReadTags) is { } scopeDenied) return scopeDenied;
            var access = await http.GetAccessAsync();
            var take = Math.Clamp(limit ?? 100, 1, 500);

            IEnumerable<Tag> query = config.Tags;
            if (deviceId is int dev) query = query.Where(x => x.DeviceId == dev);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                query = query.Where(x => x.Name.Contains(kw) || (x.Description != null && x.Description.Contains(kw)));
            }

            var items = query
                .Where(x => access.CanTag(Permission.View, x.Id))
                .Take(take)
                .Select(x => new
                {
                    id = x.Id,
                    name = x.Name,
                    deviceId = x.DeviceId,
                    kind = x.Kind.ToString(),
                    dataType = x.DataType.ToString(),
                    unit = x.Unit,
                    writable = x.Writable,
                    description = x.Description,
                    address = x.Address
                })
                .ToList();

            return Results.Ok(new { items, count = items.Count, dataAsOf = ScadaTime.NowMs() });
        })
            .WithSummary("检索测点目录")
            .WithDescription("Lists tags visible to the caller with name, device, unit and writability. Use this to resolve tag IDs before calling realtime, trends or reports.")
            .Produces<AiTagsResponse>();
    }

    // 按 ID / 名称 / 设备解析测点。名称匹配大小写不敏感，取精确匹配优先，再退回包含匹配。
    private static int[] ResolveTagIds(string? tagIds, string? names, int? deviceId, IConfigSnapshot config)
    {
        var ids = EndpointHelpers.ParseIds(tagIds);
        if (ids.Length > 0) return ids;

        if (!string.IsNullOrWhiteSpace(names))
        {
            var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matched = new List<int>();
            foreach (var name in wanted)
            {
                var exact = config.Tags.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToArray();
                var hits = exact.Length > 0 ? exact : config.Tags.Where(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).Take(5).ToArray();
                matched.AddRange(hits);
            }
            ids = matched.Distinct().ToArray();
        }

        if (ids.Length == 0 && deviceId is int dev)
        {
            ids = config.Tags.Where(x => x.DeviceId == dev).Select(x => x.Id).Take(200).ToArray();
        }

        return ids;
    }

    private static long ParseTime(string? value, long fallback) => EndpointHelpers.ParseTime(value, fallback);

    // 纵深防御：本组只注册 GET，这里再挡一次非 GET 方法。
    // 万一将来有人误加了一个 POST，会被明确拒绝而不是静默放行。
    private sealed class AiReadOnlyGuard : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var method = context.HttpContext.Request.Method;
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            {
                return Results.Json(new { message = "AI 只读接口仅支持 GET 请求" }, statusCode: StatusCodes.Status405MethodNotAllowed);
            }
            return await next(context);
        }
    }
}

public sealed record AiDeviceItem(
    int Id, string Name, int AreaId, string? AreaName, bool Enabled, string? Protocol, string? Address,
    string? Description, bool? Online, long? LastPollTs, double? LastCycleMs, int? TagCount,
    long? CommFailures, int? ReconnectAttempts, string? Error, bool HasStatus);

public sealed record AiDevicesResponse(IReadOnlyList<AiDeviceItem> Items, int Count, long DataAsOf);

public sealed record AiRealtimeResponse(
    IReadOnlyList<object> Items, int Count, int[] MissingTagIds, long DataAsOf);

public sealed record AiTrendsResponse(
    IReadOnlyList<object> Series, string Resolution, bool Downsampled, int PointLimit, bool Truncated,
    long From, long To, long DataAsOf);

public sealed record AiAlarmsResponse(
    IReadOnlyList<object> Items, int Total, int Offset, int Limit, long DataAsOf);

public sealed record AiReportsResponse(
    string Type, long From, long To, IReadOnlyList<object> Series, long DataAsOf);

public sealed record AiTagsResponse(IReadOnlyList<object> Items, int Count, long DataAsOf);
