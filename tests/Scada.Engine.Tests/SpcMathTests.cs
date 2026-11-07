using Scada.Core.Entities;
using Scada.Core.Models;

namespace Scada.Engine.Tests;

public class SpcMathTests
{
    [Fact]
    public void IndividualsLimitsUseMovingRange()
    {
        var limits = SpcMath.ComputeLimits(new double[] { 10, 12, 11 }, new double[] { 0, 2, 1 },
            SpcChartType.Individuals, 1);
        Assert.NotNull(limits);
        Assert.Equal(11, limits.CenterLine);
        Assert.Equal(1.5 / 1.128, limits.SigmaWithin, 6);
        Assert.Equal(11 + 3 * (1.5 / 1.128), limits.UpperControlLimit, 6);
        Assert.Equal(11 - 3 * (1.5 / 1.128), limits.LowerControlLimit, 6);
        Assert.Equal(1.5, limits.RangeCenter);
        Assert.Equal(3.267 * 1.5, limits.RangeUpperControlLimit!.Value, 6);
        Assert.Equal(0d, limits.RangeLowerControlLimit);
    }

    [Fact]
    public void XbarRLimitsUseSubgroupConstants()
    {
        var limits = SpcMath.ComputeLimits(new double[] { 10, 12 }, new double[] { 2, 3 },
            SpcChartType.XbarR, 5);
        Assert.NotNull(limits);
        Assert.Equal(11, limits.CenterLine);
        Assert.Equal(2.5, limits.RangeCenter);
        Assert.Equal(11 + SpcMath.A2(5) * 2.5, limits.UpperControlLimit, 6);
        Assert.Equal(11 - SpcMath.A2(5) * 2.5, limits.LowerControlLimit, 6);
        Assert.Equal(2.5 / SpcMath.D2(5), limits.SigmaWithin, 6);
        Assert.Equal(SpcMath.D3(5) * 2.5, limits.RangeLowerControlLimit);
        Assert.Equal(SpcMath.D4(5) * 2.5, limits.RangeUpperControlLimit!.Value, 6);
    }

    [Fact]
    public void LimitsRequireAtLeastTwoSubgroups()
    {
        Assert.Null(SpcMath.ComputeLimits(new double[] { 10 }, new double[] { 0 }, SpcChartType.Individuals, 1));
        Assert.Null(SpcMath.ComputeLimits(new double[] { 10 }, new double[] { 2 }, SpcChartType.XbarR, 5));
    }

    private static SpcChart Chart() => new()
    {
        RuleBeyond3Sigma = true, RuleTwoOfThreeBeyond2Sigma = true, RuleFourOfFiveBeyond1Sigma = true,
        RuleEightOnOneSide = true, RuleRangeBeyondLimit = true
    };

    private static SpcLimits Limits() => new(0, 3, -3, 1, 3.267, 0, 1, false);

    [Fact]
    public void DetectsPointBeyondThreeSigma()
    {
        var rules = SpcMath.Evaluate(new double[] { 0, 0, 3.1 }, new double[] { 0, 0, 0 }, Limits(), Chart());
        Assert.Contains(SpcViolationRule.Beyond3Sigma, rules);
    }

    [Fact]
    public void DetectsTwoOfThreeBeyondTwoSigma()
    {
        var rules = SpcMath.Evaluate(new double[] { 0, 2.1, 2.1 }, new double[] { 0, 0, 0 }, Limits(), Chart());
        Assert.Contains(SpcViolationRule.TwoOfThreeBeyond2Sigma, rules);
        Assert.DoesNotContain(SpcViolationRule.Beyond3Sigma, rules);
    }

    [Fact]
    public void DetectsFourOfFiveBeyondOneSigma()
    {
        var rules = SpcMath.Evaluate(new double[] { 0, 1.1, 1.1, 1.1, 1.1 }, new double[] { 0, 0, 0, 0, 0 }, Limits(), Chart());
        Assert.Contains(SpcViolationRule.FourOfFiveBeyond1Sigma, rules);
    }

    [Fact]
    public void DetectsEightOnOneSide()
    {
        var rules = SpcMath.Evaluate(new double[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8 },
            new double[] { 0, 0, 0, 0, 0, 0, 0, 0 }, Limits(), Chart());
        Assert.Contains(SpcViolationRule.EightOnOneSide, rules);
    }

    [Fact]
    public void EqualCenterPointBreaksEightOnOneSide()
    {
        var rules = SpcMath.Evaluate(new double[] { 0.1, 0.2, 0.3, 0, 0.5, 0.6, 0.7, 0.8 },
            new double[] { 0, 0, 0, 0, 0, 0, 0, 0 }, Limits(), Chart());
        Assert.DoesNotContain(SpcViolationRule.EightOnOneSide, rules);
    }

    [Fact]
    public void DetectsRangeBeyondLimit()
    {
        var rules = SpcMath.Evaluate(new double[] { 0 }, new double[] { 4 }, Limits(), Chart());
        Assert.Contains(SpcViolationRule.RangeBeyondLimit, rules);
    }

    [Fact]
    public void DisabledRulesDoNotTrigger()
    {
        var chart = Chart();
        chart.RuleBeyond3Sigma = false;
        var rules = SpcMath.Evaluate(new double[] { 5 }, new double[] { 0 }, Limits(), chart);
        Assert.DoesNotContain(SpcViolationRule.Beyond3Sigma, rules);
    }

    [Fact]
    public void CapabilityUsesSpecLimits()
    {
        var both = SpcMath.Capability(10, 1, 13, 7);
        Assert.Equal(1d, both.Cp);
        Assert.Equal(1d, both.Cpk);
        var upper = SpcMath.Capability(10, 1, 13, null);
        Assert.Null(upper.Cp);
        Assert.Equal(1d, upper.Cpk);
        var none = SpcMath.Capability(10, 1, null, null);
        Assert.Null(none.Cp);
        Assert.Null(none.Cpk);
    }

    [Fact]
    public void ChartValidationRejectsInvalidManualLimits()
    {
        var chart = new SpcChart
        {
            Name = "test", AreaId = 1, TagId = 1, ChartType = SpcChartType.Individuals, SubgroupSize = 1,
            SamplingIntervalSec = 60, BaselineSubgroups = 25, AutoLimits = false,
            CenterLine = 10, UpperControlLimit = 9, LowerControlLimit = 11
        };
        Assert.NotNull(SpcValidation.Chart(chart));
    }

    [Fact]
    public void ChartValidationRejectsNoRules()
    {
        var chart = new SpcChart
        {
            Name = "test", AreaId = 1, TagId = 1, ChartType = SpcChartType.Individuals, SubgroupSize = 1,
            SamplingIntervalSec = 60, BaselineSubgroups = 25,
            RuleBeyond3Sigma = false, RuleTwoOfThreeBeyond2Sigma = false,
            RuleFourOfFiveBeyond1Sigma = false, RuleEightOnOneSide = false, RuleRangeBeyondLimit = false
        };
        Assert.NotNull(SpcValidation.Chart(chart));
    }

    [Fact]
    public void QualityValidationRequiresDefectCodeForFailedInspection()
    {
        var inspection = new QualityInspection
        {
            AreaId = 1, Result = QualityResult.Failed, DefectCodeId = null, Ts = 1000
        };
        Assert.NotNull(QualityValidation.Inspection(inspection));
        inspection.DefectCodeId = 1;
        Assert.Null(QualityValidation.Inspection(inspection));
    }
}
