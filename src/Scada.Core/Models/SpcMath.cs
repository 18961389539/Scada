using Scada.Core.Entities;

namespace Scada.Core.Models;

// SPC 统计：控制限、极差图、判异规则与过程能力。所有公式按常数采样子组计算。
public static class SpcMath
{
    public static double A2(int n) => n switch
    {
        2 => 1.880, 3 => 1.023, 4 => 0.729, 5 => 0.577, 6 => 0.483, 7 => 0.419,
        8 => 0.373, 9 => 0.337, 10 => 0.308, _ => 0
    };

    public static double D3(int n) => n switch
    {
        2 => 0, 3 => 0, 4 => 0, 5 => 0, 6 => 0, 7 => 0.076, 8 => 0.136, 9 => 0.184, 10 => 0.223, _ => 0
    };

    public static double D4(int n) => n switch
    {
        2 => 3.267, 3 => 2.574, 4 => 2.282, 5 => 2.114, 6 => 2.004, 7 => 1.924,
        8 => 1.864, 9 => 1.816, 10 => 1.777, _ => 3.267
    };

    public static double D2(int n) => n switch
    {
        2 => 1.128, 3 => 1.693, 4 => 2.059, 5 => 2.326, 6 => 2.534, 7 => 2.704,
        8 => 2.847, 9 => 2.970, 10 => 3.078, _ => 1.128
    };

    public static SpcLimits? ComputeLimits(
        IReadOnlyList<double> means, IReadOnlyList<double> ranges, SpcChartType chartType, int subgroupSize)
    {
        if (means.Count == 0 || means.Count != ranges.Count) return null;
        var center = means.Average();
        if (chartType == SpcChartType.Individuals)
        {
            // 单值图用移动极差：第一个点没有前值，Range 为 0，不参与 MRbar。
            if (means.Count < 2) return null;
            var moving = ranges.Skip(1).ToArray();
            if (moving.Length == 0 || moving.Any(x => !double.IsFinite(x) || x < 0)) return null;
            var mrBar = moving.Average();
            if (mrBar <= 0) return null;
            var sigma = mrBar / D2(2);
            return new SpcLimits(center, center + 3 * sigma, center - 3 * sigma,
                mrBar, D4(2) * mrBar, 0, sigma, false);
        }
        else
        {
            if (means.Count < 2) return null;
            var validRanges = ranges.Where(x => double.IsFinite(x) && x >= 0).ToArray();
            if (validRanges.Length == 0) return null;
            var rBar = validRanges.Average();
            if (rBar <= 0) return null;
            var n = Math.Clamp(subgroupSize, 2, 10);
            var sigma = rBar / D2(n);
            return new SpcLimits(center, center + A2(n) * rBar, center - A2(n) * rBar,
                rBar, D4(n) * rBar, D3(n) * rBar, sigma, false);
        }
    }

    public static (double? Cp, double? Cpk) Capability(double center, double sigmaWithin, double? usl, double? lsl)
    {
        if (!double.IsFinite(sigmaWithin) || sigmaWithin <= 0) return (null, null);
        if (usl is double upper && lsl is double lower)
            return ((upper - lower) / (6 * sigmaWithin), Math.Min(upper - center, center - lower) / (3 * sigmaWithin));
        if (usl is double onlyUpper) return (null, (onlyUpper - center) / (3 * sigmaWithin));
        if (lsl is double onlyLower) return (null, (center - onlyLower) / (3 * sigmaWithin));
        return (null, null);
    }

    // 判异规则按“当前点”和最近若干点评估；返回当前样本命中的规则。
    public static IReadOnlyList<SpcViolationRule> Evaluate(
        IReadOnlyList<double> means, IReadOnlyList<double> ranges, SpcLimits limits, SpcChart chart)
    {
        var result = new List<SpcViolationRule>();
        if (means.Count == 0) return result;
        var sigma = (limits.UpperControlLimit - limits.CenterLine) / 3;
        if (sigma <= 0) return result;

        var current = means[^1];
        var last3 = means.TakeLast(3).ToArray();
        var last5 = means.TakeLast(5).ToArray();
        var last8 = means.TakeLast(8).ToArray();

        if (chart.RuleBeyond3Sigma && (current > limits.UpperControlLimit || current < limits.LowerControlLimit))
            result.Add(SpcViolationRule.Beyond3Sigma);
        if (chart.RuleTwoOfThreeBeyond2Sigma && last3.Length == 3)
        {
            var above = last3.Count(x => x > limits.CenterLine + 2 * sigma);
            var below = last3.Count(x => x < limits.CenterLine - 2 * sigma);
            if (above >= 2 || below >= 2) result.Add(SpcViolationRule.TwoOfThreeBeyond2Sigma);
        }
        if (chart.RuleFourOfFiveBeyond1Sigma && last5.Length == 5)
        {
            var above = last5.Count(x => x > limits.CenterLine + sigma);
            var below = last5.Count(x => x < limits.CenterLine - sigma);
            if (above >= 4 || below >= 4) result.Add(SpcViolationRule.FourOfFiveBeyond1Sigma);
        }
        if (chart.RuleEightOnOneSide && last8.Length == 8)
        {
            if (last8.All(x => x > limits.CenterLine) || last8.All(x => x < limits.CenterLine))
                result.Add(SpcViolationRule.EightOnOneSide);
        }
        if (chart.RuleRangeBeyondLimit && ranges.Count > 0 &&
            limits.RangeUpperControlLimit is double rangeUpper && limits.RangeLowerControlLimit is double rangeLower)
        {
            var currentRange = ranges[^1];
            if (currentRange > rangeUpper || currentRange < rangeLower)
                result.Add(SpcViolationRule.RangeBeyondLimit);
        }
        return result;
    }
}

public sealed record SpcLimits(
    double CenterLine, double UpperControlLimit, double LowerControlLimit,
    double? RangeCenter, double? RangeUpperControlLimit, double? RangeLowerControlLimit,
    double SigmaWithin, bool Provisional);

public static class SpcRuleText
{
    public static string Code(SpcViolationRule rule) => rule.ToString();

    public static string Label(SpcViolationRule rule) => rule switch
    {
        SpcViolationRule.Beyond3Sigma => "超出 3σ 控制限",
        SpcViolationRule.TwoOfThreeBeyond2Sigma => "连续 3 点中 2 点超出 2σ",
        SpcViolationRule.FourOfFiveBeyond1Sigma => "连续 5 点中 4 点超出 1σ",
        SpcViolationRule.EightOnOneSide => "连续 8 点在中心线同一侧",
        SpcViolationRule.RangeBeyondLimit => "极差超出控制限",
        _ => rule.ToString()
    };

    public static string LabelEn(SpcViolationRule rule) => rule switch
    {
        SpcViolationRule.Beyond3Sigma => "Beyond the 3-sigma control limit",
        SpcViolationRule.TwoOfThreeBeyond2Sigma => "2 of 3 points beyond 2 sigma",
        SpcViolationRule.FourOfFiveBeyond1Sigma => "4 of 5 points beyond 1 sigma",
        SpcViolationRule.EightOnOneSide => "8 consecutive points on one side of the center line",
        SpcViolationRule.RangeBeyondLimit => "Range beyond the control limit",
        _ => rule.ToString()
    };
}
