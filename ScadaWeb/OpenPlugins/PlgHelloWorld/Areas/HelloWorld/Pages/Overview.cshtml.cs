using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Scada.Web.Authorization;
using Scada.Web.Services;
using Scada.Data.Models;
using System.Globalization;
using System.Text;

namespace Scada.Web.Plugins.PlgHelloWorld.Areas.HelloWorld.Pages;

[Authorize(Policy = PolicyName.RequireViewAll)]
public sealed class OverviewModel(IWebContext context, IUserContext userContext, IClientAccessor clientAccessor) : PageModel
{
    public bool CanControl => userContext.Rights.ControlAll;
    public bool CanEdit => userContext.Rights.Full;
    public string Username => userContext.UserEntity.Name;
    public string EditorUrl => Url.Content("~/MimicEditor/MimicOpen") + "?fileName=" +
        Uri.EscapeDataString(Path.Combine(context.AppDirs.InstanceDir, "Views", "HelloWorld", "Process.mim"));

    public IActionResult OnGet()
    {
        // The learning plugin is meaningful only with its matching configuration database.
        return context.ConfigDatabase.CnlTable.GetItem(201)?.Code == "HelloWorld.201"
            ? Page() : NotFound();
    }

    public IActionResult OnGetExport(int cnlNum, DateTime startTime, DateTime endTime)
    {
        if (context.ConfigDatabase.CnlTable.GetItem(201)?.Code != "HelloWorld.201")
            return NotFound();
        if (!ModelState.IsValid || cnlNum is not (101 or 201 or 202 or 301 or 403) ||
            startTime == default || endTime <= startTime || endTime - startTime > TimeSpan.FromDays(1))
            return BadRequest("请选择演示通道和不超过 24 小时的有效时间范围。");

        var trend = clientAccessor.ScadaClient.GetTrends(1,
            new TimeRange(startTime.ToUniversalTime(), endTime.ToUniversalTime(), true), [cnlNum]);
        string name = context.ConfigDatabase.CnlTable.GetItem(cnlNum).Name;
        // Keep configuration text from being interpreted as a spreadsheet formula.
        if (name.Length > 0 && "=+-@".Contains(name[0])) name = "'" + name;
        name = name.Replace("\"", "\"\"");
        var csv = new StringBuilder("\uFEFFUTC时间,通道,名称,数值,状态\r\n");
        for (int i = 0; i < trend.Timestamps.Count; i++)
        {
            var point = trend.Trends[0][i];
            csv.Append(trend.Timestamps[i].ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(cnlNum).Append(",\"").Append(name).Append("\",")
                .Append(point.Stat > 0 && double.IsFinite(point.Val) ? point.Val.ToString("R", CultureInfo.InvariantCulture) : "")
                .Append(',').Append(point.Stat).Append("\r\n");
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"HelloWorld-channel-{cnlNum}.csv");
    }
}
