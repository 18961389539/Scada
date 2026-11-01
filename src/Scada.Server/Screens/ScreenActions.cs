using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Server.Auth;
using Scada.Server.Endpoints;

namespace Scada.Server.Screens;

internal static class ScreenActions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<IResult> ExecuteAsync(int id, string nodeId, ScreenCommandRequest request,
        HttpContext http, IDbContextFactory<ConfigDbContext> factory, ControlCommandService commands, MomentaryControlService momentary, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var screen = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (screen?.PublishedJson is null) return Results.NotFound();
        if (screen.PublishedRevision != request.PublishedRevision)
            return Results.Conflict(new { message = "画面已更新，请刷新后再操作" });
        var published = JsonSerializer.Deserialize<ScreenDocument>(screen.PublishedJson, Json)!;
        if (ScreenPublication.RequiresRepublish(published))
            return Results.Conflict(new { message = ScreenPublication.RepublishMessage });
        var node = published.Nodes.FirstOrDefault(x => x.Id == nodeId);
        var action = node?.Action;
        if (node?.Type == "equipment-panel" && request.Operation is null) return EndpointHelpers.BadRequest("请选择设备面板操作");
        if (request.Operation is not null)
        {
            var operation = node?.Type == "equipment-panel" ? node.Commands?.FirstOrDefault(command => command.Key == request.Operation) : null;
            if (operation?.TagId is not int operationTagId) return EndpointHelpers.BadRequest("设备面板命令不存在");
            if (request.Value is double operationValue && operationValue != operation.Value) return EndpointHelpers.BadRequest("设备面板命令值与发布配置不一致");
            action = new ScreenAction { Type = "command", TagId = operationTagId, Value = operation.Value };
        }
        if (action?.Type != "command" || action.TagId is not int tagId) return EndpointHelpers.BadRequest("图元未配置控制动作");
        if (request.RequestId == Guid.Empty) return EndpointHelpers.BadRequest("命令标识无效");
        if (request.Value is double requested && !double.IsFinite(requested)) return EndpointHelpers.BadRequest("命令值无效");
        var type = node?.Type;
        var actor = ControlEndpoints.Actor(http);
        var source = $"画面 {screen.Name} 图元 {nodeId}";
        // 点动/瞬时：按下走完整检查；释放跳过联锁（失电优先），看门狗在最长保持或脉冲结束后兜底释放。
        if (type is "jog" or "pulse")
        {
            if (request.Phase is not (null or "press" or "release" or "pulse")) return EndpointHelpers.BadRequest("点动阶段无效");
            if (type == "jog" && request.Phase == "release")
                return Results.Ok(await momentary.ReleaseAsync(request.RequestId, tagId, actor, ct));
            if (type == "pulse" && request.Phase == "release") return EndpointHelpers.BadRequest("瞬时按钮不支持手动释放");
            if (type == "jog" && request.Phase != "press") return EndpointHelpers.BadRequest("点动必须指定按下或释放阶段");
            if (request.Value is double pressValue && pressValue != action.Value) return EndpointHelpers.BadRequest("命令值与发布配置不一致");
            if (!(await http.GetAccessAsync()).CanTag(Permission.Control, tagId))
                return await Audit.ForbidAsync(http, $"画面控制：画面 #{id} 图元 {nodeId} → 测点 #{tagId}", "无权控制该区域的测点", "Tag", tagId);
            var momentaryResult = type == "jog"
                ? await momentary.PressAsync(tagId, action.Value, node!.ReleaseValue ?? 0, node.MaxHoldMs ?? 5000, actor, source, request.RequestId, request.Confirmed, ct)
                : await momentary.PulseAsync(tagId, action.Value, node!.ReleaseValue ?? 0, node.PulseMs ?? 500, actor, source, request.RequestId, request.Confirmed, ct);
            return Results.Ok(momentaryResult.Result);
        }
        if (type is "selector" or "knob")
        {
            if (request.Value is not double selected || (node!.States ?? []).All(position => position.Value != selected))
                return EndpointHelpers.BadRequest("请选择发布配置中的开关位置");
        }
        else if (type is not ("switch" or "slider" or "spinner" or "input" or "reset") && request.Value is double fixedValue && fixedValue != action.Value)
            return EndpointHelpers.BadRequest("命令值与发布配置不一致");
        if (type == "input" && (request.Value is not double inputValue || inputValue < node!.Min || inputValue > node.Max)) return EndpointHelpers.BadRequest("请输入图元量程内的数值");
        if (!(await http.GetAccessAsync()).CanTag(Permission.Control, tagId))
            return await Audit.ForbidAsync(http, $"画面控制：画面 #{id} 图元 {nodeId} → 测点 #{tagId}", "无权控制该区域的测点", "Tag", tagId);
        // 运行画面控制与实时监视走同一控制闭环：检查、确认、双人复核、回读与反馈、审计。
        // 交互控件（开关/滑块/步进/选择开关）可在请求里带运行期取值，仍受控制闭环的限幅与许可约束。
        var result = await commands.SubmitAsync(new ControlSubmission(tagId, request.Value ?? action.Value, actor, ControlOrigin.Screen,
            source, request.Confirmed, request.RequestId), ct);
        return Results.Ok(result);
    }
}
