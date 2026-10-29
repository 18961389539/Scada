using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using HistorySample = Scada.Core.Models.HistoryValue;

namespace Scada.Data;

public static partial class DatabaseInitializer
{
    private static readonly JsonSerializerOptions DemoJson = new(JsonSerializerDefaults.Web);

    private static async Task SeedBusinessExamplesAsync(ConfigDbContext db, IHistoryStore? history, CancellationToken cancellationToken)
    {
        var now = ScadaTime.NowMs();
        var devices = await db.Devices.ToDictionaryAsync(x => x.Name, cancellationToken);
        var tags = await db.Tags.ToListAsync(cancellationToken);
        var tester = devices["EOL 电性能检测台"];
        var lineDevice = devices["A01 电机智能装配线"];
        Tag Point(Device device, string name) => tags.Single(x => x.DeviceId == device.Id && x.Name == name);

        foreach (var device in devices.Values.Where(x => x.Id != devices["1# 锅炉"].Id))
        {
            var deviceTags = tags.Where(x => x.DeviceId == device.Id).ToList();
            ScreenDocument document;
            if (device.Id == lineDevice.Id) document = CreateAssemblyLineScreen(device, deviceTags);
            else
            {
                document = new ScreenDocument { Height = 720 };
                document.Nodes.Add(new ScreenNode { Id = "title", Type = "text", Text = device.Name, X = 30, Y = 20, Width = 1140, Height = 50 });
                var analogTags = deviceTags.Where(x => x.DataType != TagDataType.Bool).ToList();
                var index = 0;
                foreach (var tag in deviceTags)
                {
                    var column = index % 4;
                    var row = index / 4;
                    var x = 30 + column * 290;
                    var y = 100 + row * 160;
                    document.Nodes.Add(new ScreenNode { Id = $"label-{tag.Id}", Type = "text", Text = tag.Name, X = x, Y = y, Width = 260, Height = 36 });
                    document.Nodes.Add(tag.DataType == TagDataType.Bool
                        ? new ScreenNode { Id = $"state-{tag.Id}", Type = "led", Text = tag.Name, TagId = tag.Id, X = x + 108, Y = y + 48, Width = 44, Height = 44, Min = 0, Max = 1 }
                        : new ScreenNode
                        {
                            Id = $"value-{tag.Id}", Type = "value", Text = tag.Name, TagId = tag.Id, Unit = tag.Unit ?? "",
                            X = x, Y = y + 45, Width = 260, Height = 65,
                            Min = tag.MinValue ?? 0, Max = tag.MaxValue ?? 100
                        });
                    index++;
                }
                if (analogTags.Count >= 2)
                {
                    var chartTags = analogTags.Take(3).Select(x => x.Id).ToList();
                    document.Nodes.Add(new ScreenNode { Id = "trend", Type = "chart-line", Text = "关键参数趋势", X = 30, Y = 545, Width = 620, Height = 150, Tags = chartTags, WindowSec = 3600 });
                    document.Nodes.Add(new ScreenNode { Id = "compare", Type = "chart-bar", Text = "当前值对比", X = 680, Y = 545, Width = 400, Height = 150, Tags = chartTags });
                }
            }
            var json = JsonSerializer.Serialize(document, DemoJson);
            db.ProcessScreens.Add(new ProcessScreen
            {
                Name = device.Name + "运行总览", PublishedName = device.Name + "运行总览",
                DraftJson = json, PublishedJson = json, Revision = 1, PublishedRevision = 1,
                UpdatedTs = now, UpdatedBy = "DEMO"
            });
        }

        var alarmExamples = new[]
        {
            (Device: devices["厂区低压配电总表"], Tag: "A 相电流", Type: AlarmRuleType.High, Limit: 220.0, Level: AlarmLevel.Warning, Message: "配电进线 A 相负荷偏高"),
            (Device: devices["厂区低压配电总表"], Tag: "功率因数", Type: AlarmRuleType.Low, Limit: 0.9, Level: AlarmLevel.Warning, Message: "功率因数偏低，请检查无功补偿"),
            (Device: devices["1# 螺杆空压机"], Tag: "排气温度", Type: AlarmRuleType.High, Limit: 95.0, Level: AlarmLevel.Critical, Message: "空压机排气高温，请检查冷却和润滑"),
            (Device: devices["1# 螺杆空压机"], Tag: "过滤器压差", Type: AlarmRuleType.High, Limit: 25.0, Level: AlarmLevel.Warning, Message: "进气过滤器压差偏高，建议安排保养"),
            (Device: devices["循环冷却水站"], Tag: "水箱液位", Type: AlarmRuleType.Low, Limit: 20.0, Level: AlarmLevel.Critical, Message: "循环水箱低液位，请检查补水"),
            (Device: tester, Tag: "绝缘电阻", Type: AlarmRuleType.Low, Limit: 100.0, Level: AlarmLevel.Warning, Message: "产品绝缘电阻不合格"),
            (Device: tester, Tag: "振动速度", Type: AlarmRuleType.High, Limit: 4.5, Level: AlarmLevel.Warning, Message: "产品振动超限，请复检轴承装配"),
            (Device: lineDevice, Tag: "压装压力", Type: AlarmRuleType.High, Limit: 6.5, Level: AlarmLevel.Warning, Message: "压装压力偏高，请检查工装定位"),
            (Device: devices["2# 冷冻站"], Tag: "冷冻水供水温度", Type: AlarmRuleType.High, Limit: 10.0, Level: AlarmLevel.Warning, Message: "冷冻水供水温度偏高，冷机负荷不足"),
            (Device: devices["2# 冷冻站"], Tag: "冷冻水压差", Type: AlarmRuleType.Low, Limit: 110.0, Level: AlarmLevel.Warning, Message: "冷冻水压差偏低，请检查水泵与过滤器"),
            (Device: devices["污水处理站"], Tag: "出水 COD", Type: AlarmRuleType.High, Limit: 50.0, Level: AlarmLevel.Critical, Message: "出水 COD 超排放限值"),
            (Device: devices["污水处理站"], Tag: "出水 pH", Type: AlarmRuleType.Low, Limit: 6.0, Level: AlarmLevel.Critical, Message: "出水 pH 低于排放下限"),
            (Device: devices["10kV 高压配电室"], Tag: "变压器温度", Type: AlarmRuleType.High, Limit: 85.0, Level: AlarmLevel.Critical, Message: "主变绕组温度高，请检查冷却与负荷"),
            (Device: devices["10kV 高压配电室"], Tag: "功率因数", Type: AlarmRuleType.Low, Limit: 0.9, Level: AlarmLevel.Warning, Message: "高压侧功率因数偏低"),
            (Device: devices["光伏储能站"], Tag: "电池温度", Type: AlarmRuleType.High, Limit: 50.0, Level: AlarmLevel.Warning, Message: "储能电池温度偏高"),
            (Device: devices["光伏储能站"], Tag: "电池 SOC", Type: AlarmRuleType.Low, Limit: 20.0, Level: AlarmLevel.Warning, Message: "储能电量偏低，请安排充电")
        };
        foreach (var example in alarmExamples)
            db.AlarmRules.Add(new AlarmRule { TagId = Point(example.Device, example.Tag).Id, Type = example.Type, Threshold = example.Limit, Level = example.Level, DelaySec = 3, RecoverDeadband = 0.05, Message = example.Message });
        await db.SaveChangesAsync(cancellationToken);

        var rules = await db.AlarmRules.OrderBy(x => x.Id).ToListAsync(cancellationToken);
        for (var episode = 0; episode < 5; episode++)
        {
            foreach (var rule in rules)
            {
                var tag = tags.Single(x => x.Id == rule.TagId);
                var start = now - (episode * 30L + 2) * 3600000 - rule.Id * 60000L;
                db.AlarmEvents.Add(new AlarmEvent
                {
                    TagId = tag.Id, DeviceId = tag.DeviceId, RuleId = rule.Id, Level = rule.Level,
                    Value = rule.Type is AlarmRuleType.Low or AlarmRuleType.LowLow ? rule.Threshold * 0.8 : rule.Threshold * 1.1,
                    StartTs = start, EndTs = start + (3 + episode) * 60000,
                    AckTs = episode == 0 ? null : start + 90000, AckUser = episode == 0 ? null : "admin",
                    Message = "[DEMO] " + rule.Message, FirstOut = rule.Id == rules[0].Id
                });
            }

        }

        var inspectionMapping = new TraceabilityOutcomeMapping(1, 2);
        var fields = DemoSignals.Inspections[0].Values.Keys.Select(name => new TraceabilityField(name, Point(tester, name).Id,
            name == "检测判定" ? inspectionMapping : null)).ToArray();
        var webJob = new TraceabilityJob { Name = "[DEMO] 电机终检 · 网页扫码", TriggerTagId = Point(tester, "检测触发").Id, SnSource = TraceabilitySnSource.WebScan, FieldsJson = JsonSerializer.Serialize(fields, DemoJson) };
        var plcJob = new TraceabilityJob { Name = "[DEMO] 电机终检 · PLC SN", TriggerTagId = Point(tester, "PLC SN 检测触发").Id, SnSource = TraceabilitySnSource.Plc, SnTagIdsJson = JsonSerializer.Serialize(new[] { Point(tester, "产品 SN").Id }), FieldsJson = webJob.FieldsJson };
        db.TraceabilityJobs.AddRange(webJob, plcJob);
        await db.SaveChangesAsync(cancellationToken);
        for (var day = 0; day < 7; day++)
        {
            for (var index = 0; index < DemoSignals.Inspections.Count; index++)
            {
                var sample = DemoSignals.Inspections[index];
                var ts = now - day * 86400000L - (index + 1) * 300000L;
                foreach (var job in new[] { webJob, plcJob })
                {
                    var cycle = Guid.NewGuid();
                    db.TraceabilityAttempts.Add(new TraceabilityAttempt { Id = cycle, JobId = job.Id, StartedTs = ts - 200, ClosedTs = ts + 300, Reply = 2, Acknowledged = true });
                    db.TraceabilityRecords.Add(new TraceabilityRecord
                    {
                        CycleId = cycle, JobId = job.Id, AreaId = tester.AreaId, JobName = job.Name, Ts = ts, Acknowledged = true,
                        Sn = job.SnSource == TraceabilitySnSource.WebScan ? $"DEMO-MTR-{day:D2}-{sample.Sn}" : (2600001 + day * 100 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Outcome = inspectionMapping.Evaluate(sample.Values["检测判定"]),
                        ValuesJson = JsonSerializer.Serialize(fields.Select(field => new TraceabilityValue(field.Name, field.TagId, sample.Values[field.Name],
                            Point(tester, field.Name).Unit, field.OutcomeMapping?.Evaluate(sample.Values[field.Name]))), DemoJson)
                    });
                }
            }
            db.TraceabilityAttempts.Add(new TraceabilityAttempt { Id = Guid.NewGuid(), JobId = webJob.Id, StartedTs = now - day * 86400000L - 7200000, ClosedTs = now - day * 86400000L - 7199000, Reply = 3, Acknowledged = true, Error = "[DEMO] 未绑定有效 SN，拒绝保存无 SN 数据，已回复 3" });
        }

        var products = new[]
        {
            new ProductionProduct { Name = "M80 标准电机", Code = "M80-STD", AreaId = lineDevice.AreaId, IdealCycleSec = 12, Description = "客户演示：80 系列标准电机，220 V / 750 W", UpdatedTs = now, UpdatedBy = "DEMO" },
            new ProductionProduct { Name = "M80 低噪声电机", Code = "M80-LOW", AreaId = lineDevice.AreaId, IdealCycleSec = 15, Description = "客户演示：低噪声轴承与精密动平衡工艺", UpdatedTs = now, UpdatedBy = "DEMO" },
            new ProductionProduct { Name = "M100 高扭矩电机", Code = "M100-HT", AreaId = lineDevice.AreaId, IdealCycleSec = 18, Description = "客户演示：100 系列高扭矩电机，220 V / 1.5 kW", UpdatedTs = now, UpdatedBy = "DEMO" }
        };
        db.ProductionProducts.AddRange(products);
        db.ProductionShifts.AddRange(
            new ProductionShift { Name = "白班 08:00-20:00", AreaId = lineDevice.AreaId, StartMinute = 480, EndMinute = 1200, UpdatedTs = now, UpdatedBy = "DEMO" },
            new ProductionShift { Name = "夜班 20:00-08:00", AreaId = lineDevice.AreaId, StartMinute = 1200, EndMinute = 480, UpdatedTs = now, UpdatedBy = "DEMO" });
        var reasons = new[]
        {
            new DowntimeReason { Code = "DEMO-MATERIAL", Name = "上料工位缺料", AreaId = lineDevice.AreaId },
            new DowntimeReason { Code = "DEMO-FAULT", Name = "压装工位传感器故障", AreaId = lineDevice.AreaId },
            new DowntimeReason { Code = "DEMO-SETUP", Name = "产品换型与首件确认", AreaId = lineDevice.AreaId, IsPlanned = true },
            new DowntimeReason { Code = "DEMO-MAINT", Name = "计划点检与设备保养", AreaId = lineDevice.AreaId, IsPlanned = true }
        };
        db.DowntimeReasons.AddRange(reasons);
        await db.SaveChangesAsync(cancellationToken);
        var line = new ProductionLine
        {
            Name = lineDevice.Name, AreaId = lineDevice.AreaId, Description = "[DEMO] 投入、良品、不良品和停机事件共同计算 OEE",
            StateTagId = Point(lineDevice, "运行状态码").Id, StateMapJson = "{\"0\":\"Stopped\",\"1\":\"Running\",\"2\":\"Fault\"}",
            GoodCountTagId = Point(lineDevice, "累计良品数").Id, RejectCountTagId = Point(lineDevice, "累计不良品数").Id,
            TotalCountTagId = Point(lineDevice, "累计总产量").Id, ProductId = products[0].Id,
            IdealCycleSec = 12, TargetUnitsPerHour = 280, UpdatedTs = now, UpdatedBy = "DEMO"
        };
        db.ProductionLines.Add(line);
        await db.SaveChangesAsync(cancellationToken);
        var lastHour = now / 3600000 * 3600000;
        for (var hour = 0; hour < 7 * 24; hour++)
        {
            var from = lastHour - (hour + 1L) * 3600000;
            var reason = reasons[hour % reasons.Length];
            var downMinutes = 3 + hour % 9;
            db.DowntimeEvents.Add(new DowntimeEvent { LineId = line.Id, ReasonId = reason.Id, StartTs = from + 600000, EndTs = from + (10 + downMinutes) * 60000, State = hour % 4 == 1 ? LineState.Fault : hour % 4 == 2 ? LineState.Setup : LineState.Stopped, Note = "[DEMO] 模拟停机，已完成原因确认", CreatedBy = "DEMO", CreatedTs = from + 600000 });
            db.ProductionCountCorrections.Add(new ProductionCountCorrection { LineId = line.Id, ProductId = products[0].Id, PeriodFrom = from, PeriodTo = from + 3600000, GoodDelta = 226 + hour % 25, RejectDelta = 2 + hour % 6, ScrapDelta = hour % 3, Reason = "[DEMO] 历史生产样本，非真实现场产量", CreatedBy = "DEMO", CreatedTs = from + 3600000 });
        }

        var recipeTemplate = new RecipeTemplate
        {
            Name = "[DEMO] 电机装配工艺", AreaId = lineDevice.AreaId, Description = "产品换型参数；仅连接本机模拟器时演示下发",
            ParametersJson = JsonSerializer.Serialize(new[]
            {
                new RecipeParameter { Key = "cycle", Name = "标准节拍", TagId = Point(lineDevice, "目标节拍设定").Id, Unit = "s", Min = 5, Max = 60, DefaultValue = 12 },
                new RecipeParameter { Key = "torque", Name = "锁付扭矩", TagId = Point(lineDevice, "扭矩设定").Id, Unit = "N·m", Min = 4, Max = 12, DefaultValue = 8 }
            }, DemoJson), UpdatedTs = now, UpdatedBy = "DEMO"
        };
        db.RecipeTemplates.Add(recipeTemplate);
        await db.SaveChangesAsync(cancellationToken);
        for (var index = 0; index < products.Length; index++)
        {
            var recipe = new Recipe { TemplateId = recipeTemplate.Id, Name = products[index].Name + "工艺配方", Description = products[index].Description, CurrentVersion = 2, UpdatedTs = now, UpdatedBy = "DEMO" };
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync(cancellationToken);
            for (var version = 1; version <= 2; version++)
                db.RecipeVersions.Add(new RecipeVersion { RecipeId = recipe.Id, Version = version, ValuesJson = JsonSerializer.Serialize(new[] { new RecipeValue { Key = "cycle", Value = products[index].IdealCycleSec + (version == 1 ? 1 : 0) }, new RecipeValue { Key = "torque", Value = 8 + index } }, DemoJson), Note = version == 1 ? "[DEMO] 工艺试制参数" : "[DEMO] 首件确认后优化节拍，保留旧版本用于对比", CreatedTs = now - (3 - version) * 86400000L, CreatedBy = "DEMO" });
        }

        var power = devices["厂区低压配电总表"];
        var boiler = devices["1# 锅炉"];
        db.ReportTemplates.AddRange(
            new ReportTemplate { Name = "[DEMO] 公用工程能源日报", AreaId = power.AreaId, ColumnsJson = JsonSerializer.Serialize(new[]
            {
                new ReportColumn { Key = "energy", Title = "用电量", Kind = ReportColumnKind.TagDiff, TagId = Point(power, "累计用电量").Id, Unit = "kWh" },
                new ReportColumn { Key = "load", Title = "平均负荷", Kind = ReportColumnKind.TagAvg, TagId = Point(power, "有功功率").Id, Unit = "kW" },
                new ReportColumn { Key = "peak", Title = "最大负荷", Kind = ReportColumnKind.TagMax, TagId = Point(power, "有功功率").Id, Unit = "kW" },
                new ReportColumn { Key = "factor", Title = "平均功率因数", Kind = ReportColumnKind.TagAvg, TagId = Point(power, "功率因数").Id, Decimals = 3 },
                new ReportColumn { Key = "water", Title = "补水量", Kind = ReportColumnKind.TagDiff, TagId = Point(devices["循环冷却水站"], "累计补水量").Id, Unit = "m³" },
                new ReportColumn { Key = "alarms", Title = "区域报警数", Kind = ReportColumnKind.AlarmCount, Decimals = 0 }
            }, DemoJson), UpdatedTs = now },
            new ReportTemplate { Name = "[DEMO] 锅炉运行日报", AreaId = boiler.AreaId, ColumnsJson = JsonSerializer.Serialize(new[]
            {
                new ReportColumn { Key = "temp", Title = "平均蒸汽温度", Kind = ReportColumnKind.TagAvg, TagId = Point(boiler, "出口温度").Id, Unit = "℃" },
                new ReportColumn { Key = "pressure", Title = "平均蒸汽压力", Kind = ReportColumnKind.TagAvg, TagId = Point(boiler, "出口压力").Id, Unit = "MPa", Decimals = 3 },
                new ReportColumn { Key = "steam", Title = "累计蒸汽量", Kind = ReportColumnKind.TagSum, TagId = Point(boiler, "主蒸汽流量").Id, Unit = "t" },
                new ReportColumn { Key = "oxygen", Title = "平均含氧量", Kind = ReportColumnKind.TagAvg, TagId = Point(boiler, "烟气含氧量").Id, Unit = "%" }
            }, DemoJson), UpdatedTs = now },
            new ReportTemplate { Name = "[DEMO] 装配线生产日报", AreaId = line.AreaId, ColumnsJson = JsonSerializer.Serialize(new[]
            {
                new ReportColumn { Key = "good", Title = "良品数", Kind = ReportColumnKind.OeeKpi, Output = "good", Decimals = 0 },
                new ReportColumn { Key = "reject", Title = "不良品数", Kind = ReportColumnKind.OeeKpi, Output = "reject", Decimals = 0 },
                new ReportColumn { Key = "availability", Title = "时间开动率", Kind = ReportColumnKind.OeeKpi, Output = "availability", Factor = 100, Unit = "%" },
                new ReportColumn { Key = "performance", Title = "性能开动率", Kind = ReportColumnKind.OeeKpi, Output = "performance", Factor = 100, Unit = "%" },
                new ReportColumn { Key = "quality", Title = "良品率", Kind = ReportColumnKind.OeeKpi, Output = "quality", Factor = 100, Unit = "%" },
                new ReportColumn { Key = "oee", Title = "综合设备效率", Kind = ReportColumnKind.OeeKpi, Output = "oee", Factor = 100, Unit = "%" }
            }, DemoJson), UpdatedTs = now });
        await db.SaveChangesAsync(cancellationToken);

        if (history is not null)
        {
            var signals = DemoSignals.All.ToDictionary(x => (x.Area, x.Address));
            var values = new List<HistorySample>();
            var end = now / 300000 * 300000;
            for (var ts = end - 7L * 86400000; ts < end; ts += 300000)
                foreach (var tag in tags)
                    if (signals.TryGetValue((tag.Area, tag.Address), out var signal))
                        values.Add(new HistorySample(tag.Id, ts, Math.Round(signal.ValueAt((ts - end) / 1000.0), 4), TagQuality.Good));
            await history.WriteRawAsync(values, cancellationToken);
            await history.WriteAggregate5MinAsync(values.Select(x => new AggregateValue(x.TagId, x.Ts, x.Value, x.Value, x.Value, 1)).ToArray(), cancellationToken);
            var hourly = values.GroupBy(x => (x.TagId, Ts: x.Ts / 3600000 * 3600000))
                .Select(group => new AggregateValue(group.Key.TagId, group.Key.Ts, group.Average(x => x.Value), group.Min(x => x.Value), group.Max(x => x.Value), group.Count())).ToArray();
            await history.WriteAggregateHourAsync(hourly, cancellationToken);
        }
    }

    private static ScreenDocument CreateAssemblyLineScreen(Device device, IReadOnlyCollection<Tag> deviceTags)
    {
        var tags = deviceTags.ToDictionary(x => x.Name);
        var document = new ScreenDocument { Width = 1600, Height = 440 };
        document.Nodes.Add(new ScreenNode { Id = "title", Type = "text", Text = device.Name, X = 24, Y = 4, Width = 1536, Height = 24, Color = "#dce6f0" });
        void Section(string id, string text, double x, double y, double width) =>
            document.Nodes.Add(new ScreenNode { Id = id, Type = "rectangle", Text = text, X = x, Y = y, Width = width, Height = 24, Color = "#40556e" });
        void Metric(string name, string label, double x, double y, double width = 288, int decimals = 1, List<ScreenState>? states = null)
        {
            var tag = tags[name];
            document.Nodes.Add(new ScreenNode { Id = $"label-{tag.Id}", Type = "text", Text = label, X = x, Y = y, Width = width, Height = 22, Color = "#b6c4d5" });
            document.Nodes.Add(new ScreenNode
            {
                Id = $"value-{tag.Id}", Type = "value", Text = label, TagId = tag.Id, Unit = tag.Unit ?? "",
                X = x, Y = y + 22, Width = width, Height = 32, Decimals = decimals,
                Min = tag.MinValue ?? 0, Max = tag.MaxValue ?? 100, States = states
            });
        }
        Section("group-production", "产量与质量", 24, 34, 912);
        Section("group-cycle", "生产节拍", 960, 34, 600);
        Metric("累计总产量", "累计总产量", 24, 58, decimals: 0);
        Metric("累计良品数", "累计良品数", 336, 58, decimals: 0);
        Metric("累计不良品数", "累计不良品数", 648, 58, decimals: 0);
        Metric("实时生产节拍", "当前生产节拍（实际）", 960, 58);
        Metric("目标节拍设定", "目标节拍（设定）", 1272, 58);
        Section("group-process", "工艺参数与设定", 24, 118, 1536);
        Metric("压装压力", "压装压力（实际）", 24, 142);
        Metric("锁付扭矩", "锁付扭矩（实际）", 336, 142);
        Metric("扭矩设定", "锁付扭矩（设定）", 648, 142);
        Metric("轴承温度", "轴承温度（实际）", 960, 142);
        Metric("输送线速度", "输送线速度（实际）", 1272, 142);
        Section("group-state", "设备状态", 24, 202, 1536);
        Metric("运行状态码", "运行状态", 24, 226, 496, 0,
            [new(0, "停机", "#b6c4d5"), new(1, "运行", "#dce6f0"), new(2, "故障", "#ff8e8e")]);
        Metric("装配线运行", "装配线运行反馈", 544, 226, 496, 0,
            [new(0, "未运行", "#b6c4d5"), new(1, "运行", "#dce6f0")]);
        Metric("缺料等待", "缺料等待反馈", 1064, 226, 496, 0,
            [new(0, "未等待", "#b6c4d5"), new(1, "等待中", "#ffd166")]);
        var chartTags = new[] { "累计总产量", "累计良品数", "累计不良品数" }.Select(name => tags[name].Id).ToList();
        document.Nodes.Add(new ScreenNode { Id = "trend", Type = "chart-line", Text = "产量趋势", X = 24, Y = 288, Width = 920, Height = 128, Tags = chartTags, WindowSec = 3600 });
        document.Nodes.Add(new ScreenNode { Id = "compare", Type = "chart-bar", Text = "产量对比", X = 960, Y = 288, Width = 600, Height = 128, Tags = chartTags });
        return document;
    }
}
