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

/// <summary>产品/产线/班次/停机原因与信号盘。</summary>
internal sealed class ProjectProductionApplier(ProjectApplyContext context) : ProjectSection(context)
{
    internal async Task ApplyProductionAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "productionProducts"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<ProductionProduct>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await Db.ProductionProducts.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            if (incoming.AreaId is int a) Need(Permission.Configure, entry, [a, .. existing?.AreaId is int ea ? new[] { ea } : Array.Empty<int>()]);
            else NeedGlobal(Permission.Configure, entry);
            incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            if (existing is null) { incoming.Revision = 1; Db.ProductionProducts.Add(incoming); }
            else { incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
        var productIds = await Db.ProductionProducts.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        foreach (var entry in Upserts(work, "productionLines"))
        {
            var raw = (JsonObject)entry.Data!.DeepClone();
            var productRef = raw["productRef"]?.GetValue<string>(); raw.Remove("productRef");
            var data = Resolve(entry, raw); if (data is null) continue;
            var incoming = data.Deserialize<ProductionLine>(ProjectFormat.Json)!;
            if (productRef is { Length: > 0 } && productIds.TryGetValue(productRef, out var pid)) incoming.ProductId = pid;
            else if (productRef is { Length: > 0 }) { Error(entry, $"引用的产品 {productRef} 不存在"); continue; }
            var existing = entry.Action == "update" ? await Db.ProductionLines.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            Need(Permission.Configure, entry, [incoming.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            incoming.StateMapJson ??= "{}"; incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            if (existing is null) { incoming.Revision = 1; Db.ProductionLines.Add(incoming); }
            else { incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
        var areaByName = await Db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        foreach (var entry in Upserts(work, "productionShifts"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<ProductionShift>(ProjectFormat.Json)!;
            ProductionShift? existing = null;
            if (entry.Action == "update")
            {
                var parts = entry.FinalKey.Split('/', 2);
                if (parts.Length == 2 && areaByName.TryGetValue(parts[0], out var aid))
                    existing = await Db.ProductionShifts.SingleOrDefaultAsync(x => x.AreaId == aid && x.Name == parts[1], ct);
                existing ??= await Db.ProductionShifts.SingleOrDefaultAsync(x => x.Name == ProjectFormat.Name(entry.Data!), ct);
            }
            Need(Permission.Configure, entry, [incoming.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            if (existing is null) { incoming.Revision = 1; Db.ProductionShifts.Add(incoming); }
            else { incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
        foreach (var entry in Upserts(work, "downtimeReasons"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            if (data["category"]?.GetValue<string>() is string cat && Enum.TryParse<OeeLossCategory>(cat, true, out var parsed))
                data["category"] = (int)parsed;
            var incoming = data.Deserialize<DowntimeReason>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await Db.DowntimeReasons.SingleAsync(x => x.Code == entry.FinalKey, ct) : null;
            if (incoming.AreaId is int a) Need(Permission.Configure, entry, [a, .. existing?.AreaId is int ea ? new[] { ea } : Array.Empty<int>()]);
            else NeedGlobal(Permission.Configure, entry);
            incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            if (existing is null) { incoming.Revision = 1; Db.DowntimeReasons.Add(incoming); }
            else { incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
    }

    internal async Task ApplyAnnunciatorsAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "annunciatorPanels"))
        {
            var raw = (JsonObject)entry.Data!.DeepClone();
            var outputsNode = raw["outputs"]; raw.Remove("outputs");
            var levelsNode = raw["levels"]; raw.Remove("levels");
            var data = Resolve(entry, raw); if (data is null) continue;
            if (data["latchMode"]?.GetValue<string>() is string lm && Enum.TryParse<AnnunciatorLatchMode>(lm, true, out var latch)) data["latchMode"] = (int)latch;
            var incoming = data.Deserialize<AnnunciatorPanel>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await Db.AnnunciatorPanels.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            if (incoming.AreaId is int a) Need(Permission.Configure, entry, [a, .. existing?.AreaId is int ea ? new[] { ea } : Array.Empty<int>()]);
            else NeedGlobal(Permission.Configure, entry);
            incoming.UpdatedTs = Now; incoming.UpdatedBy = UserName;
            if (existing is null) { incoming.Revision = 1; Db.AnnunciatorPanels.Add(incoming); await SaveAsync(ct); }
            else
            {
                incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1;
                Db.Entry(existing).CurrentValues.SetValues(incoming);
                Db.AnnunciatorOutputs.RemoveRange(Db.AnnunciatorOutputs.Where(x => x.PanelId == existing.Id));
                Db.AnnunciatorLevelMaps.RemoveRange(Db.AnnunciatorLevelMaps.Where(x => x.PanelId == existing.Id));
                await SaveAsync(ct);
            }
            var panelId = existing?.Id ?? incoming.Id;
            if (outputsNode is JsonArray outs)
            {
                foreach (var item in outs.OfType<JsonObject>())
                {
                    var o = Resolve(entry, item); if (o is null) continue;
                    if (o["kind"]?.GetValue<string>() is string k && Enum.TryParse<AnnunciatorChannelKind>(k, true, out var kind)) o["kind"] = (int)kind;
                    var output = o.Deserialize<AnnunciatorOutput>(ProjectFormat.Json)!;
                    output.PanelId = panelId;
                    Db.AnnunciatorOutputs.Add(output);
                }
            }
            if (levelsNode is JsonArray levels)
            {
                foreach (var item in levels.OfType<JsonObject>())
                {
                    var l = (JsonObject)item.DeepClone();
                    if (l["level"]?.GetValue<string>() is string lv && Enum.TryParse<AlarmLevel>(lv, true, out var level)) l["level"] = (int)level;
                    var map = l.Deserialize<AnnunciatorLevelMap>(ProjectFormat.Json)!;
                    map.PanelId = panelId;
                    Db.AnnunciatorLevelMaps.Add(map);
                }
            }
            await SaveAsync(ct);
        }
    }

}
