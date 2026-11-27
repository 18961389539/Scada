using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.Alarms;

public enum AlarmTransitionKind
{
    Raise,
    Recover,
    StateChange
}

public sealed record AlarmTransition(AlarmRule Rule, TagValue Value, AlarmTransitionKind Kind, double? PreviousValue = null);

public sealed class AlarmEvaluator
{
    private readonly Dictionary<int, RuleState> _states = new();
    // 复合条件编译缓存：配置热加载后表达式文本变化时自然失效。
    private readonly Dictionary<int, (string Text, TagExpression? Expression)> _expressions = new();

    public IReadOnlyList<AlarmTransition> Evaluate(
        Tag tag,
        TagValue value,
        IReadOnlyList<AlarmRule> rules,
        long nowMs,
        Func<int, TagValue?>? lookup = null,
        bool targetDeviceSimulated = false,
        Func<int, bool>? officialActive = null,
        Func<int, bool>? inputSimulated = null)
    {
        var transitions = new List<AlarmTransition>();
        var sampleSimulated = value.Simulated || targetDeviceSimulated;
        foreach (var rule in rules)
        {
            var state = GetState(rule.Id);
            var official = officialActive?.Invoke(rule.Id) == true;
            // 仿真样本不得恢复或改写正式未结束报警；无正式活动时仍可用仿真样本升演练报警。
            var holdForOfficial = sampleSimulated && official;
            // 复合条件仅在目标设备本身也在仿真、且不会改写正式报警时，才允许用仿真输入继续演练。
            var allowSimulatedExpression = targetDeviceSimulated && !official;

            if (!rule.Enabled || rule.OutOfService)
            {
                state.PendingSince = null;
                state.RecoverPendingSince = null;
                if (state.Active)
                {
                    state.Active = false;
                    transitions.Add(new AlarmTransition(rule, value, AlarmTransitionKind.Recover));
                }

                continue;
            }

            // 质量坏报警：质量不是 Good 即条件成立；质量恢复 Good 后按恢复延时结束。
            if (rule.Type == AlarmRuleType.BadQuality)
            {
                if (holdForOfficial)
                {
                    state.PendingSince = null;
                    state.RecoverPendingSince = null;
                    continue;
                }
                TrackLevel(rule, state, value, nowMs, value.Quality != TagQuality.Good, value.Quality == TagQuality.Good, transitions);
                continue;
            }

            // 复合条件：任一引用测点缺失、质量不好或（非演练时）仿真时保持现有状态，避免坏数据或演练制造误报或误恢复。
            if (rule.Type == AlarmRuleType.Expression)
            {
                var result = EvaluateExpression(rule, lookup, allowSimulatedExpression, inputSimulated);
                if (result is null)
                {
                    state.PendingSince = null;
                    continue;
                }

                var judged = value with { Value = result.Value ? 1 : 0 };
                TrackLevel(rule, state, judged, nowMs, result.Value, !result.Value, transitions);
                continue;
            }

            if (value.Quality != TagQuality.Good)
            {
                state.PendingSince = null;
                continue;
            }

            if (holdForOfficial)
            {
                state.PendingSince = null;
                state.RecoverPendingSince = null;
                continue;
            }

            // 位翻转报警：StateChange 为旧类型，Discrete + Change 为新的等价写法；首次采样只记录基准状态。
            if (rule.Type == AlarmRuleType.StateChange ||
                (rule.Type == AlarmRuleType.Discrete && rule.GetEffectiveActiveWhen() == AlarmActiveWhen.Change))
            {
                var current = rule.EvaluatedValue(value.Value) >= 0.5;
                if (state.LastBool is null)
                {
                    state.LastBool = current;
                    continue;
                }

                if (state.LastBool != current)
                {
                    var previous = state.LastBool;
                    state.LastBool = current;
                    transitions.Add(new AlarmTransition(rule, value, AlarmTransitionKind.StateChange, previous is true ? 1 : 0));
                }

                continue;
            }

            var judgedValue = rule.EvaluatedValue(value.Value);
            var condition = rule.Type switch
            {
                AlarmRuleType.High or AlarmRuleType.HighHigh => judgedValue > rule.Threshold,
                AlarmRuleType.Low or AlarmRuleType.LowLow => judgedValue < rule.Threshold,
                // 位报警按电平判定：ON = 取位后读到 1 报警，OFF = 读到 0 报警；状态保持到条件解除。
                AlarmRuleType.Discrete => rule.GetEffectiveActiveWhen() == AlarmActiveWhen.On ? judgedValue >= 0.5 : judgedValue < 0.5,
                _ => false
            };
            var recovered = rule.Type switch
            {
                AlarmRuleType.High or AlarmRuleType.HighHigh =>
                    judgedValue <= rule.Threshold - Math.Max(rule.RecoverDeadband, 0),
                AlarmRuleType.Low or AlarmRuleType.LowLow =>
                    judgedValue >= rule.Threshold + Math.Max(rule.RecoverDeadband, 0),
                // 位报警没有死区：ON 在回到 0 时恢复，OFF 在回到 1 时恢复。
                AlarmRuleType.Discrete => rule.GetEffectiveActiveWhen() == AlarmActiveWhen.On ? judgedValue < 0.5 : judgedValue >= 0.5,
                _ => false
            };
            TrackLevel(rule, state, value, nowMs, condition, recovered, transitions);
        }

        return transitions;
    }

