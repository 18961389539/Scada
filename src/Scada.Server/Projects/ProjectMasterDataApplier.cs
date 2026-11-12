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

/// <summary>区域、角色、设备、测点、脚本库。</summary>
internal sealed class ProjectMasterDataApplier(ProjectApplyContext context) : ProjectSection(context)
{
    internal async Task ApplyAreasAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "areas"))
        {
            NeedGlobal(Permission.Configure, entry);
            var incoming = entry.Data!.Deserialize<Area>(ProjectFormat.Json)!;
            var area = entry.Action == "update" ? await Db.Areas.SingleAsync(x => x.Name == entry.FinalKey, ct) : Db.Areas.Add(new Area()).Entity;
            area.Name = incoming.Name.Trim(); area.Description = incoming.Description;
            if (area.Name.Length is 0 or > 100) Error(entry, "区域名称须为 1～100 个字符");
        }
        await SaveAsync(ct); await LoadAsync(ct);
    }

    internal async Task ApplyRolesAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "roles"))
        {
            NeedGlobal(Permission.ManageUsers, entry);
            var incoming = entry.Data!.Deserialize<AccessRole>(ProjectFormat.Json)!;
            var role = entry.Action == "update" ? await Db.AccessRoles.SingleAsync(x => x.Name == entry.FinalKey, ct) : Db.AccessRoles.Add(new AccessRole()).Entity;
            role.Name = incoming.Name.Trim(); role.Description = incoming.Description; role.Permissions = incoming.Permissions;
        }
        await SaveAsync(ct);
    }

    internal async Task ApplyDevicesAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "devices"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<Device>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await Db.Devices.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            Need(Permission.Configure, entry, [incoming.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            if (ConfigurationValidator.Device(incoming, Settings, Drivers) is string error) { Error(entry, error); continue; }
            // 工程文件不携带通道定义：通道必须已存在于本机，且协议一致；端点以通道为准重新下发。
            if (incoming.ChannelId is not null && await DeviceCommandService.ApplyChannelEndpointAsync(Db, incoming, ct) is string channelError)
            { Error(entry, channelError); continue; }
            if (existing is null)
            {
                incoming.DisabledSinceTs = incoming.Enabled ? 0 : Now; Db.Devices.Add(incoming);
            }
            else
            {
                incoming.Id = existing.Id; incoming.OpcPasswordProtected = existing.OpcPasswordProtected;
                incoming.DisabledSinceTs = incoming.Enabled ? 0 : existing.Enabled ? Now : existing.DisabledSinceTs;
                Db.Entry(existing).CurrentValues.SetValues(incoming);
            }
        }
        await SaveAsync(ct); await LoadAsync(ct);
    }

    internal async Task<ExpressionMap> ApplyTagsAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        // 测点第一遍：计算测点先用占位表达式，待计算任务的派生测点生成后再写入真实表达式。
        var expressions = new Dictionary<Tag, (PlanEntry Entry, string Expression)>();
        foreach (var entry in Upserts(work, "tags"))
        {
            var raw = (JsonObject)entry.Data!.DeepClone(); var expression = raw["expression"]?.GetValue<string>() ?? ""; raw["expression"] = "";
            var data = Resolve(entry, raw); if (data is null) continue;
            var incoming = data.Deserialize<Tag>(ProjectFormat.Json)!;
            if (incoming.Kind == TagKind.Derived) { Error(entry, "派生测点由计算任务维护，不能直接导入"); continue; }
            var existing = entry.Action == "update" ? await FindTagAsync(entry.FinalKey, ct) : null;
            if (existing is not null && existing.Kind != incoming.Kind) { Error(entry, "不能修改测点来源类型，请改名导入"); continue; }
            Need(Permission.Configure, entry, [.. await AreasOfAsync(incoming, ct), .. existing is null ? [] : await AreasOfAsync(existing, ct)]);
            if (incoming.Kind == TagKind.Calculated) incoming.Expression = "0";
            var device = incoming.DeviceId is int d ? await Db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == d, ct) : null;
            if (ConfigurationValidator.Tag(incoming, device) is string error) { Error(entry, error); continue; }
            Tag target;
            if (existing is null) { incoming.CalculationRevision = 1; Db.Tags.Add(incoming); target = incoming; }
            else
            {
                var engineeringChanged = existing.Kind != TagKind.Calculated
                    && (existing.Scale != incoming.Scale || existing.Offset != incoming.Offset || existing.Deadband != incoming.Deadband);
                incoming.Id = existing.Id; incoming.CalculationRevision = existing.CalculationRevision + (existing.Kind == TagKind.Calculated || engineeringChanged ? 1 : 0);
                incoming.Expression = existing.Expression;
                Db.Entry(existing).CurrentValues.SetValues(incoming); target = existing;
            }
            if (incoming.Kind == TagKind.Calculated) expressions[target] = (entry, expression);
        }
        await SaveAsync(ct); await LoadAsync(ct);
        // 计算测点的表达式要等计算任务的派生测点生成后才能解析，交给调用方带入下一段。
        return expressions;
    }

    internal async Task ApplyScriptLibrariesAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "scriptLibraries"))
        {
            NeedGlobal(Permission.Configure, entry);
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<ScriptLibrary>(ProjectFormat.Json)!;
            if (incoming.Name.Length is 0 or > 64) { Error(entry, "脚本库名称无效"); continue; }
            if (incoming.Source.Length > 8192 || incoming.PublishedSource.Length > 8192) { Error(entry, "脚本库不能超过 8192 个字符"); continue; }
            if (incoming.PublishedRevision == 0)
            {
                incoming.PublishedSource = incoming.Source; incoming.PublishedRevision = 1;
            }
            var existing = entry.Action == "update" ? await Db.ScriptLibraries.SingleOrDefaultAsync(x => x.Name == entry.FinalKey, ct) : null;
            if (existing is null) { incoming.Id = 0; incoming.Revision = 1; incoming.UpdatedTs = Now; Db.ScriptLibraries.Add(incoming); }
            else { incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; incoming.UpdatedTs = Now; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
    }

}
