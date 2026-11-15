using Microsoft.EntityFrameworkCore;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;

namespace Scada.Server.Screens;

public static class ScreenAreaRules
{
    public static bool CanEdit(AccessContext access, int? areaId) =>
        areaId is int area ? access.Has(Permission.Configure, area) : access.HasGlobal(Permission.Configure);

    public static IEnumerable<int> TagIds(ScreenDocument? document)
    {
        if (document?.Nodes is null) yield break;
        foreach (var node in document.Nodes)
        {
            if (node is null) continue;
            if (node.TagId is int tagId) yield return tagId;
            if (node.AlarmTagId is int alarmTagId) yield return alarmTagId;
            if (node.Tags is not null) foreach (var id in node.Tags) yield return id;
            if (node.Action?.TagId is int actionTagId) yield return actionTagId;
            if (node.Animation?.TagId is int animationTagId) yield return animationTagId;
            if (node.Commands is not null) foreach (var command in node.Commands) if (command?.TagId is int commandTagId) yield return commandTagId;
        }
    }

    public static async Task<string?> MismatchAsync(ConfigDbContext db, int? areaId, ScreenDocument document, CancellationToken ct)
    {
        if (areaId is not int owned) return null;
        var ids = TagIds(document).Distinct().ToList();
        if (ids.Count == 0) return null;
        var tags = await db.Tags.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.DeviceId, x.AreaId }).ToListAsync(ct);
        var deviceIds = tags.Where(x => x.DeviceId is not null).Select(x => x.DeviceId!.Value).Distinct().ToList();
        var deviceAreas = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.AreaId, ct);
        foreach (var tag in tags)
        {
            var area = tag.DeviceId is int deviceId && deviceAreas.TryGetValue(deviceId, out var deviceArea) ? deviceArea : tag.AreaId;
            if (area != owned) return "画面绑定了其他区域的测点";
        }
        return null;
    }
}
