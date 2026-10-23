using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scada.Data.Entities;
using Scada.Data.Models;
using Scada.Protocol;
using Scada.Web.Services;
using Scada.Web.Users;

namespace Scada.Web.Plugins.PlgMimBasicComp.Code;

public sealed class OperationsEngine(IWebContext web, OperationsStore store)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    public static bool Good(int status) => status is 1 or 11 or 12 or 13 or 14 or 15;
    public void Check(ParameterValue value, UserRights rights)
    {
        if (!web.AppConfig.GeneralOptions.EnableCommands) throw new UnauthorizedAccessException("命令已禁用");
        var output = web.ConfigDatabase.CnlTable.GetItem(value.Channel);
        var feedback = web.ConfigDatabase.CnlTable.GetItem(value.FeedbackChannel > 0 ? value.FeedbackChannel : value.Channel);
        if (output == null || !output.Active || !output.IsOutput() || !rights.GetRightByObj(output.ObjNum).Control ||
            feedback == null || !feedback.Active || !rights.GetRightByObj(feedback.ObjNum).View)
            throw new UnauthorizedAccessException("输出或反馈通道不可用，或没有相应权限");
    }
    public async Task<List<OperationEntry>> Execute(string action, List<ParameterValue> values, User user,
        UserRights rights, IClientAccessor accessor, CancellationToken cancellation = default, Action beforeSend = null)
    {
        OperationsStore.ValidateParameters(values);
        foreach (var value in values) Check(value, rights); // Validate the whole group before any command.
        if (!await serial.WaitAsync(0, cancellation)) throw new InvalidOperationException("另一组参数正在执行，请等待结果");
        List<OperationEntry> results = [];
        try
        {
            foreach (var value in values)
            {
                Check(value, rights);
                var entry = new OperationEntry {UserId = user.UserID, User = user.Name, Action = action, Channel = value.Channel, Value = value.Value};
                bool dispatched = false;
                try
                {
                    var before = accessor.ScadaClient.GetCurrentData([value.Channel], false, out _)[0];
                    entry.Before = Good(before.Stat) && double.IsFinite(before.Val) ? before.Val : null;
                    // Persist intent before sending: an exception after dispatch is an unknown result, never auto-retried.
                    entry.Result = "准备发送";
                    store.Record(entry);
                    beforeSend?.Invoke();
                    dispatched = true;
                    var accepted = accessor.ScadaClient.SendCommand(new TeleCommand {UserID = user.UserID, CnlNum = value.Channel, CmdVal = value.Value}, WriteCommandFlags.Default);
                    if (!accepted.IsSuccessful) entry.Result = "发送失败：" + accepted.ErrorMessage;
                    else
                    {
                        entry.Result = "反馈超时，执行结果未知";
                        var deadline = DateTime.UtcNow.AddSeconds(8);
                        while (DateTime.UtcNow < deadline)
                        {
                            await Task.Delay(400, cancellation);
                            var point = accessor.ScadaClient.GetCurrentData([value.FeedbackChannel > 0 ? value.FeedbackChannel : value.Channel], false, out _)[0];
                            if (Good(point.Stat) && double.IsFinite(point.Val) && Math.Abs(point.Val - value.Value) <= value.Tolerance)
                            { entry.Result = "回读已确认"; break; }
                        }
                    }
                }
                catch (Exception ex) { entry.Result = (dispatched ? "执行结果未知：" : "未发送：") + ex.Message; }
                store.Update(s => { s.Records.RemoveAll(r => r.Id == entry.Id); s.Records.Add(entry); });
                results.Add(entry);
                if (entry.Result != "回读已确认") break; // Partial group result is visible; do not pretend it was atomic.
            }
            return results;
        }
        finally { serial.Release(); }
    }
}

public sealed class OperationsWorker(IWebContext web, OperationsStore store, OperationsEngine engine, IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!web.IsReady) continue;
            foreach (var due in store.Read().Schedules.Where(s => s.Enabled && s.NextRunUtc <= DateTimeOffset.UtcNow))
            {
                // Claim durably before dispatch. A restart cannot execute the same occurrence twice.
                var claimed = false;
                store.Update(s => {
                    var current = s.Schedules.Find(x => x.Id == due.Id);
                    if (current?.Enabled == true && current.Version == due.Version && current.NextRunUtc == due.NextRunUtc)
                    { current.Enabled = false; current.LastResult = "正在执行，重启后不自动重发"; claimed = true; }
                });
                if (!claimed) continue;
                string result;
                try
                {
                    if (DateTimeOffset.UtcNow - due.NextRunUtc > TimeSpan.FromMinutes(1)) throw new InvalidOperationException("已错过执行时间，本次跳过");
                    var user = web.ConfigDatabase.UserTable.GetItem(due.OwnerUserId);
                    if (user?.Enabled != true || user.RoleID != Scada.Data.Const.RoleID.Administrator) throw new UnauthorizedAccessException("计划所有者已停用或不再是管理员");
                    var rights = new UserRights(); rights.Init(web.ConfigDatabase.RightMatrix, user.RoleID);
                    using var scope = scopes.CreateScope();
                    var rows = await engine.Execute("计划：" + due.Name, [due.Command], user, rights, scope.ServiceProvider.GetRequiredService<IClientAccessor>(), stoppingToken);
                    result = rows.Last().Result;
                }
                catch (Exception ex) { result = ex.Message; }
                store.Update(s => {
                    var current = s.Schedules.Find(x => x.Id == due.Id);
                    // Do not overwrite an edit made while this occurrence was executing.
                    if (current == null || current.Version != due.Version || current.NextRunUtc != due.NextRunUtc) return;
                    current.LastResult = result;
                    var next = OperationsStore.NextOccurrence(due, DateTimeOffset.UtcNow);
                    if (next.HasValue) { current.NextRunUtc = next.Value; current.Enabled = true; }
                });
            }
        }
    }
}
