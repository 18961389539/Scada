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

/// <summary>报表模板、对象模板、图库、画面、运行布局。</summary>
internal sealed class ProjectContentApplier(ProjectApplyContext context) : ProjectSection(context)
{
    internal async Task ApplyReportTemplatesAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "reportTemplates"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var columns = data["columns"]?.AsArray() ?? []; data.Remove("columns");
            var incoming = data.Deserialize<ReportTemplate>(ProjectFormat.Json)!;
            incoming.ColumnsJson = columns.ToJsonString();
            var existing = entry.Action == "update" ? await Db.ReportTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            Need(Permission.Configure, entry, [incoming.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            try { ReportTemplates.Validate(incoming, ReportTemplates.Columns(incoming.ColumnsJson)); }
            catch (Exception ex) when (ex is ReportTemplateException or JsonException) { Error(entry, ex.Message); continue; }
            if (existing is null) { incoming.Revision = 1; incoming.UpdatedTs = Now; incoming.ScheduleSinceTs = incoming.ScheduleEnabled ? Now : null; Db.ReportTemplates.Add(incoming); }
            else
            {
                incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; incoming.UpdatedTs = Now;
                incoming.ScheduleSinceTs = incoming.ScheduleEnabled ? existing.ScheduleEnabled ? existing.ScheduleSinceTs : Now : existing.ScheduleSinceTs;
                Db.Entry(existing).CurrentValues.SetValues(incoming);
            }
        }
        await SaveAsync(ct);
    }

    internal async Task ApplyObjectTemplatesAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "objectTemplates"))
        {
            NeedGlobal(Permission.Configure, entry);
            var draft = entry.Data!["draft"]?.ToJsonString() ?? "{}";
            try { _ = JsonSerializer.Deserialize<ObjectTemplateDocument>(draft, ObjectTemplateJson.Options) ?? throw new JsonException("内容为空"); }
            catch (JsonException ex) { Error(entry, $"模板内容无效：{ex.Message}"); continue; }
            var template = entry.Action == "update" ? await Db.ObjectTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct) : Db.ObjectTemplates.Add(new DeviceObjectTemplate()).Entity;
            template.Name = ProjectFormat.Name(entry.Data!); template.DraftJson = draft;
            template.Revision = entry.Action == "update" ? template.Revision + 1 : 1; template.UpdatedTs = Now; template.UpdatedBy = UserName;
            await SaveAsync(ct);
            var versions = await Db.ObjectTemplateVersions.Where(x => x.TemplateId == template.Id).ToDictionaryAsync(x => x.Version, ct);
            foreach (var version in entry.Data!["versions"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var number = version["version"]!.GetValue<int>(); var document = version["document"]?.ToJsonString() ?? "{}";
                if (versions.TryGetValue(number, out var stored))
                {
                    if (!JsonNode.DeepEquals(JsonNode.Parse(stored.DocumentJson), JsonNode.Parse(document))) Error(entry, $"已发布版本 v{number} 内容不同，已发布版本不可覆盖，请改名导入");
                }
                else Db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion { TemplateId = template.Id, Version = number, DocumentJson = document, CreatedTs = Now, CreatedBy = UserName });
            }
            template.LatestVersion = Math.Max(template.LatestVersion, entry.Data!["versions"]?.AsArray().OfType<JsonObject>().Select(x => x["version"]!.GetValue<int>()).DefaultIfEmpty(0).Max() ?? 0);
        }
        await SaveAsync(ct);
    }

    internal async Task ApplyHmiLibraryAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "hmiLibrary"))
        {
            NeedGlobal(Permission.Configure, entry);
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            Scada.Server.Endpoints.HmiLibraryRequest request;
            try { request = data.Deserialize<Scada.Server.Endpoints.HmiLibraryRequest>(ScreenJson)!; }
            catch (JsonException ex) { Error(entry, $"图库内容无效：{ex.Message}"); continue; }
            if (Scada.Server.Endpoints.HmiLibraryEndpoints.Validate(request) is string libError) { Error(entry, libError); continue; }
            var item = entry.Action == "update" ? await Db.HmiLibrary.SingleAsync(x => x.Key == entry.FinalKey, ct) : Db.HmiLibrary.Add(new HmiLibraryItem()).Entity;
            item.Key = request.Key; item.Name = request.Name.Trim(); item.Category = request.Category ?? ""; item.Kind = request.Kind; item.Width = request.Width; item.Height = request.Height;
            item.Svg = request.Kind == "symbol" ? request.Svg : null;
            item.DocumentJson = request.Kind == "template" ? JsonSerializer.Serialize(request.Document, ScreenJson) : null;
            item.PlaceholdersJson = JsonSerializer.Serialize(request.Placeholders ?? [], ScreenJson);
            item.Revision = entry.Action == "update" ? item.Revision + 1 : 1; item.UpdatedTs = Now; item.UpdatedBy = UserName;
        }
        await SaveAsync(ct);
    }

    internal async Task ApplyScreensAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        // 画面：先建出所有新画面以便互相跳转引用，再写入内容。
        var screenEntries = Upserts(work, "screens").ToList();
        var screenRows = new Dictionary<PlanEntry, ProcessScreen>();
        foreach (var entry in screenEntries)
        {
            NeedGlobal(Permission.Configure, entry);
            var screen = entry.Action == "update" ? await Db.ProcessScreens.SingleAsync(x => x.Name == entry.FinalKey, ct)
                : Db.ProcessScreens.Add(new ProcessScreen { DraftJson = "{}", Revision = 0 }).Entity;
            await ProcessScreenHistory.PreserveCurrentAsync(Db, screen, ct);
            screen.Name = ProjectFormat.Name(entry.Data!); screenRows[entry] = screen;
        }
        await SaveAsync(ct); await LoadAsync(ct);
        foreach (var (entry, screen) in screenRows)
        {
            string? Document(string field)
            {
                if (entry.Data![field] is not JsonNode node) return null;
                var data = Resolve(entry, new JsonObject { ["doc"] = node.DeepClone() }); if (data is null) return null;
                ScreenDocument document;
                try { document = data["doc"].Deserialize<ScreenDocument>(ScreenJson)!; }
                catch (JsonException ex) { Error(entry, $"画面内容无效：{ex.Message}"); return null; }
                foreach (var issue in ScreenValidator.Validate(document, publishedDocument: field == "published", expandedDocument: field == "published")) Error(entry, $"节点 {issue.NodeId}：{issue.Message}");
                if (field == "published")
                    foreach (var issue in Screens.ScreenPublication.ValidateAssets(document)) Error(entry, $"节点 {issue.NodeId}：{issue.Message}");
                return JsonSerializer.Serialize(document, ScreenJson);
            }
            screen.DraftJson = Document("draft") ?? JsonSerializer.Serialize(new ScreenDocument(), ScreenJson);
            screen.PublishedJson = Document("published");
            screen.PublishedName = screen.PublishedJson is null ? null : entry.Data!["publishedName"]?.GetValue<string>() ?? screen.Name;
            screen.Revision++; screen.PublishedRevision = screen.PublishedJson is null ? null : screen.Revision;
            screen.DraftDirty = screen.PublishedJson is null || screen.DraftJson != screen.PublishedJson;
            screen.UpdatedTs = Now; screen.UpdatedBy = UserName;
            if (screen.PublishedJson is not null)
                ProcessScreenHistory.Record(Db, screen, Now, UserName, "工程应用发布版本", "import");
        }
        await SaveAsync(ct);
        foreach (var screen in screenRows.Values.Where(x => x.PublishedJson is not null))
            await ProcessScreenHistory.TrimAsync(Db, screen.Id, ct);
    }

    internal async Task ApplyHmiLayoutsAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        var deletedScreenNames = Deletes(work, "screens").Select(x => x.FinalKey).ToHashSet();
        var publishedScreenIds = (await Db.ProcessScreens.AsNoTracking().Where(x => x.PublishedJson != null && !deletedScreenNames.Contains(x.Name)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var layoutVersions = new List<(HmiLayout Layout, HmiLayoutVersion Version)>();
        foreach (var entry in Upserts(work, "hmiLayouts"))
        {
            NeedGlobal(Permission.Configure, entry);
            var data = ProjectFormat.LayoutData(entry.Data!);
            var draftData = Resolve(entry, new JsonObject { ["document"] = data["draft"]?.DeepClone() }, allowMissingScreenRefs: true);
            var publishedData = Resolve(entry, new JsonObject { ["document"] = data["published"]?.DeepClone() });
            if (draftData is null || publishedData is null) continue;
            HmiLayoutDocument? draft, published;
            try
            {
                draft = draftData["document"].Deserialize<HmiLayoutDocument>(ScreenJson);
                published = publishedData["document"]?.Deserialize<HmiLayoutDocument>(ScreenJson);
            }
            catch (JsonException ex) { Error(entry, $"布局内容无效：{ex.Message}"); continue; }
            if (HmiLayoutValidator.ValidateDraft(draft) is string draftError) { Error(entry, $"草稿：{draftError}"); continue; }
            if (published is not null && HmiLayoutValidator.Validate(published, publishedScreenIds) is string publishedError) { Error(entry, $"发布：{publishedError}"); continue; }
            var name = ProjectFormat.Name(data).Trim();
            var publishedName = published is null ? null : data["publishedName"]?.GetValue<string>()?.Trim() ?? name;
            if (name.Length is 0 or > 100 || publishedName is { Length: 0 or > 100 }) { Error(entry, "布局名称须为 1～100 个字符"); continue; }
            var layout = entry.Action == "update" ? await Db.HmiLayouts.SingleAsync(x => x.Name == entry.FinalKey, ct) : Db.HmiLayouts.Add(new HmiLayout()).Entity;
            var publishedJson = published is null ? null : JsonSerializer.Serialize(published, ScreenJson);
            var publicationChanged = layout.PublishedName != publishedName || !JsonNode.DeepEquals(
                layout.PublishedJson is null ? null : JsonNode.Parse(layout.PublishedJson), publishedJson is null ? null : JsonNode.Parse(publishedJson));
            layout.Name = name; layout.DocumentJson = JsonSerializer.Serialize(draft, ScreenJson);
            layout.Revision = entry.Action == "update" ? layout.Revision + 1 : 1; layout.UpdatedTs = Now; layout.UpdatedBy = UserName;
            if (publicationChanged)
            {
                layout.PublishedName = publishedName; layout.PublishedJson = publishedJson;
                layout.PublishedRevision = published is null ? null : layout.Revision;
                layout.PublishedTs = published is null ? null : Now; layout.PublishedBy = published is null ? null : UserName;
                if (publishedJson is not null)
                    layoutVersions.Add((layout, new HmiLayoutVersion
                    {
                        Revision = layout.Revision, Name = publishedName!, DocumentJson = publishedJson,
                        PublishedTs = Now, PublishedBy = UserName, Note = "工程导入或快照恢复", Kind = "import"
                    }));
            }
        }
        await SaveAsync(ct);
        foreach (var (layout, version) in layoutVersions) { version.LayoutId = layout.Id; Db.HmiLayoutVersions.Add(version); }
        await SaveAsync(ct);
    }

}
