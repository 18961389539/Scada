using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record HmiLibraryRequest(string Key, string Name, string? Category, string Kind, string? Svg, ScreenDocument? Document, List<string>? Placeholders, double Width, double Height, int Revision = 0);

// 图库：用户符号（SVG）与参数化模板。草稿按键引用，发布版本固化所需图元和符号。
public static partial class HmiLibraryEndpoints
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxSvgLength = 200_000;

    public static void MapHmiLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hmi/library").RequireAuthorization();
        group.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var items = await db.HmiLibrary.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
            // 一次扫描画面与模板，给列表补上“被谁引用”，避免每行各扫一遍。
            var usage = await UsageMapAsync(db, items, ct);
            return Results.Ok(items.Select(x => View(x, usage.GetValueOrDefault(x.Id))));
        });
        group.MapPost("", (HmiLibraryRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => SaveAsync(null, request, http, factory, ct))
            .RequireAuthorization(AccessPolicies.GlobalConfigure);
        group.MapPut("/{id:int}", (int id, HmiLibraryRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => SaveAsync(id, request, http, factory, ct))
            .RequireAuthorization(AccessPolicies.GlobalConfigure);
        // 导入：按键新增或覆盖（整批先校验，任一项无效则不写入）。
        group.MapPost("/import", async (List<HmiLibraryRequest> items, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (items is null || items.Count is 0 or > 500) return EndpointHelpers.BadRequest("导入须为 1～500 项");
            var dup = items.GroupBy(x => x.Key).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) return EndpointHelpers.BadRequest($"键重复：{dup.Key}");
            foreach (var item in items) if (Validate(item) is string error) return EndpointHelpers.BadRequest($"{item.Key}：{error}");
            await using var db = await factory.CreateDbContextAsync(ct);
            var existing = await db.HmiLibrary.ToDictionaryAsync(x => x.Key, ct);
            var stale = items.Where(item => existing.TryGetValue(item.Key, out var row) && row.Revision != item.Revision).Select(item => item.Key).ToList();
            if (stale.Count > 0) return Results.Conflict(new { message = $"图库项已被他人修改，请刷新：{string.Join("、", stale.Take(8))}", keys = stale });
            int added = 0, updated = 0;
            foreach (var item in items)
            {
                if (existing.TryGetValue(item.Key, out var row)) { Apply(row, item, http); row.Revision++; updated++; }
                else { row = new HmiLibraryItem { Key = item.Key }; Apply(row, item, http); db.HmiLibrary.Add(row); added++; }
            }
            db.OperationLogs.Add(Audit.Change(http, $"导入图库 {added} 新增 {updated} 覆盖", "HmiLibrary", null, null, new { keys = items.Select(x => x.Key) }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added, updated });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
        group.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.HmiLibrary.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            var users = await UsersAsync(db, row, ct);
            if (users.Count > 0) return EndpointHelpers.BadRequest($"仍被画面引用：{string.Join("、", users.Take(5))}");
            db.HmiLibrary.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除图库项 {row.Key}", "HmiLibrary", row.Id, View(row), null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
    }

    private static async Task<IResult> SaveAsync(int? id, HmiLibraryRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        if (Validate(request) is string error) return EndpointHelpers.BadRequest(error);
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = id is int rid ? await db.HmiLibrary.SingleOrDefaultAsync(x => x.Id == rid, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (await db.HmiLibrary.AnyAsync(x => x.Key == request.Key && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("键已存在");
        if (row is not null && row.Key != request.Key && (await UsersAsync(db, row, ct)).Count > 0) return EndpointHelpers.BadRequest("已被画面引用的图库项不能改键");
        if (row is not null && row.Kind != request.Kind) return EndpointHelpers.BadRequest("不能改变图库项类型");
        if (row is not null && row.Revision != request.Revision) return Results.Conflict(new { message = "图库项已被他人修改，请刷新" });
        var before = row is null ? null : View(row);
        row ??= db.HmiLibrary.Add(new HmiLibraryItem()).Entity;
        if (before is not null) row.Revision++;
        Apply(row, request, http);
        await db.SaveChangesAsync(ct);
        db.OperationLogs.Add(Audit.Change(http, $"{(before is null ? "新建" : "修改")}图库项 {row.Key}", "HmiLibrary", row.Id, before, View(row)));
        await db.SaveChangesAsync(ct);
        var users = await UsersAsync(db, row, ct);
        return Results.Ok(new { item = View(row), usedBy = users });
    }

    private static void Apply(HmiLibraryItem row, HmiLibraryRequest r, HttpContext http)
    {
        row.Key = r.Key; row.Name = r.Name.Trim(); row.Category = (r.Category ?? "").Trim(); row.Kind = r.Kind; row.Width = r.Width; row.Height = r.Height;
        row.Svg = r.Kind == "symbol" ? r.Svg : null;
        row.DocumentJson = r.Kind == "template" ? JsonSerializer.Serialize(r.Document, Json) : null;
        row.PlaceholdersJson = JsonSerializer.Serialize(r.Kind == "template" ? r.Placeholders ?? [] : [], Json);
        row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
    }

    public static string? Validate(HmiLibraryRequest r)
    {
        if (r.Key is null || !ScreenValidator.KeyPattern().IsMatch(r.Key)) return "键须为字母开头的 1～64 个字母、数字、下划线或连字符";
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100) return "名称须为 1～100 个字符";
        if ((r.Category ?? "").Length > 40) return "分类最多 40 个字符";
        if (!double.IsFinite(r.Width) || !double.IsFinite(r.Height) || r.Width is < 16 or > 4000 || r.Height is < 16 or > 4000) return "尺寸须为 16～4000";
        if (r.Kind == "symbol")
        {
            if (string.IsNullOrWhiteSpace(r.Svg) || r.Svg.Length > MaxSvgLength) return $"SVG 不能为空且不超过 {MaxSvgLength} 字符";
            return SvgError(r.Svg);
        }
        if (r.Kind != "template") return "类型须为 symbol 或 template";
        if (r.Document is null) return "模板内容不能为空";
        var placeholders = r.Placeholders ?? [];
        if (placeholders.Count > 50 || placeholders.Any(p => p is null || !ScreenValidator.KeyPattern().IsMatch(p)) || placeholders.Distinct().Count() != placeholders.Count) return "占位符须为不重复的键（最多 50 个）";
        var doc = new ScreenDocument { SchemaVersion = 1, Width = Math.Max(320, r.Width), Height = Math.Max(200, r.Height), Background = r.Document.Background ?? "#101b2d", Nodes = r.Document.Nodes, SymbolAssets = r.Document.SymbolAssets };
        if ((r.Document.Nodes?.Count ?? 0) > 200) return "模板最多 200 个图元";
        var issue = ScreenValidator.Validate(doc, templateDocument: true).FirstOrDefault(x => !x.Message.StartsWith("画布"));
        if (issue is not null) return $"图元 {issue.NodeId}：{issue.Message}";
        if (r.Document.Nodes!.Any(n => n.X + n.Width > r.Width + 0.01 || n.Y + n.Height > r.Height + 0.01)) return "图元超出模板尺寸";
        var used = r.Document.Nodes!.SelectMany(n => new[] { n.Bind, n.AlarmBind, n.Action?.Bind, n.AnimationBind }
            .Concat(n.Commands?.Select(command => command.Bind) ?? [])
            .Concat(n.TagBinds ?? [])).Where(x => x is not null).Distinct().ToList();
        var undeclared = used.Where(x => !placeholders.Contains(x!)).ToList();
        if (undeclared.Count > 0) return $"占位符未声明：{string.Join("、", undeclared)}";
        return null;
    }

    // 用户 SVG 只允许静态图形：拒绝脚本、事件属性、外部引用与嵌入 HTML（前端导入时也会清洗）。
    public static string? SvgError(string svg)
    {
        if (!SvgRoot().IsMatch(svg)) return "SVG 须以 <svg 开头";
        if (SvgForbidden().Match(svg) is { Success: true } m) return $"SVG 含不允许的内容：{m.Value.Trim()}";
        return null;
    }
    [GeneratedRegex(@"^\s*(<\?xml[^>]*\?>\s*)?<svg[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex SvgRoot();
    [GeneratedRegex(@"<\s*(script|foreignObject|iframe|object|embed|image|use|style|a|animate|animateMotion|animateTransform|set|feImage)\b|\son[a-z]+\s*=|javascript:|<!ENTITY|<!DOCTYPE|(href|src)\s*=\s*[""']?\s*(?!#)[a-z]+:|url\(\s*[""']?\s*(?!#)", RegexOptions.IgnoreCase)]
    private static partial Regex SvgForbidden();

    public static object View(HmiLibraryItem x, IReadOnlyList<string>? usedBy = null) => new
    {
        x.Id, x.Key, x.Name, x.Category, x.Kind, x.Svg, x.Width, x.Height, x.Revision, x.UpdatedTs, x.UpdatedBy,
        document = x.DocumentJson is null ? null : JsonSerializer.Deserialize<ScreenDocument>(x.DocumentJson, Json),
        placeholders = JsonSerializer.Deserialize<List<string>>(x.PlaceholdersJson, Json) ?? [],
        usedBy = usedBy ?? []
    };

    // 图库列表的批量引用统计：画面草稿、仍依赖图库的旧发布版本，以及模板对符号的引用。
    private static async Task<Dictionary<int, List<string>>> UsageMapAsync(ConfigDbContext db, IReadOnlyList<HmiLibraryItem> items, CancellationToken ct)
    {
        var screens = await db.ProcessScreens.AsNoTracking()
            .Select(x => new { x.Name, x.DraftJson, x.PublishedJson }).ToListAsync(ct);
        var templates = await db.HmiLibrary.AsNoTracking().Where(x => x.Kind == "template")
            .Select(x => new { x.Id, x.Key, x.Name, x.DocumentJson }).ToListAsync(ct);
        var screenTemplates = new Dictionary<string, HashSet<string>>();
        var screenSymbols = new Dictionary<string, HashSet<string>>();
        var templateSymbols = new Dictionary<string, HashSet<string>>();
        void Collect(ScreenDocument? document, IDictionary<string, HashSet<string>> templateTargets, IDictionary<string, HashSet<string>> symbolTargets, string owner)
        {
            foreach (var node in document?.Nodes ?? [])
            {
                if (node is null) continue;
                if (node.Type == "template" && node.TemplateKey is not null)
                    Add(templateTargets, node.TemplateKey, owner);
                if (node.Symbol is { Length: > 5 } symbol && symbol.StartsWith("user:", StringComparison.Ordinal))
                    Add(symbolTargets, symbol[5..], owner);
            }
        }
        static void Add(IDictionary<string, HashSet<string>> targets, string key, string owner)
        {
            if (!targets.TryGetValue(key, out var owners)) targets[key] = owners = [];
            owners.Add(owner);
        }
        foreach (var screen in screens)
        {
            var draft = TryParse(screen.DraftJson);
            Collect(draft, screenTemplates, screenSymbols, screen.Name);
            if (TryParse(screen.PublishedJson) is { } published && Screens.ScreenPublication.RequiresRepublish(published))
                Collect(published, screenTemplates, screenSymbols, screen.Name);
        }
        foreach (var template in templates)
            Collect(TryParse(template.DocumentJson), new Dictionary<string, HashSet<string>>(), templateSymbols, $"模板 {template.Name}");
        return items.ToDictionary(x => x.Id, x => x.Kind == "template"
            ? (screenTemplates.GetValueOrDefault(x.Key) ?? []).ToList()
            : ((screenSymbols.GetValueOrDefault(x.Key) ?? []).Concat(templateSymbols.GetValueOrDefault(x.Key) ?? [])).Distinct().ToList());
    }
    private static ScreenDocument? TryParse(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ScreenDocument>(json, Json); } catch (JsonException) { return null; }
    }

    public static async Task<Func<string, HmiTemplateDefinition?>> LookupAsync(ConfigDbContext db, CancellationToken ct)
    {
        var templates = (await db.HmiLibrary.AsNoTracking().Where(x => x.Kind == "template").ToListAsync(ct)).ToDictionary(x => x.Key, x =>
        {
            var doc = JsonSerializer.Deserialize<ScreenDocument>(x.DocumentJson ?? "{}", Json) ?? new ScreenDocument();
            return new HmiTemplateDefinition(x.Key, x.Width, x.Height, doc.Nodes ?? [], JsonSerializer.Deserialize<List<string>>(x.PlaceholdersJson, Json) ?? []);
        });
        return key => templates.GetValueOrDefault(key);
    }

    public static async Task<HashSet<string>> SymbolKeysAsync(ConfigDbContext db, CancellationToken ct) =>
        (await db.HmiLibrary.AsNoTracking().Where(x => x.Kind == "symbol").Select(x => x.Key).ToListAsync(ct)).Select(x => $"user:{x}").ToHashSet();

    // 固化发布版本已自包含；只有草稿、未固化旧发布版本与图库模板仍依赖当前图库。
    private static async Task<List<string>> UsersAsync(ConfigDbContext db, HmiLibraryItem row, CancellationToken ct)
    {
        var symbol = $"user:{row.Key}";
        bool Uses(IEnumerable<ScreenNode>? nodes) => (nodes ?? []).Any(n => row.Kind == "template" ? n.Type == "template" && n.TemplateKey == row.Key : n.Symbol == symbol);
        var result = new List<string>();
        foreach (var s in await db.ProcessScreens.AsNoTracking().ToListAsync(ct))
        {
            var published = s.PublishedJson is null ? null : JsonSerializer.Deserialize<ScreenDocument>(s.PublishedJson, Json);
            if (Uses(JsonSerializer.Deserialize<ScreenDocument>(s.DraftJson, Json)?.Nodes)
                || published is not null && Screens.ScreenPublication.RequiresRepublish(published) && Uses(published.Nodes)) result.Add(s.Name);
        }
        if (row.Kind == "symbol")
            foreach (var t in await db.HmiLibrary.AsNoTracking().Where(x => x.Kind == "template" && x.Id != row.Id).ToListAsync(ct))
                if (Uses(JsonSerializer.Deserialize<ScreenDocument>(t.DocumentJson ?? "{}", Json)?.Nodes)) result.Add($"模板 {t.Name}");
        return result;
    }
}
