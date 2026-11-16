using Scada.Core.Models;

namespace Scada.Data;

// 示例画面按设备类型生成不同版式：工艺流程图、质量看板、能源看板、设备看板与通用总览。
// 画面只依赖测点名称/量程/单位，便于种子数据与既有演示库复用同一套生成规则。
public sealed record DemoScreenTag(int Id, string Name, string Unit, double Min, double Max, bool IsBool = false);

public static class DemoScreenFactory
{
    private sealed class Builder(ScreenDocument document, IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        public ScreenDocument Document { get; } = document;
        public IReadOnlyDictionary<string, DemoScreenTag> Tags { get; } = tags;
        public DemoScreenTag? Find(params string[] names) => names.Select(name => Tags.GetValueOrDefault(name)).FirstOrDefault(tag => tag is not null) is { } tag ? tag : null;

        public void Add(ScreenNode node) => Document.Nodes.Add(node);

        public void Title(string text)
        {
            Add(new ScreenNode { Id = "title", Type = "text", Text = text, X = 24, Y = 12, Width = 1152, Height = 42, FontSize = 22, Color = "#dce6f0" });
        }

        public void Kpis(IEnumerable<DemoScreenTag> source, double y = 66, int count = 4)
        {
            var index = 0;
            foreach (var tag in source.Where(tag => !tag.IsBool).Take(count))
            {
                var x = 24 + index * 288;
                Add(new ScreenNode
                {
                    Id = $"kpi-{tag.Id}", Type = "kpi", Text = tag.Name, TagId = tag.Id, Unit = tag.Unit,
                    X = x, Y = y, Width = 272, Height = 96, Min = tag.Min, Max = tag.Max, Decimals = 1
                });
                index++;
            }
        }

        public void Value(string id, string label, DemoScreenTag tag, double x, double y, double width = 220, double height = 70, int decimals = 1) =>
            Add(new ScreenNode
            {
                Id = id, Type = "value", Text = label, TagId = tag.Id, Unit = tag.Unit,
                X = x, Y = y, Width = width, Height = height, Min = tag.Min, Max = tag.Max, Decimals = decimals
            });

        public void Gauge(string id, string label, DemoScreenTag tag, double x, double y, double width = 240, double height = 190,
            IReadOnlyList<ScreenGaugeBand>? bands = null, double? setpoint = null) =>
            Add(new ScreenNode
            {
                Id = id, Type = "gauge", Text = label, TagId = tag.Id, Unit = tag.Unit,
                X = x, Y = y, Width = width, Height = height, Min = tag.Min, Max = tag.Max,
                GaugeBands = bands?.ToList(), Setpoint = setpoint
            });

        public void Dial(string id, string label, DemoScreenTag tag, double x, double y, double width = 220, double height = 190) =>
            Add(new ScreenNode
            {
                Id = id, Type = "dial", Text = label, TagId = tag.Id, Unit = tag.Unit,
                X = x, Y = y, Width = width, Height = height, Min = tag.Min, Max = tag.Max
            });

        public void Progress(string id, string label, DemoScreenTag tag, double x, double y, double width = 260, double height = 72) =>
            Add(new ScreenNode
            {
                Id = id, Type = "progress", Text = label, TagId = tag.Id, Unit = tag.Unit,
                X = x, Y = y, Width = width, Height = height, Min = tag.Min, Max = tag.Max
            });

        public void Chart(string id, string type, string label, IEnumerable<DemoScreenTag> source, double x, double y, double width, double height,
            double? windowSec = null, int take = 12)
        {
            var ids = source.Where(tag => !tag.IsBool).Select(tag => tag.Id).Distinct().Take(take).ToList();
            if (ids.Count == 0) return;
            Add(new ScreenNode
            {
                Id = id, Type = type, Text = label, Tags = ids, X = x, Y = y, Width = width, Height = height,
                WindowSec = windowSec, Min = 0, Max = 100
            });
        }

        public void Table(string id, string label, IEnumerable<DemoScreenTag> source, double x, double y, double width, double height, bool history = false)
        {
            var ids = source.Where(tag => !tag.IsBool).Select(tag => tag.Id).Distinct().Take(10).ToList();
            if (ids.Count == 0) return;
            Add(new ScreenNode
            {
                Id = id, Type = history ? "history-table" : "table", Text = label, Tags = ids,
                X = x, Y = y, Width = width, Height = height, Min = 0, Max = 100,
                WindowSec = history ? 3600 : null,
                TableColumns = history ? ["name", "last", "min", "max", "avg"] : ["name", "value", "unit"]
            });
        }

        public void StateLeds(IEnumerable<DemoScreenTag> source, double x, double y)
        {
            var index = 0;
            foreach (var tag in source.Where(tag => tag.IsBool).Take(4))
            {
                Add(new ScreenNode
                {
                    Id = $"state-{tag.Id}", Type = "led", Text = tag.Name, TagId = tag.Id,
                    X = x + index * 150, Y = y, Width = 132, Height = 92, Min = 0, Max = 1, ActiveThreshold = 0.5
                });
                index++;
            }
        }

        public void ProcessSymbol(string id, string type, string label, DemoScreenTag? tag, double x, double y, double width, double height, string? animation = null)
        {
            var node = new ScreenNode
            {
                Id = id, Type = type, Text = label, TagId = tag?.Id, Unit = tag?.Unit ?? "",
                X = x, Y = y, Width = width, Height = height,
                Min = tag?.Min ?? 0, Max = tag?.Max ?? 100
            };
            if (tag is not null && animation is not null)
                node.Animation = new ScreenAnimation { Type = animation, Source = "value", TagId = tag.Id, From = tag.Min, To = tag.Max, OutFrom = 0, OutTo = 100 };
            Add(node);
        }
    }

