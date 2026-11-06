using Microsoft.Extensions.DependencyInjection;
using Opc.Ua;
using Opc.Ua.Server.Fluent;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Engine.Alarms;
using UaStatus = Opc.Ua.StatusCodes;

namespace Scada.Server.External.OpcUa;

/// <summary>
/// 把报警规则挂成 OPC UA AlarmConditionType 实例，供上级系统按标准 A&amp;C 订阅与确认。
/// 状态由站内 AlarmEngine 决定，这里只做投影——不重复实现判定，避免"两套报警逻辑各说各话"。
/// 暂挂（Shelved）与抑制（Suppressed）分别映射到 ConditionType 的对应状态，上级系统能区分
/// "这条报警被人为压住了"和"它真的没发生"。
/// </summary>
internal sealed class ScadaAlarmConditionState : AlarmConditionState
{
    private readonly Func<int, string, string?, Task<bool>> _acknowledge;
    private ConditionSnapshot _last = ConditionSnapshot.Unknown;

    public ScadaAlarmConditionState(AlarmRule rule, Func<int, string, string?, Task<bool>> acknowledge)
        : base(null!)
    {
        Rule = rule;
        _acknowledge = acknowledge;
    }

    public AlarmRule Rule { get; }
    public int RuleId => Rule.Id;

    /// <summary>把站内报警状态投影到 Condition。返回 true 表示状态有变化，调用方应上报事件。</summary>
    public bool Apply(ISystemContext context, AlarmEvent? alarm)
    {
        var active = alarm is { EndTs: null };
        var acknowledged = alarm?.AckTs is not null;
        var suppressed = alarm?.Suppressed ?? false;
        var shelved = alarm?.Shelved ?? false;
        var snapshot = new ConditionSnapshot(Rule.Enabled, active, acknowledged, suppressed, shelved, Rule.Level);
        if (snapshot == _last) return false;
        // 启用状态只在真正变化时设置：SetEnableState 会重置部分内部状态，
        // 每次报警状态变化都调用它会让客户端的 Enabled 时间戳频繁抖动。
        var enabledChanged = _last.Enabled != Rule.Enabled;
        _last = snapshot;

        if (enabledChanged) SetEnableState(context, Rule.Enabled);
        SetSeverity(context, ToSeverity(Rule.Level));
        SetActiveState(context, active);
        SetAcknowledgedState(context, acknowledged);
        // 站内只有"确认"一步，没有独立的复核，确认即视为已确认。
        SetConfirmedState(context, acknowledged);
        SetSuppressedState(context, suppressed);
        SetShelvingState(context, shelved, oneShot: false, shelvingTime: 0);
        return true;
    }

    protected override ServiceResult OnAcknowledgeCalled(ISystemContext context, MethodState method, NodeId objectId,
        ByteString eventId, LocalizedText comment)
    {
        var user = context is Opc.Ua.Server.ServerSystemContext server && !string.IsNullOrWhiteSpace(server.UserIdentity?.DisplayName)
            ? server.UserIdentity!.DisplayName!
            : "opcua";
        try
        {
            // OPC UA 的方法回调是同步契约，这里必须等站内确认落库后再返回状态码，
            // 否则上级系统会认为确认成功、而站内仍是未确认。
            var ok = _acknowledge(Rule.Id, user, comment.Text).GetAwaiter().GetResult();
            return ok ? ServiceResult.Good : new ServiceResult(UaStatus.BadNothingToDo);
        }
        catch (Exception)
        {
            return new ServiceResult(UaStatus.BadUnexpectedError);
        }
    }

    private static EventSeverity ToSeverity(AlarmLevel level) => level switch
    {
        AlarmLevel.Critical => EventSeverity.High,
        AlarmLevel.Warning => EventSeverity.Medium,
        _ => EventSeverity.Low
    };

    private readonly record struct ConditionSnapshot(bool Enabled, bool Active, bool Acked, bool Suppressed, bool Shelved, AlarmLevel Level)
    {
        public static ConditionSnapshot Unknown { get; } = new(false, false, false, false, false, AlarmLevel.Info);
    }
}

