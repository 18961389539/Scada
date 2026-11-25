using System.Globalization;
using Scada.Core.Entities;
using Scada.Server.Endpoints;
using Scada.Server.Localization;

namespace Scada.Server.Reports;

public static class QualityExport
{
    public const int MaxRecords = 10000;

    public static List<OeeExport.Section> Sections(
        QualitySummaryView summary,
        IReadOnlyList<QualityInspectionView> records,
        bool truncated,
        TimeZoneInfo zone,
        string locale)
    {
        string L(string s) => Localizer.T(s, locale);
        string Pct(double? v) => v is null ? "" : (v.Value * 100).ToString("0.00", CultureInfo.InvariantCulture);
        string Ts(long ms) => ms <= 0 ? "" : TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone).ToString("yyyy-MM-dd HH:mm:ss");
        string Result(QualityResult result) => result switch
        {
            QualityResult.Passed => L("合格"),
            QualityResult.Failed => L("不合格"),
            QualityResult.Rework => L("返工"),
            QualityResult.Scrap => L("报废"),
            _ => result.ToString()
        };

        return
        [
            new(L("汇总"), [L("指标"), L("值")],
            [
                [L("检验数"), summary.Total],
                [L("合格数"), summary.Passed],
                [L("不合格数"), summary.Failed],
                [L("返工数"), summary.Rework],
                [L("报废数"), summary.Scrap],
                [L("良率 %"), Pct(summary.Yield)],
                [L("不良率 %"), Pct(summary.DefectRate)],
                [L("报废率 %"), Pct(summary.ScrapRate)],
                [L("返工率 %"), Pct(summary.ReworkRate)],
                truncated ? [L("记录截断"), L($"仅导出前 {MaxRecords} 条检验记录")] : [L("记录数"), records.Count]
            ]),
            new(L("按日"), [L("日期"), L("检验数"), L("合格"), L("不合格"), L("返工"), L("报废"), L("良率 %")],
                summary.ByDay.Select(d => (IReadOnlyList<object?>)[d.Date, d.Total, d.Passed, d.Failed, d.Rework, d.Scrap, Pct(d.Yield)]).ToList()),
            new(L("按缺陷"), [L("代码"), L("名称"), L("分类"), L("次数")],
                summary.ByDefect.Select(d => (IReadOnlyList<object?>)[d.Code, d.Name, d.Category, d.Count]).ToList()),
            new(L("按区域"), [L("区域"), L("检验数"), L("合格"), L("不合格"), L("良率 %")],
                summary.ByArea.Select(d => (IReadOnlyList<object?>)[d.AreaName, d.Total, d.Passed, d.Failed, Pct(d.Yield)]).ToList()),
            new(L("按设备"), [L("设备"), L("检验数"), L("合格"), L("不合格"), L("良率 %")],
                summary.ByDevice.Select(d => (IReadOnlyList<object?>)[d.DeviceName, d.Total, d.Passed, d.Failed, Pct(d.Yield)]).ToList()),
            new(L("检验记录"),
                [L("检验时间"), L("SN"), L("批次号"), L("产品"), L("区域编号"), L("结果"), L("缺陷代码"), L("缺陷名称"),
                    L("设备"), L("测点"), L("检验员"), L("备注"), L("创建人"), L("创建时间"), L("更新人"), L("更新时间"), L("已修订")],
                records.Select(r =>
                {
                    var revised = r.UpdatedTs > r.CreatedTs;
                    return (IReadOnlyList<object?>)[Ts(r.Ts), r.Sn, r.BatchNo, r.Product, r.AreaId, Result(r.Result),
                        r.DefectCode ?? "", r.DefectName ?? "", r.DeviceName ?? "", r.TagName ?? "",
                        r.Inspector, r.Note, r.CreatedBy, Ts(r.CreatedTs), r.UpdatedBy, Ts(r.UpdatedTs),
                        revised ? L("是") : L("否")];
                }).ToList())
        ];
    }
}
