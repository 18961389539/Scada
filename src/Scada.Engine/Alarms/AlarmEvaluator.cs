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

    public IReadOnlyList<AlarmTransition> Evaluate(
        Tag tag,
        TagValue value,
        IReadOnlyList<AlarmRule> rules,
        long nowMs)
    {
        var transitions = new List<AlarmTransition>();
        foreach (var rule in rules)
        {
            var state = GetState(rule.Id);

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

            if (value.Quality != TagQuality.Good)
            {
                state.PendingSince = null;
                continue;
            }

            if (rule.Type == AlarmRuleType.StateChange)
            {
                var current = value.Value >= 0.5;
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

            var condition = rule.Type switch
            {
                AlarmRuleType.High or AlarmRuleType.HighHigh => value.Value > rule.Threshold,
                AlarmRuleType.Low or AlarmRuleType.LowLow => value.Value < rule.Threshold,
                _ => false
            };

            if (!state.Active)
            {
                if (!condition)
                {
                    state.PendingSince = null;
                    continue;
                }

                state.PendingSince ??= nowMs;
                var delayMs = Math.Max(rule.DelaySec, 0) * 1000L;
                if (nowMs - state.PendingSince.Value >= delayMs)
                {
                    state.Active = true;
                    state.PendingSince = null;
                    transitions.Add(new AlarmTransition(rule, value, AlarmTransitionKind.Raise));
                }

                continue;
            }

            var recovered = rule.Type switch
            {
                AlarmRuleType.High or AlarmRuleType.HighHigh =>
                    value.Value <= rule.Threshold - Math.Max(rule.RecoverDeadband, 0),
                AlarmRuleType.Low or AlarmRuleType.LowLow =>
                    value.Value >= rule.Threshold + Math.Max(rule.RecoverDeadband, 0),
                _ => false
            };

            if (!recovered)
            {
                state.RecoverPendingSince = null;
                continue;
            }

            // 恢复延时：恢复条件持续 OffDelaySec 才算恢复，期间回到报警区则重新计时。
            state.RecoverPendingSince ??= nowMs;
            if (nowMs - state.RecoverPendingSince.Value >= Math.Max(rule.OffDelaySec, 0) * 1000L)
            {
                state.Active = false;
                state.RecoverPendingSince = null;
                transitions.Add(new AlarmTransition(rule, value, AlarmTransitionKind.Recover));
            }
        }

        return transitions;
    }

    public void RestoreActive(int ruleId) => GetState(ruleId).Active = true;

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
