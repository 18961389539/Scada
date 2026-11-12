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

/// <summary>
/// 各段落的公共基类：把上下文里的字段与判据转发成短名字，
/// 让从 <c>ProjectApplier</c> 搬出来的方法体可以逐字保留（便于用 diff 核对语义没变）。
/// </summary>
public abstract class ProjectSection(ProjectApplyContext context)
{
    protected ProjectApplyContext Ctx { get; } = context;

    protected static JsonSerializerOptions ScreenJson => ProjectApplyContext.ScreenJson;

    protected ConfigDbContext Db => Ctx.Db;
    protected CalcJobConfiguration Jobs => Ctx.Jobs;
    protected AccessContext? Access => Ctx.Access;
    protected int? UserId => Ctx.UserId;
    protected string UserName => Ctx.UserName;
    protected Scada.Core.Options.ScadaSettings? Settings => Ctx.Settings;
    protected Scada.Core.Interfaces.IProtocolDriverAvailability? Drivers => Ctx.Drivers;
    protected RuntimeDbContext Runtime => Ctx.Runtime;
    protected long Now => Ctx.Now;

    protected List<string> Errors => Ctx.Errors;
    protected List<string> Forbidden => Ctx.Forbidden;
    protected List<(int TagId, string Reason)> PendingAlarmCloses => Ctx.PendingAlarmCloses;

    protected Dictionary<string, int> AreaIdsByName => Ctx.AreaIdsByName;
    protected Dictionary<string, int> DeviceIdsByName => Ctx.DeviceIdsByName;
    protected Dictionary<string, int> TagIdsByKey => Ctx.TagIdsByKey;
    protected Dictionary<string, int> JobIdsByName => Ctx.JobIdsByName;
    protected Dictionary<string, int> ScreenIdsByName => Ctx.ScreenIdsByName;
    protected Dictionary<string, int> GatewayIdsByName => Ctx.GatewayIdsByName;
    protected Dictionary<string, int> ChannelIdsByName => Ctx.ChannelIdsByName;

    protected static IEnumerable<PlanEntry> Upserts(IReadOnlyList<PlanEntry> work, string section) =>
        work.Where(x => x.Section == section && x.Action is "add" or "update" or "rename");

    protected static IEnumerable<PlanEntry> Deletes(IReadOnlyList<PlanEntry> work, string section) =>
        work.Where(x => x.Section == section && x.Action == "delete");

    protected Task SaveAsync(CancellationToken ct) => Ctx.SaveAsync(ct);
    protected Task LoadAsync(CancellationToken ct) => Ctx.LoadAsync(ct);
    protected void Error(PlanEntry entry, string message) => Ctx.Error(entry, message);
    protected void Need(Permission permission, PlanEntry entry, IEnumerable<int> areas) => Ctx.Need(permission, entry, areas);
    protected void NeedGlobal(Permission permission, PlanEntry entry) => Ctx.NeedGlobal(permission, entry);
    protected JsonObject? Resolve(PlanEntry entry, JsonObject source, bool allowMissingScreenRefs = false) => Ctx.Resolve(entry, source, allowMissingScreenRefs);
    protected string? ResolveExpression(PlanEntry entry, string expression) => Ctx.ResolveExpression(entry, expression);
    protected Task<Tag?> FindTagAsync(string key, CancellationToken ct) => Ctx.FindTagAsync(key, ct);
    protected Task<int[]> AreasOfAsync(Tag tag, CancellationToken ct) => Ctx.AreasOfAsync(tag, ct);
    protected Task<int[]> TagAreasAsync(int tagId, CancellationToken ct) => Ctx.TagAreasAsync(tagId, ct);
    protected Task<bool> CanTagAsync(Permission permission, int tagId, CancellationToken ct) => Ctx.CanTagAsync(permission, tagId, ct);
    protected Task<Dictionary<string, int>> RuleKeysAsync(CancellationToken ct) => Ctx.RuleKeysAsync(ct);
}
