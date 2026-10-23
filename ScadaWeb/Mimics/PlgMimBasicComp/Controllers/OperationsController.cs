using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Scada.Data.Entities;
using Scada.Data.Models;
using Scada.Data.Tables;
using Scada.Web.Plugins.PlgMimBasicComp.Code;
using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgMimBasicComp.Controllers;

[ApiController, Authorize, Route("Api/Operations/[action]")]
public sealed class OperationsController(IWebContext web, IUserContext user, IClientAccessor accessor,
    OperationsStore store, OperationsEngine engine) : ControllerBase
{
    private IActionResult Read(Func<object> action)
    {
        try { return Ok(new {ok = true, data = action()}); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) { return BadRequest(new {ok = false, msg = ex.Message}); }
    }
    private Cnl Channel(int number, bool control = false)
    {
        var c = web.ConfigDatabase.CnlTable.GetItem(number);
        if (c == null || !c.Active || !user.Rights.GetRightByObj(c.ObjNum).View || (control && (!c.IsOutput() || !user.Rights.GetRightByObj(c.ObjNum).Control)))
            throw new UnauthorizedAccessException();
        return c;
    }
    private void Admin() { if (!user.Rights.Full) throw new UnauthorizedAccessException(); }
    private int[] Numbers(string text) => string.IsNullOrWhiteSpace(text) ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Distinct().ToArray();
    private bool Visible(SavedRecipe recipe) => recipe.Parameters.All(p => web.ConfigDatabase.CnlTable.GetItem(p.Channel) is Cnl c && user.Rights.GetRightByObj(c.ObjNum).View);
    private void Validate(ParameterValue p) { OperationsStore.ValidateParameters([p]); engine.Check(p, user.Rights); }
    private void ConfigurationAudit(string action, string result) => store.Record(new() {UserId = user.UserEntity.UserID, User = user.UserEntity.Name, Action = action, Result = result});

    [HttpGet] public IActionResult Views() => Read(() => web.ConfigDatabase.SortedViews
        .Where(v => !v.Hidden && v.ViewTypeID.HasValue && !string.IsNullOrEmpty(v.Path) && !v.Path.EndsWith('\\') && !v.Path.EndsWith('/') && user.Rights.GetRightByView(v).View)
        .Select(v => new {id = v.ViewID, title = v.Title, url = Url.Content("~/View/" + v.ViewID)}).ToArray());
    [HttpGet] public IActionResult Channels() => Read(() => new {
        admin = user.Rights.Full,
        channels = web.ConfigDatabase.CnlTable.Items.Values.Where(c => c.Active && user.Rights.GetRightByObj(c.ObjNum).View)
            .OrderBy(c => c.CnlNum).Select(c => new {number = c.CnlNum, name = c.Name,
                unit = c.UnitID.HasValue ? web.ConfigDatabase.UnitTable.GetItem(c.UnitID.Value)?.Name ?? "" : "",
                writable = c.IsOutput() && user.Rights.GetRightByObj(c.ObjNum).Control, device = c.DeviceNum}).ToArray()
    });

    [HttpGet] public IActionResult ActiveAlarms(string channels = "", int archiveBit = 4) => Read(() => {
        var numbers = Numbers(channels);
        if (numbers.Length == 0) numbers = web.ConfigDatabase.CnlTable.Items.Values.Where(c => c.Active && user.Rights.GetRightByObj(c.ObjNum).View).Select(c => c.CnlNum).ToArray();
        if (numbers.Length > 2000) throw new ArgumentException("请限定到最多 2000 个报警通道");
        var entities = numbers.Select(n => Channel(n)).ToArray();
        var values = accessor.ScadaClient.GetCurrentData(numbers, false, out _);
        var active = entities.Select((c, i) => (channel: c, data: values[i])).Where(p => p.data.Stat is 11 or 12 or 14 or 15).ToArray();
        if (active.Length == 0) return new {rows = Array.Empty<object>(), unavailable = values.Count(v => !OperationsEngine.Good(v.Stat))};
        var filter = new DataFilter(typeof(Event)) {Limit = 5000, OriginBegin = false};
        filter.AddCondition("CnlNum", FilterOperator.In, active.Select(p => p.channel.CnlNum).ToList());
        var events = accessor.ScadaClient.GetEvents(archiveBit, new TimeRange(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, true), filter, false, out _);
        var rows = active.Select(p => {
            var latest = events.Where(e => e.CnlNum == p.channel.CnlNum && e.CnlStat is 1 or 11 or 12 or 13 or 14 or 15).OrderByDescending(e => e.Timestamp).FirstOrDefault();
            if (latest?.CnlStat != p.data.Stat) latest = null;
            var view = web.ConfigDatabase.SortedViews.FirstOrDefault(v => !v.Hidden && v.ViewTypeID.HasValue && v.ObjNum == p.channel.ObjNum && user.Rights.GetRightByView(v).View);
            return (object)new {channel = p.channel.CnlNum, name = p.channel.Name, value = double.IsFinite(p.data.Val) ? (double?)p.data.Val : null,
                unit = p.channel.UnitID.HasValue ? web.ConfigDatabase.UnitTable.GetItem(p.channel.UnitID.Value)?.Name ?? "" : "",
                status = p.data.Stat, eventId = latest?.EventID.ToString(), ack = latest?.Ack,
                canAck = latest?.AckRequired == true && !latest.Ack && user.Rights.GetRightByObj(p.channel.ObjNum).Control,
                viewId = view?.ViewID, time = latest?.Timestamp};
        }).ToArray();
        return new {rows, unavailable = values.Count(v => !OperationsEngine.Good(v.Stat))};
    });

    [HttpGet] public IActionResult Interlocks(string channels = "", string expectedValues = "", int permitChannel = 0) => Read(() => {
        var numbers = Numbers(channels);
        if (numbers.Length > 64) throw new ArgumentException("最多读取 64 个联锁条件");
        var expected = string.IsNullOrWhiteSpace(expectedValues) ? Enumerable.Repeat(1.0, numbers.Length).ToArray() :
            expectedValues.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (expected.Length != numbers.Length || expected.Any(v => !double.IsFinite(v))) throw new ArgumentException("条件通道与期望值数量必须一致");
        var entities = numbers.Select(n => Channel(n)).ToArray();
        if (permitChannel > 0) Channel(permitChannel);
        var requested = numbers.Concat(permitChannel > 0 ? [permitChannel] : Array.Empty<int>()).ToArray();
        var values = requested.Length > 0 ? accessor.ScadaClient.GetCurrentData(requested, false, out _) : [];
        var rows = entities.Select((c, i) => new {channel = c.CnlNum, name = c.Name, expected = expected[i],
            value = double.IsFinite(values[i].Val) ? (double?)values[i].Val : null, status = values[i].Stat,
            matched = OperationsEngine.Good(values[i].Stat) && double.IsFinite(values[i].Val) ? (bool?)(Math.Abs(values[i].Val - expected[i]) < 1e-6) : null}).ToArray();
        var permit = permitChannel > 0 ? values[^1] : CnlData.Empty;
        return new {rows, permitChannel, permitted = permitChannel > 0 && OperationsEngine.Good(permit.Stat) && double.IsFinite(permit.Val) ? (bool?)(permit.Val == 1) : null};
    });

    private DiagnosticPolicy Policy(string id)
    {
        var policy = DiagnosticPolicy.Load(web.AppDirs.StorageDir).SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("未配置此故障复位策略");
        Channel(policy.FaultChannel);
        if (policy.Conditions.Count > 64 || policy.ResetAllowedCodes.Count == 0 || policy.ResetAllowedCodes.Any(c => c <= 0)) throw new ArgumentException("服务端复位策略无效");
        foreach (var condition in policy.Conditions) {Channel(condition.Channel); if (!double.IsFinite(condition.Expected)) throw new ArgumentException("联锁期望值无效");}
        return policy;
    }
    [HttpGet] public IActionResult Diagnostics(int channel = 0, string policyId = "") => Read(() => {
        DiagnosticPolicy policy = string.IsNullOrWhiteSpace(policyId) ? null : Policy(policyId);
        if (policy != null && channel != policy.FaultChannel) throw new ArgumentException("故障通道与服务端策略不一致");
        if (channel <= 0) return new {bound = false};
        var entity = Channel(channel);
        var value = accessor.ScadaClient.GetCurrentData([channel], false, out _)[0];
        var valid = OperationsEngine.Good(value.Stat) && double.IsFinite(value.Val) && value.Val >= 0 && value.Val <= int.MaxValue && value.Val == Math.Truncate(value.Val);
        bool canReset = false;
        if (policy != null) {
            try {engine.Check(policy.Reset, user.Rights); canReset = valid && policy.Permitted(value, policy.Conditions.Count > 0 ? accessor.ScadaClient.GetCurrentData(policy.Conditions.Select(c => c.Channel).ToArray(), false, out _) : []);}
            catch (UnauthorizedAccessException) { /* Read-only users can still diagnose faults. */ }
        }
        return new {bound = true, channel, name = entity.Name, valid, code = valid ? (int?)value.Val : null, status = value.Stat, canReset, policyConfigured = policy != null};
    });
    [HttpPost, ValidateAntiForgeryToken] public async Task<IActionResult> ResetFault([FromBody] IdRequest request)
    {
        try {
            var policy = Policy(request.Id);
            OperationsStore.ValidateParameters([policy.Reset]); engine.Check(policy.Reset, user.Rights);
            void Guard() {
                var data = accessor.ScadaClient.GetCurrentData(new[] {policy.FaultChannel}.Concat(policy.Conditions.Select(c => c.Channel)).ToArray(), false, out _);
                if (!policy.Permitted(data[0], data.Skip(1).ToArray())) throw new InvalidOperationException("故障代码不允许复位、联锁未满足或数据无效；未下发复位命令");
            }
            Guard();
            var rows = await engine.Execute("故障复位：" + policy.Id, [policy.Reset], user.UserEntity, user.Rights, accessor, HttpContext.RequestAborted, Guard);
            return Ok(new {ok = true, data = new {rows, confirmed = rows.All(r => r.Result == "回读已确认")}});
        } catch (UnauthorizedAccessException) {return Forbid();}
        catch (Exception ex) {ConfigurationAudit("故障复位拒绝", ex.Message); return BadRequest(new {ok = false, msg = ex.Message});}
    }

    [HttpGet] public IActionResult AlarmStats(string channels = "", int archiveBit = 4, DateTime? start = null, DateTime? end = null) => Read(() => {
        var to = end?.ToUniversalTime() ?? DateTime.UtcNow;
        var from = start?.ToUniversalTime() ?? to.AddDays(-1);
        if (to <= from || to - from > TimeSpan.FromDays(7) || to > DateTime.UtcNow.AddMinutes(1)) throw new ArgumentException("请选择有效的历史时间范围，最长 7 天");
        var numbers = Numbers(channels);
        if (numbers.Length == 0) numbers = web.ConfigDatabase.CnlTable.Items.Values.Where(c => c.Active && user.Rights.GetRightByObj(c.ObjNum).View).Select(c => c.CnlNum).Take(2001).ToArray();
        if (numbers.Length > 2000) throw new ArgumentException("最多统计 2000 个通道");
        var entities = numbers.Select(n => Channel(n)).ToArray();
        if (numbers.Length == 0) return new {rows = Array.Empty<object>(), truncated = false};
        var filter = new DataFilter(typeof(Event)) {Limit = 20001, OriginBegin = false};
        filter.AddCondition("CnlNum", FilterOperator.In, numbers.ToList());
        var events = accessor.ScadaClient.GetEvents(archiveBit, new TimeRange(from.AddDays(-7), to, true), filter, false, out _);
        var truncated = events.Count >= 20001;
        var grouped = events.ToLookup(e => e.CnlNum);
        var rows = entities.Select(c => {
            var result = AlarmStatistics.Calculate(c.CnlNum, grouped[c.CnlNum], from, to);
            return new {channel = c.CnlNum, name = c.Name, result.Occurrences, result.Recoveries, result.LevelChanges, result.Chatter,
                alarmSeconds = truncated ? (double?)null : result.AlarmSeconds, knownSeconds = truncated ? (double?)null : result.KnownSeconds,
                result.ActiveAtEnd};
        }).OrderByDescending(r => r.Occurrences).ThenBy(r => r.channel).ToArray();
        return new {rows, from, to, truncated, eventLimit = 20000, seedDays = 7, chatterSeconds = 60};
    });

    [HttpGet] public IActionResult Recipes() => Read(() => store.Read().Recipes.Where(Visible).ToArray());
    [HttpPost, ValidateAntiForgeryToken] public IActionResult SaveRecipe([FromBody] SavedRecipe recipe) => Read(() => {
        Admin();
        if (string.IsNullOrWhiteSpace(recipe.Name) || recipe.Name.Length > 80) throw new ArgumentException("配方名称需要 1–80 个字符");
        OperationsStore.ValidateParameters(recipe.Parameters);
        foreach (var p in recipe.Parameters) Validate(p);
        recipe.Id = string.IsNullOrEmpty(recipe.Id) ? Guid.NewGuid().ToString("N") : Guid.Parse(recipe.Id).ToString("N");
        store.Update(s => { if (s.Recipes.Count >= 100 && !s.Recipes.Any(r => r.Id == recipe.Id)) throw new ArgumentException("最多保存 100 组配方"); s.Recipes.RemoveAll(r => r.Id == recipe.Id); s.Recipes.Add(recipe); });
        ConfigurationAudit("保存配方：" + recipe.Name, "已保存"); return recipe;
    });
    [HttpPost, ValidateAntiForgeryToken] public IActionResult DeleteRecipe([FromBody] IdRequest request) => Read(() => {
        Admin(); store.Update(s => s.Recipes.RemoveAll(r => r.Id == request.Id)); ConfigurationAudit("删除配方", request.Id); return new {deleted = true};
    });
    [HttpPost, ValidateAntiForgeryToken] public async Task<IActionResult> RunRecipe([FromBody] IdRequest request)
    {
        try {
            var recipe = store.Read().Recipes.Find(r => r.Id == request.Id) ?? throw new ArgumentException("配方不存在");
            var rows = await engine.Execute("配方：" + recipe.Name, recipe.Parameters, user.UserEntity, user.Rights, accessor, HttpContext.RequestAborted);
            return Ok(new {ok = true, data = new {completed = rows.Count == recipe.Parameters.Count && rows.All(r => r.Result == "回读已确认"), rows}});
        } catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) { return BadRequest(new {ok = false, msg = ex.Message}); }
    }
    [HttpGet] public IActionResult Schedules() => Read(() => store.Read().Schedules.Where(s => web.ConfigDatabase.CnlTable.GetItem(s.Command.Channel) is Cnl c && user.Rights.GetRightByObj(c.ObjNum).View).ToArray());
    [HttpPost, ValidateAntiForgeryToken] public IActionResult SaveSchedule([FromBody] SavedSchedule schedule) => Read(() => {
        Admin(); Validate(schedule.Command);
        if (string.IsNullOrWhiteSpace(schedule.Name) || schedule.Name.Length > 80) throw new ArgumentException("计划名称需要 1–80 个字符");
        if (schedule.Enabled && schedule.NextRunUtc <= DateTimeOffset.UtcNow.AddSeconds(3)) throw new ArgumentException("启用的计划必须至少在 3 秒后执行");
        var days = Numbers(schedule.RepeatDays);
        if (days.Any(d => d is < 0 or > 6)) throw new ArgumentException("星期必须是 0–6，0 表示周日");
        schedule.RepeatDays = string.Join(',', days);
        schedule.TimeZoneId = string.IsNullOrWhiteSpace(schedule.TimeZoneId) ? user.TimeZone.Id : TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId).Id;
        if (days.Length > 0) schedule.NextRunUtc = OperationsStore.NextOccurrence(schedule, DateTimeOffset.UtcNow.AddSeconds(3)) ?? throw new ArgumentException("无法计算下次执行时间");
        schedule.OwnerUserId = user.UserEntity.UserID;
        schedule.Version = Guid.NewGuid().ToString("N");
        schedule.Id = string.IsNullOrEmpty(schedule.Id) ? Guid.NewGuid().ToString("N") : Guid.Parse(schedule.Id).ToString("N");
        schedule.LastResult = schedule.Enabled ? "等待执行" : "已停用";
        store.Update(s => { if (s.Schedules.Count >= 100 && !s.Schedules.Any(r => r.Id == schedule.Id)) throw new ArgumentException("最多保存 100 个计划"); s.Schedules.RemoveAll(r => r.Id == schedule.Id); s.Schedules.Add(schedule); });
        ConfigurationAudit("保存计划：" + schedule.Name, schedule.LastResult); return schedule;
    });
    [HttpPost, ValidateAntiForgeryToken] public IActionResult DeleteSchedule([FromBody] IdRequest request) => Read(() => {
        Admin(); store.Update(s => s.Schedules.RemoveAll(r => r.Id == request.Id)); ConfigurationAudit("删除计划", request.Id); return new {deleted = true};
    });
    [HttpGet] public IActionResult Log(int channel = 0, string query = "") => Read(() => store.Read().Records.Where(r =>
        (r.Channel > 0 ? web.ConfigDatabase.CnlTable.GetItem(r.Channel) is Cnl c && user.Rights.GetRightByObj(c.ObjNum).View : user.Rights.Full || r.UserId == user.UserEntity.UserID) &&
        (channel == 0 || r.Channel == channel) && (r.User + r.Action + r.Result).Contains(query ?? "", StringComparison.OrdinalIgnoreCase)).TakeLast(200).Reverse().ToArray());

    [HttpGet] public IActionResult Energy(int channel, int archiveBit = 1, int minutes = 1440) => Read(() => {
        var cnl = Channel(channel);
        string unit = cnl.UnitID.HasValue ? web.ConfigDatabase.UnitTable.GetItem(cnl.UnitID.Value)?.Name : "";
        if (unit is not ("kW" or "W")) throw new ArgumentException("功率积分输入通道必须使用 kW 或 W");
        if (minutes is < 1 or > 10080) throw new ArgumentException("积分时段必须为 1–10080 分钟");
        var end = DateTime.UtcNow;
        var trend = accessor.ScadaClient.GetTrends(archiveBit, new TimeRange(end.AddMinutes(-minutes), end, true), [channel]);
        var integral = ArchiveEnergy.Calculate(trend, unit == "W" ? 1 : 1000);
        return new {integral.Kwh, integral.CoveredMinutes, requestedMinutes = minutes, integral.SkippedIntervals, integral.Samples};
    });
}
public sealed class IdRequest { public string Id { get; set; } = ""; }