    public static ScreenDocument Create(string deviceName, IReadOnlyDictionary<string, DemoScreenTag> tags) => deviceName switch
    {
        "EOL 电性能检测台" => Quality(tags),
        "厂区低压配电总表" or "10kV 高压配电室" => Power(deviceName, tags),
        "光伏储能站" => Solar(tags),
        "循环冷却水站" or "2# 冷冻站" or "污水处理站" => Process(deviceName, tags),
        "1# 螺杆空压机" => Compressor(tags),
        _ => Generic(deviceName, tags)
    };

    // 跨设备综合画面：驾驶舱、图表展厅与报警中心，用来展示单设备总览之外的能力。
    public static ScreenDocument CreateCockpit(IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New("全厂", tags, "运行驾驶舱");
        var boiler = builder.Find("1# 锅炉:出口温度");
        var output = builder.Find("A01 电机智能装配线:累计总产量");
        var power = builder.Find("厂区低压配电总表:有功功率");
        var solar = builder.Find("光伏储能站:光伏发电功率");
        var hv = builder.Find("10kV 高压配电室:有功功率");
        builder.Kpis(new[] { boiler, output, power, solar }.OfType<DemoScreenTag>());
        builder.Add(new ScreenNode { Id = "clock", Type = "clock", Text = "当前时间", X = 1000, Y = 12, Width = 176, Height = 42 });
        builder.Chart("cockpit-trend", "chart-area", "能源趋势", new[] { power, solar, hv }.OfType<DemoScreenTag>(), 24, 184, 700, 260, 3600);
        builder.Chart("energy-mix", "chart-pie", "能源构成", new[] { power, solar, hv }.OfType<DemoScreenTag>(), 744, 184, 432, 260);
        builder.Add(new ScreenNode { Id = "alarm-stats", Type = "alarm-stats", Text = "报警统计", X = 24, Y = 464, Width = 360, Height = 220, Min = 0, Max = 100 });
        builder.Table("cockpit-table", "关键测点", new[] { boiler, output, power, solar, hv }.OfType<DemoScreenTag>(), 404, 464, 772, 220);
        return builder.Document;
    }

