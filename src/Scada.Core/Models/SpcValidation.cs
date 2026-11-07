using Scada.Core.Entities;

namespace Scada.Core.Models;

public static class SpcValidation
{
    public static string? Chart(SpcChart chart)
    {
        if (string.IsNullOrWhiteSpace(chart.Name) || chart.Name.Trim().Length > 100)
            return "控制图名称须为 1～100 个字符";
        if (chart.AreaId <= 0) return "必须指定区域";
        if (chart.TagId <= 0) return "必须选择测点";
        if (!Enum.IsDefined(chart.ChartType)) return "控制图类型无效";
        if (chart.ChartType == SpcChartType.Individuals && chart.SubgroupSize != 1)
            return "单值移动极差图的子组大小必须为 1";
        if (chart.ChartType == SpcChartType.XbarR && chart.SubgroupSize is < 2 or > 10)
            return "均值极差图的子组大小须为 2～10";
        if (chart.SamplingIntervalSec is < 5 or > 86400) return "采样周期须为 5～86400 秒";
        if (chart.BaselineSubgroups is < 5 or > 100) return "基线子组数须为 5～100";
        if (!chart.RuleBeyond3Sigma && !chart.RuleTwoOfThreeBeyond2Sigma &&
            !chart.RuleFourOfFiveBeyond1Sigma && !chart.RuleEightOnOneSide && !chart.RuleRangeBeyondLimit)
            return "至少启用一条判异规则";
        if (!chart.AutoLimits)
        {
            if (chart.CenterLine is not double center || chart.UpperControlLimit is not double ucl || chart.LowerControlLimit is not double lcl)
                return "手动控制限必须填写中心线、上限和下限";
            if (!double.IsFinite(center) || !double.IsFinite(ucl) || !double.IsFinite(lcl) || ucl <= center || center <= lcl)
                return "控制限须满足上限大于中心线、中心线大于下限";
        }
        if (chart.SpecUpperLimit is double usl && chart.SpecLowerLimit is double lsl && usl <= lsl)
            return "规格上限必须大于规格下限";
        if (chart.SpecUpperLimit is double upper && !double.IsFinite(upper)) return "规格上限无效";
        if (chart.SpecLowerLimit is double lower && !double.IsFinite(lower)) return "规格下限无效";
        if (chart.Description.Length > 500) return "控制图说明过长（最多 500 个字符）";
        return null;
    }
}
