using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Scada.Web.Api;
using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgMimBasicComp.Code;

// Capture native command outcomes as well as recipe/scheduler records.
public sealed class OperationsAuditFilter(OperationsStore store, IUserContext user, IWebContext web, IClientAccessor accessor) : IActionFilter
{
    private int channel;
    private double? value;
    private double? before;
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.Request.Path.Value!.EndsWith("/Api/Main/SendCommand", StringComparison.OrdinalIgnoreCase)) return;
        var dto = context.ActionArguments.Values.FirstOrDefault();
        if (dto == null) return;
        channel = (int?)dto.GetType().GetProperty("CnlNum")?.GetValue(dto) ?? 0;
        value = (double?)dto.GetType().GetProperty("CmdVal")?.GetValue(dto);
        if (web.ConfigDatabase.CnlTable.GetItem(channel) is { } cnl && user.Rights.GetRightByObj(cnl.ObjNum).View)
            try { var data = accessor.ScadaClient.GetCurrentData([channel], false, out _)[0]; before = OperationsEngine.Good(data.Stat) && double.IsFinite(data.Val) ? data.Val : null; } catch { }
    }
    public void OnActionExecuted(ActionExecutedContext context)
    {
        if (channel <= 0) return;
        var dto = (context.Result as ObjectResult)?.Value as Dto;
        try { store.Record(new() {UserId = user.UserEntity.UserID, User = user.UserEntity.Name, Action = "原生命令", Channel = channel,
            Before = before, Value = value.HasValue && double.IsFinite(value.Value) ? value : null,
            Result = dto?.Ok == true ? "命令已发送（回读见设备面板）" : "拒绝或失败：" + (dto?.Msg ?? context.Exception?.Message ?? "未知")}); }
        catch (Exception ex) { web.Log.WriteError(ex, "记录操作日志失败"); }
    }
}
