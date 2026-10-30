using Microsoft.EntityFrameworkCore;
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

// ReportBuilder 带历史数据：差值边界、表底复位、时间加权累计与合计行。
public class ReportBuilderHistoryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly long Day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const long H = 3_600_000;
    private static Scada.Core.Models.HistoryPoint P(long ts, double v, Scada.Core.Enums.TagQuality q = Scada.Core.Enums.TagQuality.Good) => new(ts, v, q);

    [Fact]
    public void CounterDeltaHandlesBoundariesAndMeterReset()
    {
        // 起点前 10 分钟读数 100；区间内 150、复位后 20、40；终点后 5 分钟 60（终点前 1 小时内有 40）。
        var points = new[] { P(-10 * 60_000, 100), P(H / 2, 150), P(H, 20), P(2 * H - 60_000, 40), P(2 * H + 5 * 60_000, 60) };
        Assert.Equal(50 + 20 + 20, ReportMath.CounterDelta(points, 0, 2 * H));
        // 边界前 1 小时内无样本：取边界后第一个样本，逐段之和等于整段。
        var sparse = new[] { P(0, 10), P(3 * H + 10 * 60_000, 40) };
        var a = ReportMath.CounterDelta(sparse, 0, 3 * H); var b = ReportMath.CounterDelta(sparse, 3 * H, 4 * H);
        Assert.Equal(30, a); Assert.Equal(0, b);
        Assert.Null(ReportMath.CounterDelta([P(5 * H, 1)], 0, H)); // 两端都取不到值
    }

    [Fact]
    public void IntegrateIsTimeWeightedAndDoesNotBridgeLongGaps()
    {
        // 0~1h 从 0 线性升到 10：积分 5；1h~1.5h 恒 10：5；之后 3 小时空档不桥接。
        var points = new[] { P(0, 0), P(H, 10), P(H + H / 2, 10), P(4 * H + H / 2, 10), P(5 * H, 10) };
        Assert.Equal(10, ReportMath.Integrate(points, 0, 2 * H)!.Value, 6);
        Assert.Equal(1.25, ReportMath.Integrate(points, 0, H / 2)!.Value, 6); // 截断处线性插值
        Assert.Equal(5, ReportMath.Integrate(points, 4 * H, 6 * H)!.Value, 6);
        Assert.Null(ReportMath.Integrate(points, 2 * H, 4 * H));
        // 采样密度不同不影响结果（小时均值近似会偏向密集段）。
        var uneven = new[] { P(0, 0), P(60_000, 0), P(120_000, 0), P(H, 60) };
        Assert.Equal(ReportMath.Integrate([P(120_000, 0), P(H, 60)], 0, H), ReportMath.Integrate(uneven, 0, H));
    }

    [Fact]
    public async Task DayReportRowsAndSummaryFromHistory()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var store = new FakeHistory();
        // 测点 1：表底，每 10 分钟 +1，08:30 复位到 0 后继续；测点 2：功率恒 10 kW，09:00~10:00 坏质量。
        double meter = 1000;
        for (var t = Day - H; t <= Day + 25 * H; t += 600_000)
        {
            if (t == Day + 8 * H + H / 2) meter = 0; else meter += 1;
            store.Add(1, P(t, meter));
            store.Add(2, P(t, 10, t >= Day + 9 * H && t < Day + 10 * H ? Scada.Core.Enums.TagQuality.Bad : Scada.Core.Enums.TagQuality.Good));
        }
        var builder = new ReportBuilder(new ReportService(store), factory,
            new Scada.Data.AccessModelProvider(_ => Task.FromResult(Scada.Core.Security.AccessModel.Build([], [], [], [], [], 0))));
        var columns = new ReportColumn[]
        {
            new() { Key = "flow", Title = "表底差", Kind = ReportColumnKind.TagDiff, TagId = 1 },
            new() { Key = "kwh", Title = "电量", Kind = ReportColumnKind.TagSum, TagId = 2 },
            new() { Key = "avg", Title = "功率均值", Kind = ReportColumnKind.TagAvg, TagId = 2 },
            new() { Key = "ratio", Title = "单耗", Kind = ReportColumnKind.Formula, Formula = "$kwh / $flow" }
        };
        var template = new ReportTemplate { Name = "日报", Period = ReportPeriod.Day, ColumnsJson = System.Text.Json.JsonSerializer.Serialize(columns, ReportTemplates.Json) };
        var table = await builder.BuildAsync(template, "2026-10-01", Day, Day + 24 * H, Utc, default);
        Assert.Equal(24, table.Rows.Count);
        Assert.Equal(6, table.Rows[0].Values[0]);
        Assert.Equal(5, table.Rows[8].Values[0]); // 复位小时：08:10~08:20 +2，复位 0 计 0，之后 08:40/08:50/09:00 +3
        Assert.Equal(10, table.Rows[0].Values[1]!.Value, 6);
        // 坏质量 09:00~10:00：好样本 08:50 与 10:00 间隔 70 分钟超过 1 小时，不桥接 → 09 时为空，08 时只积到 08:50。
        Assert.Null(table.Rows[9].Values[1]);
        Assert.Equal(10 * 50 / 60.0, table.Rows[8].Values[1]!.Value, 6);
        Assert.Equal(10 / 6.0, table.Rows[0].Values[3]!.Value, 6);
        // 合计：差值 = 逐行之和（复位后不出现负数），累计求和，公式按合计重算。
        var rowsFlow = table.Rows.Sum(r => r.Values[0]!.Value);
        Assert.Equal(rowsFlow, table.Summary.Values[0]); Assert.Equal(143, rowsFlow);
        Assert.Equal(240 - 10 - 10 / 6.0, table.Summary.Values[1]!.Value, 6);
        Assert.Equal(10, table.Summary.Values[2]!.Value, 6);
        Assert.Equal((240 - 10 - 10 / 6.0) / rowsFlow, table.Summary.Values[3]!.Value, 6);
    }

    private sealed class Factory(Microsoft.Data.Sqlite.SqliteConnection connection) : Microsoft.EntityFrameworkCore.IDbContextFactory<Scada.Data.ConfigDbContext>
    {
        public Scada.Data.ConfigDbContext CreateDbContext() => new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Scada.Data.ConfigDbContext>().UseSqlite(connection).Options);
    }

    private sealed class FakeHistory : Scada.Core.Interfaces.IHistoryStore
    {
        private readonly Dictionary<int, List<Scada.Core.Models.HistoryPoint>> _points = new();
        public void Add(int tag, Scada.Core.Models.HistoryPoint p) { if (!_points.TryGetValue(tag, out var list)) _points[tag] = list = new(); list.Add(p); }
        public Task<IReadOnlyList<Scada.Core.Models.HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Scada.Core.Models.HistorySeries>>(tagIds.Select(t => new Scada.Core.Models.HistorySeries { TagId = t, Points = (_points.GetValueOrDefault(t) ?? []).Where(p => p.Ts >= from && p.Ts <= to).ToList() }).ToList());
        public Task<IReadOnlyList<Scada.Core.Models.AggregatePoint>> QueryHourAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Scada.Core.Models.AggregatePoint>>(tagIds.SelectMany(t => (_points.GetValueOrDefault(t) ?? []).Where(p => p.Ts >= from && p.Ts < to && p.Quality == Scada.Core.Enums.TagQuality.Good)
                .GroupBy(p => p.Ts / H * H).Select(g => new Scada.Core.Models.AggregatePoint(t, g.Key, g.Average(p => p.Value), g.Min(p => p.Value), g.Max(p => p.Value), g.Count()))).ToList());
        public Task<IReadOnlyList<Scada.Core.Models.AggregatePoint>> QueryAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, Scada.Core.Models.HistoryResolution resolution, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Scada.Core.Models.AggregatePoint>>([]);
        public Task WriteRawAsync(IReadOnlyCollection<Scada.Core.Models.HistoryValue> values, CancellationToken ct) => Task.CompletedTask;
        public Task WriteAggregate5MinAsync(IReadOnlyCollection<Scada.Core.Models.AggregateValue> values, CancellationToken ct) => Task.CompletedTask;
        public Task WriteAggregateHourAsync(IReadOnlyCollection<Scada.Core.Models.AggregateValue> values, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<Scada.Core.Models.HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken ct) => Task.FromResult<IReadOnlyList<Scada.Core.Models.HistorySeries>>([]);
        public Task<IReadOnlyList<Scada.Core.Models.HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken ct) => Task.FromResult<IReadOnlyList<Scada.Core.Models.HistorySeries>>([]);
        public Task<IReadOnlyList<Scada.Core.Models.AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken ct) => Task.FromResult<IReadOnlyList<Scada.Core.Models.AggregateValue>>([]);
        public Task<IReadOnlyList<Scada.Core.Models.AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken ct) => Task.FromResult<IReadOnlyList<Scada.Core.Models.AggregateValue>>([]);
    }
}
