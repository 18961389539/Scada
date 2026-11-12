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

/// <summary>计算任务、计算表达式、报警规则。</summary>
internal sealed class ProjectCalcApplier(ProjectApplyContext context) : ProjectSection(context)
{
    internal async Task ApplyCalcJobsAsync(IReadOnlyList<PlanEntry> work, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "calcJobs"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var unit = data["unit"]?.GetValue<string>(); data.Remove("unit");
            var candidate = data.Deserialize<CalcJob>(ProjectFormat.Json)!;
            if (candidate.IsScript && !data.ContainsKey("publishedIntervalMs"))
            {
                candidate.PublishedTrigger = candidate.Trigger; candidate.PublishedIntervalMs = candidate.IntervalMs;
                candidate.PublishedChangeDeadband = candidate.ChangeDeadband; candidate.PublishedChangeCooldownMs = candidate.ChangeCooldownMs;
                candidate.PublishedAlarmMinLevel = candidate.AlarmMinLevel; candidate.PublishedAlarmOnRaise = candidate.AlarmOnRaise;
                candidate.PublishedAlarmOnAck = candidate.AlarmOnAck; candidate.PublishedAlarmOnRecover = candidate.AlarmOnRecover;
            }
            if (candidate.Kind == CalcJobKind.CSharp)
            {
                NeedGlobal(Permission.Configure, entry);
                var ids = ProjectExporter.CalcScriptIds(candidate.ReadTags).Concat(ProjectExporter.CalcScriptIds(candidate.WriteTags)).ToHashSet();
                var bindings = data["scriptTagBindings"] as JsonObject;
                var map = new Dictionary<int, int>();
                var valid = bindings is not null && bindings.Count == ids.Count;
                if (bindings is not null)
                    foreach (var binding in bindings)
                    {
                        if (!int.TryParse(binding.Key, out var originalId) || binding.Value is not JsonValue key || !key.TryGetValue<string>(out var tagKey)
                            || !TagIdsByKey.TryGetValue(tagKey, out var mappedId) || !ids.Contains(mappedId)) valid = false;
                        else map[originalId] = mappedId;
                    }
                if (!valid) { Error(entry, "C# 脚本引用的测点缺少名称绑定，或目标工程没有同名测点。"); continue; }
                candidate.Script = ScriptTagNames.RebindCalls(candidate.Script, map);
                candidate.PublishedScript = ScriptTagNames.RebindCalls(candidate.PublishedScript, map);
                candidate.PublishedLibrarySource = ScriptTagNames.RebindCalls(candidate.PublishedLibrarySource, map);
            }
            var existing = entry.Action == "update" ? await Db.CalcJobs.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            Need(Permission.Configure, entry, [candidate.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            if (existing is not null && existing.Kind != candidate.Kind) { Error(entry, "不能修改任务类型，请改名导入"); continue; }
            candidate.Id = existing?.Id ?? 0;
            if (candidate.IsScript && candidate.PublishedRevision != 0)
            {
                var published = JsonSerializer.Deserialize<CalcJob>(JsonSerializer.Serialize(candidate, ProjectFormat.Json), ProjectFormat.Json)!;
                published.Script = candidate.PublishedScript; published.ReadTags = candidate.PublishedReadTags; published.WriteTags = candidate.PublishedWriteTags;
                published.Libraries = candidate.PublishedLibraries;
                published.Trigger = candidate.PublishedTrigger; published.IntervalMs = candidate.PublishedIntervalMs;
                published.ChangeDeadband = candidate.PublishedChangeDeadband; published.ChangeCooldownMs = candidate.PublishedChangeCooldownMs;
                published.HistoryWindowSec = candidate.PublishedHistoryWindowSec;
                published.AlarmMinLevel = candidate.PublishedAlarmMinLevel; published.AlarmOnRaise = candidate.PublishedAlarmOnRaise;
                published.AlarmOnAck = candidate.PublishedAlarmOnAck; published.AlarmOnRecover = candidate.PublishedAlarmOnRecover;
                try { await Jobs.ValidateAsync(Db, published, ct); }
                catch (CalculationRequestException ex) { Error(entry, "已发布脚本未通过校验：" + ex.Message); continue; }
                catch (Scada.Engine.Calculations.CSharpScriptException ex) { Error(entry, "已发布脚本未通过校验：" + ex.Message); continue; }
            }
            IReadOnlyList<string> outputs;
            try
            {
                outputs = await Jobs.ValidateAsync(Db, candidate, ct);
                if (candidate.IsScript && candidate.PublishedRevision == 0)
                {
                    candidate.PublishedScript = candidate.Script; candidate.PublishedReadTags = candidate.ReadTags; candidate.PublishedWriteTags = candidate.WriteTags;
                    candidate.PublishedHistoryWindowSec = candidate.HistoryWindowSec; candidate.PublishedAlarmSnapshot = candidate.AlarmSnapshot; candidate.PublishedRevision = 1;
                    candidate.PublishedTrigger = candidate.Trigger; candidate.PublishedIntervalMs = candidate.IntervalMs;
                    candidate.PublishedChangeDeadband = candidate.ChangeDeadband; candidate.PublishedChangeCooldownMs = candidate.ChangeCooldownMs;
                    candidate.PublishedAlarmMinLevel = candidate.AlarmMinLevel; candidate.PublishedAlarmOnRaise = candidate.AlarmOnRaise;
                    candidate.PublishedAlarmOnAck = candidate.AlarmOnAck; candidate.PublishedAlarmOnRecover = candidate.AlarmOnRecover;
                }
            }
            catch (CalculationRequestException ex) { Error(entry, ex.Message); continue; }
            catch (Scada.Engine.Calculations.CSharpScriptException ex) { Error(entry, ex.Message); continue; }
            var writes = ProjectExporter.CalcScriptIds(candidate.WriteTags).ToArray();
            foreach (var tagId in writes)
                if (Access is not null && !await CanTagAsync(Permission.Control, tagId, ct)) Forbidden.Add($"计算任务 {entry.FinalKey}：无权控制可写测点 #{tagId}");
            CalcJob job;
            if (existing is null) { Db.CalcJobs.Add(candidate); job = candidate; job.Revision = 1; }
            else { var revision = existing.Revision + 1; Db.Entry(existing).CurrentValues.SetValues(candidate); job = existing; job.Revision = revision; }
            job.UpdatedTs = Now;
            if (job.Kind == CalcJobKind.CSharp || job.IsScript && writes.Length > 0) { job.RunAsUserId = UserId; job.RunAsUserName = UserName; }
            else { job.RunAsUserId = null; job.RunAsUserName = ""; }
            await SaveAsync(ct);
            try
            {
                foreach (var id in await CalcJobConfiguration.SyncOutputsAsync(Db, job, outputs, unit, ct))
                    PendingAlarmCloses.Add((id, "计算任务输出移除"));
            }
            catch (CalculationRequestException ex) { Error(entry, ex.Message); }
        }
        await SaveAsync(ct); await LoadAsync(ct);
    }

    internal async Task ApplyCalcExpressionsAsync(ExpressionMap expressions, CancellationToken ct)
    {
        foreach (var (tag, (entry, expression)) in expressions)
        {
            var resolved = ResolveExpression(entry, expression); if (resolved is null) continue;
            tag.Expression = resolved;
            if (ConfigurationValidator.Tag(tag) is string error) Error(entry, error);
        }
        await SaveAsync(ct);
    }

    internal async Task ApplyAlarmRulesAsync(IReadOnlyList<PlanEntry> work, Dictionary<string, int> rules, CancellationToken ct)
    {
        foreach (var entry in Upserts(work, "alarmRules"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<AlarmRule>(ProjectFormat.Json)!;
            incoming.ActiveWhen = AlarmRuleText.NormalizeActiveWhen(incoming.Type, incoming.ActiveWhen);
            var existing = entry.Action == "update" && rules.TryGetValue(entry.FinalKey, out var id) ? await Db.AlarmRules.SingleAsync(x => x.Id == id, ct) : null;
            Need(Permission.Configure, entry, [.. await TagAreasAsync(incoming.TagId, ct), .. existing is null ? [] : await TagAreasAsync(existing.TagId, ct)]);
            if (EndpointHelpers.ValidateAlarmRule(incoming) is string error) { Error(entry, error); continue; }
            if (await EndpointHelpers.ValidateAlarmRuleTargetAsync(Db, incoming, existing?.Id, ct) is string targetError) { Error(entry, targetError); continue; }
            if (incoming.Key.Length is 0 or > 64) { Error(entry, "报警规则稳定键须为 1～64 个字符"); continue; }
            if (existing is null) Db.AlarmRules.Add(incoming);
            else { incoming.Id = existing.Id; incoming.OutOfService = existing.OutOfService; incoming.OutOfServiceReason = existing.OutOfServiceReason; Db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);
    }

}