internal static class AlarmConditionBinder
{
    /// <summary>在地址空间里建 Alarms 文件夹并挂 Condition，随后启动状态投影循环。</summary>
    public static void Bind(
        INodeManagerBuilder nodes,
        NodeId parentNodeId,
        IReadOnlyDictionary<int, NodeId> tagNodeIds,
        IReadOnlyList<AlarmRule> rules,
        IServiceScopeFactory scopes,
        ILogger logger,
        CancellationToken publishToken,
        int intervalMs)
    {
        var folder = nodes.AddFolder("Alarms", parentNodeId);
        var context = nodes.Context;
        var conditions = new List<ScadaAlarmConditionState>();

        foreach (var rule in rules.Where(r => r.Type != AlarmRuleType.BadQuality).OrderBy(r => r.Id))
        {
            try
            {
                if (!tagNodeIds.TryGetValue(rule.TagId, out var sourceNodeId)) continue;
                var name = string.IsNullOrWhiteSpace(rule.Key) ? $"Alarm_{rule.Id}" : Sanitize(rule.Key);
                var nodeId = new NodeId($"Alarm_{rule.Id}", nodes.DefaultNamespaceIndex);
                var condition = new ScadaAlarmConditionState(rule, (ruleId, user, note) => AcknowledgeAsync(scopes, rule, user, note));
                condition.Create(context, nodeId, new QualifiedName(name, nodes.DefaultNamespaceIndex),
                    new LocalizedText(rule.Message ?? name), false);
                // Create 会补齐必填子节点；这里写入的是实例值，不是可选结构。
                condition.SourceNode!.Value = sourceNodeId;
                condition.SourceName!.Value = rule.Key;
                condition.ConditionName!.Value = name;
                condition.InputNode!.Value = sourceNodeId;
                condition.EnabledState!.Id!.Value = rule.Enabled;
                condition.EnabledState.EffectiveDisplayName!.Value = new LocalizedText(rule.Enabled ? "Enabled" : "Disabled");
                nodes.Add(condition, folder.Node.NodeId);
                conditions.Add(condition);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OPC UA 报警条件绑定失败：规则 {RuleId}", rule.Id);
            }
        }

        if (conditions.Count == 0) return;
        logger.LogInformation("对外 OPC UA 已挂载 {Count} 个报警条件", conditions.Count);
        _ = Task.Run(() => PublishLoopAsync(conditions, scopes, context, logger, publishToken, intervalMs), publishToken);
    }

    private static async Task PublishLoopAsync(List<ScadaAlarmConditionState> conditions, IServiceScopeFactory scopes,
        ISystemContext context, ILogger logger, CancellationToken ct, int intervalMs)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Math.Clamp(intervalMs, 200, 30_000), ct);
                await using var scope = scopes.CreateAsyncScope();
                var alarms = scope.ServiceProvider.GetRequiredService<AlarmService>();
                // 两条查询才能覆盖四态：活动报警给出 Unack/Acked，未确认查询补上已恢复但未确认的 RtnUnack。
                // 只查活动报警会让"恢复未确认"在上级系统里直接消失，操作员就再也确认不到它了。
                var live = await alarms.QueryAsync(null, null, activeOnly: true, limit: 5000, ct);
                var unacked = await alarms.QueryAsync(null, null, activeOnly: false, limit: 5000, ct, unackedOnly: true);

                var byRule = new Dictionary<int, AlarmEvent>();
                foreach (var a in unacked.Where(a => a.RuleId is not null).OrderByDescending(a => a.Id))
                    byRule[a.RuleId!.Value] = a;
                // 活动报警后写，优先级高于同规则的已恢复报警。
                foreach (var a in live.Where(a => a.RuleId is not null).OrderByDescending(a => a.Id))
                    byRule[a.RuleId!.Value] = a;

                foreach (var condition in conditions)
                {
                    byRule.TryGetValue(condition.RuleId, out var alarm);
                    if (!condition.Apply(context, alarm)) continue;
                    try { condition.ReportEvent(context, null!); }
                    catch (Exception ex) { logger.LogDebug(ex, "OPC UA 报警事件上报失败：规则 {RuleId}", condition.RuleId); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "OPC UA 报警投影循环异常");
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    private static async Task<bool> AcknowledgeAsync(IServiceScopeFactory scopes, AlarmRule rule, string user, string? note)
    {
        await using var scope = scopes.CreateAsyncScope();
        var alarms = scope.ServiceProvider.GetRequiredService<AlarmService>();
        var candidates = await alarms.QueryAsync(null, null, activeOnly: true, limit: 50,
            filter: new AlarmQueryFilter(TagId: rule.TagId));
        var target = candidates.FirstOrDefault(a => a.RuleId == rule.Id && a.AckTs is null);
        if (target is null) return false;
        return await alarms.AckAsync(target.Id, user, note) is not null;
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Alarm";
        var chars = name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        return new string(chars);
    }
}