    // 事件没能落库时撤回本次闩锁，否则内存认为已触发/已恢复，库里却不是。
    public void Revert(AlarmTransition transition)
    {
        if (!_states.TryGetValue(transition.Rule.Id, out var state)) return;
        if (transition.Kind == AlarmTransitionKind.Raise)
        {
            state.Active = false;
            state.PendingSince = null;
        }
        else if (transition.Kind == AlarmTransitionKind.Recover)
        {
            state.Active = true;
            state.RecoverPendingSince = null;
        }
    }

    // 触发/恢复的通用延时状态机：触发需持续 DelaySec，恢复需持续 OffDelaySec；期间回到原状态则重新计时。
    private static void TrackLevel(AlarmRule rule, RuleState state, TagValue value, long nowMs, bool condition, bool recovered, List<AlarmTransition> transitions)
    {
        if (!state.Active)
        {
            if (!condition)
            {
                state.PendingSince = null;
                return;
            }

            state.PendingSince ??= nowMs;
            var delayMs = Math.Max(rule.DelaySec, 0) * 1000L;
            if (nowMs - state.PendingSince.Value >= delayMs)
            {
                state.Active = true;
                state.PendingSince = null;
                transitions.Add(new AlarmTransition(rule, value, AlarmTransitionKind.Raise));
            }

            return;
        }

        if (!recovered)
        {
            state.RecoverPendingSince = null;
            return;
        }

        state.RecoverPendingSince ??= nowMs;
        if (nowMs - state.RecoverPendingSince.Value >= Math.Max(rule.OffDelaySec, 0) * 1000L)
        {
            state.Active = false;
            state.RecoverPendingSince = null;
            transitions.Add(new AlarmTransition(rule, value, AlarmTransitionKind.Recover));
        }
    }

    // null 表示无法判定（表达式编译失败、引用测点缺失/质量不好，或非演练时遇到仿真输入），调用方保持现有状态。
    private bool? EvaluateExpression(AlarmRule rule, Func<int, TagValue?>? lookup, bool allowSimulated, Func<int, bool>? inputSimulated)
    {
        if (lookup is null || string.IsNullOrWhiteSpace(rule.Expression)) return null;
        if (!_expressions.TryGetValue(rule.Id, out var cached) || !string.Equals(cached.Text, rule.Expression, StringComparison.Ordinal))
        {
            TagExpression? compiled;
            try { compiled = TagExpression.Compile(rule.Expression); }
            catch (TagExpressionException) { compiled = null; }
            cached = (rule.Expression, compiled);
            _expressions[rule.Id] = cached;
        }

        if (cached.Expression is not { } expression) return null;
        var values = new Dictionary<int, double>();
        foreach (var id in expression.Dependencies)
        {
            var input = lookup(id);
            if (input is null || input.Quality != TagQuality.Good) return null;
            if ((input.Simulated || inputSimulated?.Invoke(id) == true) && !allowSimulated) return null;
            values[id] = input.Value;
        }

        try { return expression.Evaluate(id => values.GetValueOrDefault(id)) != 0; }
        catch (TagExpressionException) { return null; }
    }

    public void RestoreActive(int ruleId) => GetState(ruleId).Active = true;

    // 演练报警因仿真结束关闭后，允许同一条件再升一条正式报警。
    public void ClearActive(int ruleId)
    {
        var state = GetState(ruleId);
        state.Active = false;
        state.PendingSince = null;
        state.RecoverPendingSince = null;
    }

    public bool IsActive(int ruleId) => GetState(ruleId).Active;

    private RuleState GetState(int ruleId)
    {
        if (!_states.TryGetValue(ruleId, out var state))
        {
            state = new RuleState();
            _states[ruleId] = state;
        }

        return state;
    }

    private sealed class RuleState
    {
        public bool Active { get; set; }
        public long? PendingSince { get; set; }
        public long? RecoverPendingSince { get; set; }
        public bool? LastBool { get; set; }
    }
}
