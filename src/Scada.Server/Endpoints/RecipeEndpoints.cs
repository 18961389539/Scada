using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record RecipeTemplateRequest(string Name, int AreaId, string? Description, bool DualApproval, List<RecipeParameter> Parameters, long Revision = 0);
public sealed record RecipeRequest(int TemplateId, string Name, string? Description, List<RecipeValue> Values, string? Note, int Version = 0);
public sealed record RecipeUploadRequest(int TemplateId, int? RecipeId, string? Name, string? Note);
public sealed record RecipeDownloadRequest(int Version, bool Confirmed);

// 配方管理：模板（参数绑定测点）、配方与不可变版本、比较、上载、经现场控制闭环下发。按模板所在区域授权，写差分审计。
public static class RecipeEndpoints
{
    public const int MaxParameters = 200;
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_]{0,39}$", RegexOptions.Compiled);

    public static void MapRecipeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recipes").RequireAuthorization().AddEndpointFilter<AuditFailuresFilter>();

        // ---- 模板 ----
        group.MapGet("/templates", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok((await db.RecipeTemplates.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)).Where(x => access.Has(Permission.View, x.AreaId)).Select(View));
        });
        group.MapPost("/templates", (RecipeTemplateRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveTemplateAsync(null, r, http, f, ct));
        group.MapPut("/templates/{id:int}", (int id, RecipeTemplateRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveTemplateAsync(id, r, http, f, ct));
        group.MapDelete("/templates/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var template = await db.RecipeTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (template is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, template.AreaId)) return await Audit.ForbidAsync(http, $"删除配方模板 {template.Name}", "无该区域的配置权限", "RecipeTemplate", id);
            if (await Busy(db, id, ct)) return Results.Conflict(new { message = "该模板的配方正在下发或待复核" });
            var count = await db.Recipes.CountAsync(x => x.TemplateId == id, ct);
            db.RecipeTemplates.Remove(template); // 配方与版本级联删除；下发记录保留
            db.OperationLogs.Add(Audit.Change(http, $"删除配方模板 {template.Name}（含 {count} 个配方）", "RecipeTemplate", id, View(template), null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- 配方 ----
        group.MapGet("/", async (int? templateId, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var templates = (await db.RecipeTemplates.AsNoTracking().ToListAsync(ct)).Where(x => access.Has(Permission.View, x.AreaId)).ToDictionary(x => x.Id);
            var query = db.Recipes.AsNoTracking();
            if (templateId is int t) query = query.Where(x => x.TemplateId == t);
            var recipes = (await query.OrderBy(x => x.Name).ToListAsync(ct)).Where(x => templates.ContainsKey(x.TemplateId)).ToList();
            var ids = recipes.Select(x => x.Id).ToList();
            var versions = await db.RecipeVersions.AsNoTracking().Where(x => ids.Contains(x.RecipeId)).ToListAsync(ct);
            return Results.Ok(recipes.Select(x => RecipeView(x, templates[x.TemplateId], versions.SingleOrDefault(v => v.RecipeId == x.Id && v.Version == x.CurrentVersion))));
        });
        group.MapGet("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var (template, recipe) = await FindAsync(db, id, ct);
            if (recipe is null || !(await http.GetAccessAsync()).Has(Permission.View, template!.AreaId)) return Results.NotFound();
            var version = await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.RecipeId == id && x.Version == recipe.CurrentVersion, ct);
            return Results.Ok(RecipeView(recipe, template, version));
        });
        group.MapPost("/", (RecipeRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveRecipeAsync(null, r, "manual", http, f, ct));
        group.MapPut("/{id:int}", (int id, RecipeRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveRecipeAsync(id, r, "manual", http, f, ct));
        group.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var (template, recipe) = await FindAsync(db, id, ct, track: true);
            if (recipe is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, template!.AreaId)) return await Audit.ForbidAsync(http, $"删除配方 {recipe.Name}", "无该区域的配置权限", "Recipe", id);
            if (await db.RecipeDownloads.AnyAsync(x => x.RecipeId == id && (x.Status == RecipeDownloadStatus.Running || x.Status == RecipeDownloadStatus.Pending), ct))
                return Results.Conflict(new { message = "配方正在下发或待复核" });
            db.Recipes.Remove(recipe);
            db.OperationLogs.Add(Audit.Change(http, $"删除配方 {template.Name}/{recipe.Name}", "Recipe", id, new { recipe.Name, recipe.CurrentVersion }, null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        group.MapGet("/{id:int}/versions", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var (template, recipe) = await FindAsync(db, id, ct);
            if (recipe is null || !(await http.GetAccessAsync()).Has(Permission.View, template!.AreaId)) return Results.NotFound();
            return Results.Ok((await db.RecipeVersions.AsNoTracking().Where(x => x.RecipeId == id).OrderByDescending(x => x.Version).ToListAsync(ct)).Select(VersionView));
        });

        // 比较：两个配方（或同一配方的两个版本），右侧可以是现场实时值（right=live）。
        group.MapGet("/compare", async (int left, int? leftVersion, string right, int? rightVersion, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IRealtimeCache cache, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            async Task<(RecipeTemplate T, Recipe R, RecipeVersion V)?> Side(int id, int? version)
            {
                try { var (t, r, v) = await RecipeService.LoadAsync(db, id, version ?? 0, ct); return access.Has(Permission.View, t.AreaId) ? (t, r, v) : null; }
                catch (ControlWorkflowException) { return null; }
            }
            var a = await Side(left, leftVersion);
            if (a is null) return Results.NotFound();
            var aValues = RecipeService.Values(a.Value.V).ToDictionary(x => x.Key, x => x.Value);
            Dictionary<string, double?> bValues; string bLabel; List<RecipeParameter> bParams;
            if (right == "live")
            {
                bParams = RecipeService.Parameters(a.Value.T); bLabel = "现场实时值";
                bValues = bParams.ToDictionary(p => p.Key, p => cache.Get(p.TagId) is { Quality: TagQuality.Good } v ? v.Value : (double?)null);
            }
            else
            {
                if (!int.TryParse(right, out var rid) || await Side(rid, rightVersion) is not { } b) return Results.NotFound();
                bParams = RecipeService.Parameters(b.T); bLabel = $"{b.R.Name} v{b.V.Version}";
                bValues = RecipeService.Values(b.V).ToDictionary(x => x.Key, x => (double?)x.Value);
            }
            var aParams = RecipeService.Parameters(a.Value.T);
            var keys = aParams.Select(x => x.Key).Concat(bParams.Select(x => x.Key)).Distinct().ToList();
            var rows = keys.Select(k =>
            {
                var p = aParams.FirstOrDefault(x => x.Key == k) ?? bParams.First(x => x.Key == k);
                double? l = aValues.TryGetValue(k, out var lv) ? lv : null; var r = bValues.GetValueOrDefault(k);
                return new { key = k, name = p.Name, unit = p.Unit, tagId = p.TagId, left = l, right = r, different = l is null || r is null || Math.Abs(l.Value - r.Value) > 1e-9 };
            }).ToList();
            return Results.Ok(new { leftLabel = $"{a.Value.R.Name} v{a.Value.V.Version}", rightLabel = bLabel, rows, differences = rows.Count(x => x.different) });
        });

        // 上载：参数测点的当前值保存为新配方或已有配方的新版本。
        group.MapPost("/upload", async (RecipeUploadRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, RecipeService recipes, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var template = await db.RecipeTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TemplateId, ct);
            if (template is null) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (!access.Has(Permission.Configure, template.AreaId)) return await Audit.ForbidAsync(http, $"上载配方：{template.Name}", "无该区域的配置权限", "RecipeTemplate", template.Id);
            var missingView = RecipeService.Parameters(template).FirstOrDefault(p => !access.CanTag(Permission.View, p.TagId));
            if (missingView is not null) return await Audit.ForbidAsync(http, $"上载配方：{template.Name}", $"无参数 {missingView.Name} 测点的查看权限", "RecipeTemplate", template.Id);
            var values = recipes.ReadLive(template, out var missing);
            if (missing.Count > 0) return EndpointHelpers.BadRequest($"以下参数没有有效的实时值，无法上载：{string.Join("、", missing)}");
            var name = request.Name;
            if (request.RecipeId is int rid)
            {
                var existing = await db.Recipes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rid && x.TemplateId == template.Id, ct);
                if (existing is null) return Results.NotFound();
                return await SaveRecipeAsync(rid, new RecipeRequest(template.Id, existing.Name, existing.Description, values, request.Note ?? "从现场上载", existing.CurrentVersion), "upload", http, factory, ct, skipRange: true);
            }
            return await SaveRecipeAsync(null, new RecipeRequest(template.Id, name ?? "", null, values, request.Note ?? "从现场上载"), "upload", http, factory, ct, skipRange: true);
        });

        // ---- 下发 ----
        group.MapPost("/{id:int}/check", async (int id, RecipeDownloadRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, RecipeService recipes, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            try
            {
                var (template, recipe, version) = await RecipeService.LoadAsync(db, id, request.Version, ct);
                if (await CanDownloadAsync(http, template) is string denied) return await Audit.ForbidAsync(http, $"预检查配方 {recipe.Name}", denied, "Recipe", id);
                var check = await recipes.CheckAsync(template, recipe, version, ControlEndpoints.Actor(http), ct);
                return Results.Ok(new { check.RecipeId, check.Version, check.DualApproval, ok = check.Ok, refusals = check.Refusals.ToList(), check.Parameters });
            }
            catch (ControlWorkflowException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        }).RequireAuthorization(AccessPolicies.Control);
        group.MapPost("/{id:int}/download", async (int id, RecipeDownloadRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, RecipeService recipes, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            try
            {
                var (template, recipe, version) = await RecipeService.LoadAsync(db, id, request.Version, ct);
                if (await CanDownloadAsync(http, template) is string denied) return await Audit.ForbidAsync(http, $"下发配方 {recipe.Name}", denied, "Recipe", id);
                if (!request.Confirmed) return EndpointHelpers.BadRequest("下发配方前须确认");
                var download = await recipes.RequestAsync(id, version.Version, ControlEndpoints.Actor(http), ct);
                return Results.Ok(DownloadView(download));
            }
            catch (ControlWorkflowException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        }).RequireAuthorization(AccessPolicies.Control);
        group.MapGet("/downloads", async (int? recipeId, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.RecipeDownloads.AsNoTracking();
            if (recipeId is int r) query = query.Where(x => x.RecipeId == r);
            return Results.Ok((await query.OrderByDescending(x => x.Id).Take(200).ToListAsync(ct)).Where(x => access.Has(Permission.View, x.AreaId)).Select(DownloadView));
        });
        group.MapGet("/downloads/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var d = await db.RecipeDownloads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return d is null || !(await http.GetAccessAsync()).Has(Permission.View, d.AreaId) ? Results.NotFound() : Results.Ok(DownloadView(d));
        });
        group.MapPost("/downloads/{id:int}/abort", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, RecipeService recipes, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var d = await db.RecipeDownloads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Control, d.AreaId)) return await Audit.ForbidAsync(http, $"中止配方下发 #{id}", "无该区域的控制权限", "RecipeDownload", id);
            return await recipes.AbortAsync(id, ControlEndpoints.Actor(http), ct) ? Results.Accepted() : Results.Conflict(new { message = "下发已结束" });
        }).RequireAuthorization(AccessPolicies.Control);
    }

    public static async Task<string?> CanDownloadAsync(HttpContext http, RecipeTemplate template)
    {
        var access = await http.GetAccessAsync();
        if (!access.Has(Permission.Control, template.AreaId)) return "无该配方所在区域的控制权限";
        var missing = RecipeService.Parameters(template).FirstOrDefault(p => !access.CanTag(Permission.Control, p.TagId));
        return missing is null ? null : $"无参数 {missing.Name}（测点 #{missing.TagId}）所在区域的控制权限";
    }

    private static Task<bool> Busy(ConfigDbContext db, int templateId, CancellationToken ct) =>
        db.RecipeDownloads.AnyAsync(x => x.TemplateId == templateId && (x.Status == RecipeDownloadStatus.Running || x.Status == RecipeDownloadStatus.Pending), ct);

    private static async Task<(RecipeTemplate?, Recipe?)> FindAsync(ConfigDbContext db, int id, CancellationToken ct, bool track = false)
    {
        var recipe = track ? await db.Recipes.SingleOrDefaultAsync(x => x.Id == id, ct) : await db.Recipes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (recipe is null) return (null, null);
        return (await db.RecipeTemplates.AsNoTracking().SingleAsync(x => x.Id == recipe.TemplateId, ct), recipe);
    }

    // 模板参数校验（工程包导入共用）。
    public static string? ValidateParameters(List<RecipeParameter> parameters, IReadOnlyDictionary<int, Tag> tags)
    {
        if (parameters is null || parameters.Count is 0 or > MaxParameters) return $"参数须为 1～{MaxParameters} 个";
        var keys = new HashSet<string>(); var tagIds = new HashSet<int>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i]; var label = $"第 {i + 1} 个参数";
            p.Key = p.Key?.Trim() ?? ""; p.Name = p.Name?.Trim() ?? ""; p.Unit = string.IsNullOrWhiteSpace(p.Unit) ? null : p.Unit.Trim();
            if (!KeyPattern.IsMatch(p.Key)) return $"{label}的键须以字母开头，由字母、数字、下划线组成（最多 40 个字符）";
            if (!keys.Add(p.Key)) return $"参数键 {p.Key} 重复";
            if (p.Name.Length is 0 or > 100) return $"{label}名称须为 1～100 个字符";
            if (p.Unit?.Length > 20) return $"{label}单位最多 20 个字符";
            if (!tags.TryGetValue(p.TagId, out var tag) || tag.Kind != TagKind.Physical || !tag.Writable) return $"参数 {p.Name} 的测点不存在或不可写";
            if (!tagIds.Add(p.TagId)) return $"参数 {p.Name} 的测点与其他参数重复";
            if (p.Min is double a && p.Max is double b && a > b) return $"参数 {p.Name} 的下限不能大于上限";
            if (p.Min is double mn && !double.IsFinite(mn) || p.Max is double mx && !double.IsFinite(mx)) return $"参数 {p.Name} 的上下限无效";
            if (p.DefaultValue is double d && (p.Min is double lo && d < lo || p.Max is double hi && d > hi)) return $"参数 {p.Name} 的默认值超出上下限";
        }
        return null;
    }

    private static async Task<IResult> SaveTemplateAsync(int? id, RecipeTemplateRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return EndpointHelpers.BadRequest("名称须为 1～100 个字符");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Areas.AnyAsync(x => x.Id == request.AreaId, ct)) return EndpointHelpers.BadRequest("区域不存在");
        var existing = id is int tid ? await db.RecipeTemplates.SingleOrDefaultAsync(x => x.Id == tid, ct) : null;
        if (id is not null && existing is null) return Results.NotFound();
        var action = existing is null ? $"新建配方模板 {name}" : $"修改配方模板 {existing.Name}";
        if (!access.Has(Permission.Configure, request.AreaId) || existing is not null && !access.Has(Permission.Configure, existing.AreaId))
            return await Audit.ForbidAsync(http, action, "无该区域的配置权限", "RecipeTemplate", id);
        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        if (ValidateParameters(request.Parameters, tags) is string error) return EndpointHelpers.BadRequest(error);
        var denied = request.Parameters.FirstOrDefault(p => !access.CanTag(Permission.Configure, p.TagId));
        if (denied is not null) return await Audit.ForbidAsync(http, action, $"参数 {denied.Name} 的测点 #{denied.TagId} 不在可配置区域", "RecipeTemplate", id);
        if (await db.RecipeTemplates.AnyAsync(x => x.Name == name && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("名称已存在");
        var before = existing is null ? null : View(existing);
        if (existing is not null)
        {
            if (existing.Revision != request.Revision) return Results.Conflict(new { message = "模板已被他人修改，请刷新" });
            if (await Busy(db, existing.Id, ct)) return Results.Conflict(new { message = "该模板的配方正在下发或待复核，不能修改" });
            existing.Revision++;
        }
        var template = existing ?? db.RecipeTemplates.Add(new RecipeTemplate()).Entity;
        template.Name = name; template.AreaId = request.AreaId; template.Description = Blank(request.Description); template.DualApproval = request.DualApproval;
        template.ParametersJson = JsonSerializer.Serialize(request.Parameters, RecipeService.Json); template.UpdatedTs = ScadaTime.NowMs(); template.UpdatedBy = http.User.GetUserName();
        await db.SaveChangesAsync(ct);
        db.OperationLogs.Add(Audit.Change(http, action, "RecipeTemplate", template.Id, before, View(template)));
        await db.SaveChangesAsync(ct);
        return existing is null ? Results.Created($"/api/recipes/templates/{template.Id}", View(template)) : Results.Ok(View(template));
    }

    // 保存配方：新建为 v1；修改生成新版本（旧版本不可变），Version 须等于当前版本（乐观并发）。
    private static async Task<IResult> SaveRecipeAsync(int? id, RecipeRequest request, string source, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct, bool skipRange = false)
    {
        var access = await http.GetAccessAsync();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return EndpointHelpers.BadRequest("配方名称须为 1～100 个字符");
        if (request.Note?.Trim().Length > 200) return EndpointHelpers.BadRequest("版本说明最多 200 字");
        await using var db = await factory.CreateDbContextAsync(ct);
        var template = await db.RecipeTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.TemplateId, ct);
        if (template is null) return EndpointHelpers.BadRequest("配方模板不存在");
        var existing = id is int rid ? await db.Recipes.SingleOrDefaultAsync(x => x.Id == rid, ct) : null;
        if (id is not null && (existing is null || existing.TemplateId != template.Id)) return Results.NotFound();
        var action = source == "upload" ? $"上载配方 {template.Name}/{name}" : existing is null ? $"新建配方 {template.Name}/{name}" : $"修改配方 {template.Name}/{existing.Name}";
        if (!access.Has(Permission.Configure, template.AreaId)) return await Audit.ForbidAsync(http, action, "无该区域的配置权限", "Recipe", id);
        var parameters = RecipeService.Parameters(template);
        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var values = request.Values ?? [];
        var errors = RecipeService.Validate(parameters, values, tags);
        // 上载如实记录现场值：超出模板上下限只提示（下发时仍会被拦截），不阻止保存。
        var warnings = skipRange ? errors.Where(x => x.Contains("超出范围")).ToList() : [];
        errors = errors.Except(warnings).ToList();
        if (errors.Count > 0) return EndpointHelpers.BadRequest(string.Join("；", errors.Take(10)));
        if (await db.Recipes.AnyAsync(x => x.TemplateId == template.Id && x.Name == name && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("该模板下已有同名配方");
        var ordered = parameters.Select(p => new RecipeValue { Key = p.Key, Value = values.First(v => v.Key == p.Key).Value }).ToList();
        object? before = null;
        if (existing is not null)
        {
            if (existing.CurrentVersion != request.Version) return Results.Conflict(new { message = "配方已被他人修改，请刷新" });
            var prev = await db.RecipeVersions.AsNoTracking().SingleAsync(x => x.RecipeId == existing.Id && x.Version == existing.CurrentVersion, ct);
            before = new { existing.Name, existing.Description, version = prev.Version, values = RecipeService.Values(prev).ToDictionary(x => x.Key, x => x.Value) };
        }
        var recipe = existing ?? db.Recipes.Add(new Recipe { TemplateId = template.Id, CurrentVersion = 0 }).Entity;
        recipe.Name = name; recipe.Description = Blank(request.Description); recipe.CurrentVersion++; recipe.UpdatedTs = ScadaTime.NowMs(); recipe.UpdatedBy = http.User.GetUserName();
        await db.SaveChangesAsync(ct);
        var version = new RecipeVersion
        {
            RecipeId = recipe.Id, Version = recipe.CurrentVersion, ValuesJson = JsonSerializer.Serialize(ordered, RecipeService.Json), Note = Blank(request.Note), Source = source,
            CreatedTs = ScadaTime.NowMs(), CreatedBy = http.User.GetUserName()
        };
        db.RecipeVersions.Add(version);
        db.OperationLogs.Add(Audit.Change(http, action + $" → v{version.Version}", "Recipe", recipe.Id, before,
            new { recipe.Name, recipe.Description, version = version.Version, values = ordered.ToDictionary(x => x.Key, x => x.Value) }));
        await db.SaveChangesAsync(ct);
        var view = new { recipe = RecipeView(recipe, template, version), warnings };
        return existing is null ? Results.Created($"/api/recipes/{recipe.Id}", view) : Results.Ok(view);
    }

    private static object View(RecipeTemplate x) => new { x.Id, x.Name, x.AreaId, x.Description, x.DualApproval, x.Revision, x.UpdatedTs, x.UpdatedBy, parameters = RecipeService.Parameters(x) };
    private static object RecipeView(Recipe x, RecipeTemplate t, RecipeVersion? v) => new
    {
        x.Id, x.TemplateId, templateName = t.Name, t.AreaId, x.Name, x.Description, version = x.CurrentVersion, x.UpdatedTs, x.UpdatedBy,
        values = v is null ? [] : RecipeService.Values(v), note = v?.Note, source = v?.Source
    };
    private static object VersionView(RecipeVersion v) => new { v.Version, v.Note, v.Source, v.CreatedTs, v.CreatedBy, values = RecipeService.Values(v) };
    public static object DownloadView(RecipeDownload d) => new
    {
        d.Id, d.RecipeId, d.RecipeName, d.Version, d.TemplateId, d.AreaId, status = d.Status.ToString(), statusLabel = RecipeService.Label(d.Status), d.RequestedBy, d.ApprovedBy, d.ApprovalId,
        d.CreatedTs, d.StartedTs, d.EndedTs, d.Written, d.Message, results = RecipeService.Results(d)
    };
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
