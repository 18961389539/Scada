using Scada.Core.Entities;
using Scada.Engine.Reports;

namespace Scada.Engine.Tests;

// 报表周期（班/日/月，按班次时区）与模板校验。
public class ReportTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static long Local(int y, int mo, int d, int h, int mi = 0) => ReportPeriods.ToUtc(new DateTime(y, mo, d, h, mi, 0), Shanghai);

    [Fact]
    public void PeriodsFollowShiftZoneAndRows()
    {
        var day = ReportPeriods.Range(ReportPeriod.Day, "08:00,20:00", Local(2026, 10, 5, 0, 30), Shanghai);
        Assert.Equal(Local(2026, 10, 5, 0), day.From); Assert.Equal(Local(2026, 10, 6, 0), day.To);
        Assert.Equal(24, ReportPeriods.Rows(ReportPeriod.Day, day.From, day.To, Shanghai).Count);

        // 夜班跨零点：凌晨 03:00 属于前一天 20:00 开始的班。
        var night = ReportPeriods.Range(ReportPeriod.Shift, "08:00,20:00", Local(2026, 10, 5, 3), Shanghai);
        Assert.Equal(Local(2026, 10, 4, 20), night.From); Assert.Equal(Local(2026, 10, 5, 8), night.To);
        Assert.Equal(12, ReportPeriods.Rows(ReportPeriod.Shift, night.From, night.To, Shanghai).Count);
        Assert.NotEqual(day.Key, night.Key);

        var month = ReportPeriods.Range(ReportPeriod.Month, "", Local(2026, 2, 10, 12), Shanghai);
        Assert.Equal(Local(2026, 2, 1, 0), month.From); Assert.Equal(Local(2026, 3, 1, 0), month.To);
        Assert.Equal(28, ReportPeriods.Rows(ReportPeriod.Month, month.From, month.To, Shanghai).Count);

        var previous = ReportPeriods.Previous(ReportPeriod.Day, "", Local(2026, 10, 5, 0, 5), Shanghai);
        Assert.Equal(Local(2026, 10, 4, 0), previous.From);
    }

    [Fact]
    public void TemplateValidationRejectsBadColumns()
    {
        var t = new ReportTemplate { Name = "日报", Period = ReportPeriod.Day, ShiftStarts = "08:00,20:00" };
        ReportColumn Tag(string key) => new() { Key = key, Title = key, Kind = ReportColumnKind.TagAvg, TagId = 1 };
        Assert.NotEmpty(ReportTemplates.Validate(t, [Tag("a"), Tag("b"), new() { Key = "c", Title = "c", Kind = ReportColumnKind.Formula, Formula = "$a - $b" }]));
        Assert.Throws<ReportTemplateException>(() => ReportTemplates.Validate(t, []));
        Assert.Throws<ReportTemplateException>(() => ReportTemplates.Validate(t, [Tag("a"), Tag("a")]));
        Assert.Throws<ReportTemplateException>(() => ReportTemplates.Validate(t, [new() { Key = "c", Title = "c", Kind = ReportColumnKind.Formula, Formula = "$a * 2" }, Tag("a")]));
        Assert.Throws<ReportTemplateException>(() => ReportTemplates.Validate(t, [new() { Key = "x", Title = "x", Kind = ReportColumnKind.TagMax }]));
        Assert.Throws<ReportTemplateException>(() => ReportTemplates.Validate(t, [new() { Key = "x", Title = "x", Kind = ReportColumnKind.CalcSnapshot }]));
        Assert.Throws<ReportTemplateException>(() => ReportTemplates.Validate(new ReportTemplate { Name = "班报", Period = ReportPeriod.Shift, ShiftStarts = "25:00" }, [Tag("a")]));
    }
}