    private static readonly string[] OutputTagNames = ["A01 电机智能装配线:累计总产量", "A01 电机智能装配线:累计良品数", "A01 电机智能装配线:累计不良品数"];
    private static readonly string[] WaterTagNames = ["循环冷却水站:供水温度", "循环冷却水站:回水温度", "循环冷却水站:供水压力", "循环冷却水站:供水流量", "循环冷却水站:水箱液位"];
    private static readonly string[] EnergyTagNames = ["厂区低压配电总表:有功功率", "光伏储能站:光伏发电功率", "10kV 高压配电室:有功功率"];

    public static ScreenDocument CreateChartGallery(IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New("图表示例", tags, "图表与指标展厅");
        var output = OutputTagNames.Select(name => builder.Find(name)).OfType<DemoScreenTag>().ToList();
        var water = WaterTagNames.Select(name => builder.Find(name)).OfType<DemoScreenTag>().ToList();
        var energy = EnergyTagNames.Select(name => builder.Find(name)).OfType<DemoScreenTag>().ToList();
        builder.Chart("pie-output", "chart-pie", "产量构成", output, 24, 70, 370, 260);
        builder.Chart("radar-water", "chart-radar", "水系统指标雷达", water, 414, 70, 370, 260);
        builder.Chart("funnel-output", "chart-funnel", "产量漏斗", output, 804, 70, 372, 260);
        builder.Chart("area-energy", "chart-area", "能源趋势", energy, 24, 350, 560, 310, 3600);
        builder.Table("history-water", "水系统历史统计", water, 604, 350, 572, 310, history: true);
        return builder.Document;
    }

    public static ScreenDocument CreateAlarmCenter(IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New("报警", tags, "报警中心示例");
        builder.Add(new ScreenNode { Id = "alarm-banner", Type = "alarm-banner", Text = "最新报警", X = 24, Y = 66, Width = 1152, Height = 48, Min = 0, Max = 100 });
        builder.Add(new ScreenNode { Id = "alarm-stats", Type = "alarm-stats", Text = "报警统计", X = 24, Y = 130, Width = 360, Height = 240, Min = 0, Max = 100 });
        builder.Add(new ScreenNode { Id = "alarm-list", Type = "alarm-list", Text = "活动报警", X = 404, Y = 130, Width = 772, Height = 520, Min = 0, Max = 100 });
        builder.Add(new ScreenNode
        {
            Id = "alarm-hint", Type = "text", Text = "报警横幅、统计卡与活动列表可直接组合到设备画面或工作台区域；确认与暂挂仍走报警页。",
            X = 24, Y = 400, Width = 360, Height = 120, Color = "#9fb3c8"
        });
        return builder.Document;
    }

    private static Builder New(string deviceName, IReadOnlyDictionary<string, DemoScreenTag> tags, string suffix)
    {
        var document = new ScreenDocument { Width = 1200, Height = 720 };
        var builder = new Builder(document, tags);
        builder.Title($"{deviceName} · {suffix}");
        return builder;
    }

    private static ScreenDocument Generic(string deviceName, IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New(deviceName, tags, "运行总览");
        var points = tags.Values.Where(tag => !tag.IsBool).ToList();
        builder.Kpis(points);
        var first = points.FirstOrDefault();
        if (first is not null) builder.Gauge("gauge-main", first.Name, first, 24, 184, 260, 190);
        builder.Chart("trend", "chart-line", "关键参数趋势", points, 304, 184, 872, 210, 3600);
        builder.Chart("compare", "chart-bar", "当前值对比", points, 24, 414, 560, 270);
        builder.Table("detail", "测点明细", points, 604, 414, 572, 270);
        return builder.Document;
    }

