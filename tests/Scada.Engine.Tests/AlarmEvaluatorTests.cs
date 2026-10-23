using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.Alarms;

namespace Scada.Engine.Tests;

public class AlarmEvaluatorTests
{
    private static readonly Tag SampleTag = new() { Id = 1, DeviceId = 1, Name = "T1", DataType = TagDataType.Float };

    private static AlarmRule Rule(AlarmRuleType type, double threshold, int delaySec = 0, double deadband = 0) => new()
    {
        Id = 10,
        TagId = 1,
        Type = type,
        Threshold = threshold,
        DelaySec = delaySec,
        RecoverDeadband = deadband,
        Level = AlarmLevel.Warning,
        Enabled = true,
        Message = "test"
    };

    private static TagValue Value(double value, long ts, TagQuality quality = TagQuality.Good) =>
        new(1, value, quality, ts);

    [Fact]
    public void HighAlarm_WaitsForDelay()
    {
        var evaluator = new AlarmEvaluator();
        var rule = Rule(AlarmRuleType.High, 100, delaySec: 3);

        Assert.Empty(evaluator.Evaluate(SampleTag, Value(105, 1_000), [rule], 1_000));
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(106, 2_000), [rule], 2_000));
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(107, 3_999), [rule], 3_999));

        var transitions = evaluator.Evaluate(SampleTag, Value(108, 4_000), [rule], 4_000);
        var raise = Assert.Single(transitions);
        Assert.Equal(AlarmTransitionKind.Raise, raise.Kind);
    }

    [Fact]
    public void HighAlarm_ConditionBreakResetsDelay()
    {
        var evaluator = new AlarmEvaluator();
        var rule = Rule(AlarmRuleType.High, 100, delaySec: 5);

        Assert.Empty(evaluator.Evaluate(SampleTag, Value(105, 1_000), [rule], 1_000));
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(90, 4_000), [rule], 4_000));
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(105, 6_000), [rule], 6_000));
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(105, 10_999), [rule], 10_999));

        var transitions = evaluator.Evaluate(SampleTag, Value(105, 11_000), [rule], 11_000);
        Assert.Equal(AlarmTransitionKind.Raise, Assert.Single(transitions).Kind);
    }

    [Fact]
    public void HighAlarm_RecoversOnlyBelowDeadband()
    {
        var evaluator = new AlarmEvaluator();
        var rule = Rule(AlarmRuleType.High, 100, deadband: 5);
        Assert.Single(evaluator.Evaluate(SampleTag, Value(110, 1_000), [rule], 1_000));

        Assert.Empty(evaluator.Evaluate(SampleTag, Value(97, 2_000), [rule], 2_000));
        Assert.True(evaluator.IsActive(rule.Id));
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(95.1, 3_000), [rule], 3_000));
        Assert.True(evaluator.IsActive(rule.Id));

        var transitions = evaluator.Evaluate(SampleTag, Value(95, 4_000), [rule], 4_000);
        Assert.Equal(AlarmTransitionKind.Recover, Assert.Single(transitions).Kind);
        Assert.False(evaluator.IsActive(rule.Id));
    }

    [Fact]
    public void LowLowAlarm_RaisesAndRecovers()
    {
        var evaluator = new AlarmEvaluator();
        var rule = Rule(AlarmRuleType.LowLow, -120, delaySec: 3, deadband: 5);

        Assert.Empty(evaluator.Evaluate(SampleTag, Value(-125, 1_000), [rule], 1_000));
        Assert.Equal(AlarmTransitionKind.Raise,
            Assert.Single(evaluator.Evaluate(SampleTag, Value(-126, 4_000), [rule], 4_000)).Kind);
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(-118, 5_000), [rule], 5_000));
        Assert.Equal(AlarmTransitionKind.Recover,
            Assert.Single(evaluator.Evaluate(SampleTag, Value(-114, 6_000), [rule], 6_000)).Kind);
    }

    [Fact]
    public void StateChange_IgnoresFirstSampleThenReportsEachToggle()
    {
        var evaluator = new AlarmEvaluator();
        var rule = Rule(AlarmRuleType.StateChange, 0.5);
        var boolTag = new Tag { Id = 1, DeviceId = 1, Name = "run", DataType = TagDataType.Bool };

        Assert.Empty(evaluator.Evaluate(boolTag, Value(1, 1_000), [rule], 1_000));
        Assert.Empty(evaluator.Evaluate(boolTag, Value(1, 2_000), [rule], 2_000));

        Assert.Equal(AlarmTransitionKind.StateChange,
            Assert.Single(evaluator.Evaluate(boolTag, Value(0, 3_000), [rule], 3_000)).Kind);
        Assert.Equal(AlarmTransitionKind.StateChange,
            Assert.Single(evaluator.Evaluate(boolTag, Value(1, 4_000), [rule], 4_000)).Kind);
    }

    [Fact]
    public void BadQuality_DoesNotRaiseAlarm()
    {
        var evaluator = new AlarmEvaluator();
        var rule = Rule(AlarmRuleType.High, 100, delaySec: 0);

        Assert.Empty(evaluator.Evaluate(SampleTag, Value(150, 1_000, TagQuality.CommFailure), [rule], 1_000));
        Assert.Empty(evaluator.Evaluate(SampleTag, Value(150, 2_000, TagQuality.Bad), [rule], 2_000));
    }

    [Fact]
    public void DisablingRule_RecoversActiveAlarm()
    {
        var evaluator = new AlarmEvaluator();
        var rule = Rule(AlarmRuleType.High, 100);
        Assert.Single(evaluator.Evaluate(SampleTag, Value(110, 1_000), [rule], 1_000));

        rule.Enabled = false;
        var transitions = evaluator.Evaluate(SampleTag, Value(110, 2_000), [rule], 2_000);
        Assert.Equal(AlarmTransitionKind.Recover, Assert.Single(transitions).Kind);
        Assert.False(evaluator.IsActive(rule.Id));
    }
}