    private static ScreenDocument Quality(IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New("EOL 电性能检测台", tags, "质量检测看板");
        var points = tags.Values.Where(tag => !tag.IsBool).ToList();
        var voltage = builder.Find("测试电压", "检测电压") ?? points.ElementAtOrDefault(0);
        var current = builder.Find("测试电流") ?? points.ElementAtOrDefault(1);
        var insulation = builder.Find("绝缘电阻") ?? points.ElementAtOrDefault(2);
        var speed = builder.Find("空载转速") ?? points.ElementAtOrDefault(3);
        var vibration = builder.Find("振动速度") ?? points.FirstOrDefault();
        var temperature = builder.Find("检测温度", "测试温度") ?? points.FirstOrDefault();
        var result = builder.Find("检测判定") ?? points.FirstOrDefault();

        builder.Kpis(new[] { voltage, current, insulation, speed }.OfType<DemoScreenTag>());
        if (vibration is not null) builder.Gauge("gauge-vibration", "振动速度", vibration, 24, 184, 260, 200,
            [new ScreenGaugeBand(vibration.Min, Math.Min(4.5, vibration.Max - 0.1), "#34d399"), new ScreenGaugeBand(Math.Max(4.5, vibration.Min + 0.1), vibration.Max, "#f87171")]);
        if (temperature is not null) builder.Dial("dial-temperature", "检测温度", temperature, 304, 184, 230, 200);
        builder.StateLeds(tags.Values, 854, 184);
        if (result is not null)
            builder.Value("inspection-result", "检测判定", result, 554, 194, 250, 100, 0);
        builder.Table("inspection-table", "关键检测项", points, 24, 414, 560, 270);
        builder.Chart("inspection-compare", "chart-bar", "检测值对比", points, 604, 414, 572, 270, take: 6);
        return builder.Document;
    }

    private static ScreenDocument Power(string deviceName, IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New(deviceName, tags, "配电与负荷看板");
        var points = tags.Values.Where(tag => !tag.IsBool).ToList();
        var voltage = builder.Find("A 相电压", "母线电压", "进线电压") ?? points.FirstOrDefault();
        var current = builder.Find("A 相电流", "进线电流") ?? points.ElementAtOrDefault(1);
        var power = builder.Find("有功功率") ?? points.ElementAtOrDefault(2);
        var factor = builder.Find("功率因数") ?? points.ElementAtOrDefault(3);
        var extra = builder.Find("累计用电量", "变压器温度") ?? points.ElementAtOrDefault(4);

        builder.Kpis(new[] { voltage, current, power, extra }.OfType<DemoScreenTag>());
        if (power is not null) builder.Gauge("gauge-power", "有功功率", power, 24, 184, 260, 200);
        if (factor is not null) builder.Progress("progress-factor", "功率因数", factor, 24, 404, 260, 74);
        builder.StateLeds(tags.Values, 24, 500);
        builder.Chart("load-trend", "chart-line", "负荷趋势", new[] { power, current, voltage }.OfType<DemoScreenTag>(), 320, 184, 856, 220, 3600);
        builder.Chart("phase-compare", "chart-bar", "三相/分项对比", points, 320, 424, 856, 260, take: 6);
        return builder.Document;
    }

    private static ScreenDocument Solar(IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New("光伏储能站", tags, "发电与储能看板");
        var points = tags.Values.Where(tag => !tag.IsBool).ToList();
        var power = builder.Find("光伏发电功率") ?? points.FirstOrDefault();
        var irradiance = builder.Find("辐照度") ?? points.ElementAtOrDefault(1);
        var soc = builder.Find("电池 SOC") ?? points.ElementAtOrDefault(2);
        var efficiency = builder.Find("逆变器效率") ?? points.ElementAtOrDefault(3);
        var charge = builder.Find("充电功率");
        var discharge = builder.Find("放电功率");

        builder.Kpis(new[] { power, irradiance, soc, efficiency }.OfType<DemoScreenTag>());
        if (soc is not null) builder.Gauge("gauge-soc", "电池 SOC", soc, 24, 184, 260, 200,
            [new ScreenGaugeBand(soc.Min, 20, "#f87171"), new ScreenGaugeBand(20, 80, "#fbbf24"), new ScreenGaugeBand(80, soc.Max, "#34d399")]);
        builder.StateLeds(tags.Values, 854, 184);
        builder.Chart("solar-trend", "chart-line", "发电与充放电功率", new[] { power, charge, discharge }.OfType<DemoScreenTag>(), 304, 184, 520, 210, 3600);
        builder.Chart("temperature", "chart-bar", "组件与电池温度", new[] { builder.Find("组件温度"), builder.Find("电池温度") }.OfType<DemoScreenTag>(), 604, 414, 572, 270, take: 2);
        builder.Table("solar-table", "运行测点", points, 24, 414, 560, 270);
        return builder.Document;
    }

    private static ScreenDocument Process(string deviceName, IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New(deviceName, tags, "工艺流程图");
        var points = tags.Values.Where(tag => !tag.IsBool).ToList();
        var level = builder.Find("水箱液位", "污泥浓度");
        var pump = builder.Find("循环泵运行", "冷冻泵运行", "提升泵运行");
        var frequency = builder.Find("水泵频率", "冷冻水泵频率", "鼓风机频率") ?? pump;
        var gaugeTag = builder.Find("供水温度", "冷冻水供水温度", "出水 COD") ?? points.FirstOrDefault();

        builder.ProcessSymbol("tank", "tank", deviceName.Contains("污水") ? "曝气池" : "水箱", level, 48, 90, 240, 250, "level");
        builder.ProcessSymbol("pump", "pump", "循环泵", pump, 320, 140, 160, 150, "rotate");
        builder.ProcessSymbol("valve", "valve", "调节阀", frequency, 520, 160, 120, 110);
        builder.Add(new ScreenNode { Id = "pipe-in", Type = "pipe", Text = "进水", X = 288, Y = 250, Width = 32, Height = 180, Min = 0, Max = 100 });
        builder.Add(new ScreenNode { Id = "pipe-out", Type = "pipe", Text = "出水", X = 480, Y = 250, Width = 32, Height = 180, Min = 0, Max = 100 });
        foreach (var (tag, index) in points.Take(4).Select((tag, index) => (tag, index)))
            builder.Value($"process-{tag.Id}", tag.Name, tag, 680 + (index % 2) * 250, 90 + (index / 2) * 90, 230, 70);
        if (gaugeTag is not null) builder.Gauge("process-gauge", gaugeTag.Name, gaugeTag, 680, 260, 230, 150);
        builder.Chart("process-trend", "chart-line", "关键参数趋势", points, 48, 430, 560, 260, 3600);
        builder.Table("process-table", "测点明细", points, 640, 430, 536, 260);
        return builder.Document;
    }

    private static ScreenDocument Compressor(IReadOnlyDictionary<string, DemoScreenTag> tags)
    {
        var builder = New("1# 螺杆空压机", tags, "设备运行看板");
        var points = tags.Values.Where(tag => !tag.IsBool).ToList();
        var pressure = builder.Find("供气压力") ?? points.FirstOrDefault();
        var temperature = builder.Find("排气温度") ?? points.ElementAtOrDefault(1);
        var flow = builder.Find("供气流量") ?? points.ElementAtOrDefault(2);
        var runtime = builder.Find("累计运行时间") ?? points.ElementAtOrDefault(3);
        var filter = builder.Find("过滤器压差");

        builder.Kpis(new[] { pressure, temperature, flow, runtime }.OfType<DemoScreenTag>());
        if (pressure is not null) builder.Gauge("gauge-pressure", "供气压力", pressure, 24, 184, 260, 200);
        if (filter is not null) builder.Progress("progress-filter", "过滤器压差", filter, 24, 404, 260, 74);
        builder.StateLeds(tags.Values, 24, 500);
        builder.Chart("pressure-trend", "chart-line", "压力与温度趋势", new[] { pressure, temperature }.OfType<DemoScreenTag>(), 320, 184, 856, 220, 3600);
        builder.Chart("compressor-compare", "chart-bar", "运行参数对比", points, 320, 424, 856, 260, take: 6);
        return builder.Document;
    }
}
