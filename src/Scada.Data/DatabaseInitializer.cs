using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Core.Models;
using System.Text.Json;

namespace Scada.Data;

public static partial class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var settings = services.GetRequiredService<IOptions<ScadaSettings>>().Value;
        var configPath = Path.GetFullPath(settings.ConfigDbPath);
        var configDirectory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

        Directory.CreateDirectory(settings.HistoryRoot);
        Directory.CreateDirectory(settings.BackupRoot);

        var factory = services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);

        if (!await db.Users.AnyAsync(cancellationToken))
        {
            db.Users.Add(new User
            {
                UserName = "admin",
                DisplayName = "系统管理员",
                PasswordHash = PasswordHasher.Hash("admin123"),
                Role = UserRole.Admin,
                Enabled = true
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        if (settings.SeedDemoData && !await db.Devices.AnyAsync(cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await SeedDemoDataAsync(db, cancellationToken);
            SeedDemoAlarms(db);
            SeedDemoScreen(db);
            await db.SaveChangesAsync(cancellationToken);
            await SeedBusinessExamplesAsync(db, services.GetService<IHistoryStore>(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var snapshot = services.GetRequiredService<IConfigSnapshot>();
        await snapshot.ReloadAsync(cancellationToken);
    }

    private static async Task SeedDemoDataAsync(ConfigDbContext db, CancellationToken cancellationToken)
    {
        var device = new Device
        {
            Name = "1# 锅炉",
            Protocol = ProtocolType.ModbusTcp,
            Host = "127.0.0.1",
            Port = 1502,
            StationNumber = 1,
            ByteOrder = ByteOrder.ABCD,
            PollIntervalMs = 1000,
            TimeoutMs = 3000,
            Enabled = true
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);

        var tags = new[]
        {
            NewTag(device, "出口温度", "过热器出口主蒸汽温度", TagArea.HoldingRegister, 0, TagDataType.Float, 1, 0, "℃", false, 0.2, 0, 200),
            NewTag(device, "出口压力", "过热器出口主蒸汽压力", TagArea.HoldingRegister, 2, TagDataType.Float, 1, 0, "MPa", false, 0.01, 0, 2.5),
            NewTag(device, "汽包水位", "汽包水位", TagArea.HoldingRegister, 22, TagDataType.Float, 1, 0, "mm", false, 0.5, -200, 200),
            NewTag(device, "给水泵运行", "给水泵运行状态", TagArea.Coil, 0, TagDataType.Bool, 1, 0, "", false, 0, 0, 1),
            NewTag(device, "给水泵频率", "给水泵变频器频率", TagArea.HoldingRegister, 5, TagDataType.Float, 1, 0, "Hz", true, 0.05, 0, 50),
            NewTag(device, "烟气含氧量", "省煤器出口烟气含氧量", TagArea.HoldingRegister, 7, TagDataType.Float, 1, 0, "%", false, 0.05, 0, 15),
            NewTag(device, "炉膛负压", "炉膛负压", TagArea.HoldingRegister, 9, TagDataType.Float, 1, 0, "Pa", false, 1, -200, 100),
            NewTag(device, "主蒸汽流量", "主蒸汽流量", TagArea.HoldingRegister, 11, TagDataType.Float, 1, 0, "t/h", false, 0.2, 0, 120),
            NewTag(device, "排烟温度", "空预器出口排烟温度", TagArea.HoldingRegister, 13, TagDataType.Float, 1, 0, "℃", false, 0.2, 0, 200),
            NewTag(device, "鼓风机电流", "送风机电流", TagArea.HoldingRegister, 15, TagDataType.Float, 1, 0, "A", false, 0.1, 0, 100),
            NewTag(device, "引风机电流", "引风机电流", TagArea.HoldingRegister, 17, TagDataType.Float, 1, 0, "A", false, 0.1, 0, 100),
            NewTag(device, "急停按钮", "现场急停按钮状态", TagArea.DiscreteInput, 0, TagDataType.Bool, 1, 0, "", false, 0, 0, 1)
        };
        db.Tags.AddRange(tags);
        await db.SaveChangesAsync(cancellationToken);
        await SeedPlantStationsAsync(db, device, cancellationToken);
    }

    private static async Task SeedPlantStationsAsync(ConfigDbContext db, Device boiler, CancellationToken cancellationToken)
    {
        var utilities = new Area { Name = "公用工程区", Description = "锅炉、空压站、循环水、配电与高压进线，展示设备监控与能源管理" };
        var assembly = new Area { Name = "智能装配区", Description = "电机装配线，展示产量、节拍、设备效率和停机分析" };
        var quality = new Area { Name = "质量检测区", Description = "成品电性能检测，展示扫码绑定、PLC 触发采集和 SN 质量追溯" };
        var hvac = new Area { Name = "暖通空调区", Description = "冷冻站与空调水系统，展示冷水机组、水泵、冷却塔的能效与联锁" };
        var waterEnv = new Area { Name = "水务与环保区", Description = "污水处理与排放监测，展示进出水水质、曝气与提升泵运行" };
        var energy = new Area { Name = "新能源区", Description = "光伏发电与储能，展示发电功率、电池 SOC 与并网状态" };
        db.Areas.AddRange(utilities, assembly, quality, hvac, waterEnv, energy);
        await db.SaveChangesAsync(cancellationToken);
        boiler.AreaId = utilities.Id;

        Device Station(string name, Area area) => new()
        {
            Name = name, AreaId = area.Id, Protocol = ProtocolType.ModbusTcp,
            Host = "127.0.0.1", Port = 1502, StationNumber = 1, ByteOrder = ByteOrder.ABCD,
            PollIntervalMs = 1000, TimeoutMs = 3000, Enabled = true
        };
        var line = Station("A01 电机智能装配线", assembly);
        var tester = Station("EOL 电性能检测台", quality);
        var power = Station("厂区低压配电总表", utilities);
        var compressor = Station("1# 螺杆空压机", utilities);
        var water = Station("循环冷却水站", utilities);
        var chiller = Station("2# 冷冻站", hvac);
        var wwtp = Station("污水处理站", waterEnv);
        var hv = Station("10kV 高压配电室", utilities);
        var solar = Station("光伏储能站", energy);
        db.Devices.AddRange(line, tester, power, compressor, water, chiller, wwtp, hv, solar);
        await db.SaveChangesAsync(cancellationToken);

        Tag Point(Device device, string name, string description, int address, string unit, double min, double max, bool writable = false) =>
            NewTag(device, name, description, TagArea.HoldingRegister, address, TagDataType.Float, 1, 0, unit, writable, 0.01, min, max);
        Tag State(Device device, string name, string description, int address) =>
            NewTag(device, name, description, TagArea.Coil, address, TagDataType.Bool, 1, 0, "", false, 0, 0, 1);

        db.Tags.AddRange(
            Point(line, "累计总产量", "装配线累计投入数量，供 OEE 产能分析使用", 100, "件", 0, 10000000),
            Point(line, "累计良品数", "通过终检的累计数量，供良品率分析使用", 102, "件", 0, 10000000),
            Point(line, "累计不良品数", "不良品累计数量，供质量损失分析使用", 104, "件", 0, 1000000),
            Point(line, "实时生产节拍", "相邻产品完成时间差，标准节拍为 12 秒", 106, "s", 0, 120),
            Point(line, "压装压力", "转子压装工位压力，正常范围 4.5 至 6.5 MPa", 108, "MPa", 0, 10),
            Point(line, "锁付扭矩", "端盖螺钉锁付扭矩，工艺目标为 8 N·m", 110, "N·m", 0, 20),
            Point(line, "轴承温度", "压装后轴承温度，用于工艺稳定性监测", 112, "℃", 0, 120),
            Point(line, "输送线速度", "装配输送线速度反馈", 114, "m/min", 0, 30),
            Point(line, "运行状态码", "0=停机，1=运行，2=故障，供生产效率分析使用", 116, "", 0, 2),
            Point(line, "目标节拍设定", "装配线标准节拍，仅用于本机模拟器演示写入", 118, "s", 5, 60, true),
            Point(line, "扭矩设定", "端盖锁付扭矩目标，仅用于本机模拟器演示写入", 120, "N·m", 4, 12, true),
            State(line, "装配线运行", "自动模式下输送和装配工位运行反馈", 10),
            State(line, "缺料等待", "上料工位缺料等待反馈", 11),

            Point(tester, "检测触发", "0=待机，1=请求采集，2=采集成功，3=采集失败", 200, "", 0, 3, true),
            Point(tester, "产品 SN", "PLC 数值型产品序列号，与网页扫码方式二选一", 202, "", 0, 9999999),
            Point(tester, "测试电压", "电机空载检测电压，标准范围 210 至 240 V", 204, "V", 0, 300),
            Point(tester, "测试电流", "电机空载检测电流，标准范围 1.2 至 2.8 A", 206, "A", 0, 10),
            Point(tester, "绝缘电阻", "绕组对外壳绝缘电阻，合格下限 100 MΩ", 208, "MΩ", 0, 1000),
            Point(tester, "绕组电阻", "电机绕组直流电阻，标准范围 12 至 16 Ω", 210, "Ω", 0, 50),
            Point(tester, "空载转速", "电机空载稳定转速，标准范围 1400 至 1500 rpm", 212, "rpm", 0, 3000),
            Point(tester, "振动速度", "电机轴承座振动速度有效值，合格上限 4.5 mm/s", 214, "mm/s", 0, 20),
            Point(tester, "检测温度", "电性能检测时的产品表面温度", 216, "℃", 0, 100),
            Point(tester, "测试时长", "单件电性能检测持续时间", 218, "s", 0, 120),
            Point(tester, "检测判定", "0=待检测，1=合格，2=不合格", 220, "", 0, 2),
            Point(tester, "PLC SN 检测触发", "PLC SN 任务独立握手：0=待机，1=采集，2=成功，3=失败", 222, "", 0, 3, true),
            State(tester, "检测台就绪", "安全门闭合且测试仪器就绪", 20),

            Point(power, "A 相电压", "低压配电母线 A 相电压有效值", 300, "V", 0, 300),
            Point(power, "B 相电压", "低压配电母线 B 相电压有效值", 302, "V", 0, 300),
            Point(power, "C 相电压", "低压配电母线 C 相电压有效值", 304, "V", 0, 300),
            Point(power, "A 相电流", "厂区进线 A 相电流有效值", 306, "A", 0, 500),
            Point(power, "B 相电流", "厂区进线 B 相电流有效值", 308, "A", 0, 500),
            Point(power, "C 相电流", "厂区进线 C 相电流有效值", 310, "A", 0, 500),
            Point(power, "有功功率", "厂区实时总有功功率，用于负荷趋势和报表", 312, "kW", 0, 500),
            Point(power, "功率因数", "进线综合功率因数，目标不低于 0.9", 314, "", 0, 1),
            Point(power, "累计用电量", "电表累计正向有功电能，用于时段能耗统计", 316, "kWh", 0, 10000000),
            Point(power, "电网频率", "厂区进线电网频率", 318, "Hz", 45, 55),

            Point(compressor, "供气压力", "压缩空气总管压力，正常范围 0.65 至 0.8 MPa", 400, "MPa", 0, 1.2),
            Point(compressor, "排气温度", "螺杆主机排气温度，高温报警上限 95 ℃", 402, "℃", 0, 130),
            Point(compressor, "润滑油温", "空压机润滑油循环温度", 404, "℃", 0, 120),
            Point(compressor, "电机电流", "空压机主电机电流", 406, "A", 0, 200),
            Point(compressor, "供气流量", "压缩空气总管瞬时体积流量", 408, "m³/min", 0, 30),
            Point(compressor, "累计运行时间", "空压机累计运行时间，供保养计划参考", 410, "h", 0, 100000),
            Point(compressor, "过滤器压差", "进气过滤器压差，维护建议上限 25 kPa", 412, "kPa", 0, 50),
            Point(compressor, "压力设定值", "供气压力目标，仅用于本机模拟器演示写入", 414, "MPa", 0.5, 0.85, true),
            State(compressor, "空压机运行", "主电机运行反馈", 30),
            State(compressor, "加载状态", "空压机加载供气反馈", 31),

            Point(water, "供水温度", "循环冷却水供水温度，工艺目标 25 ℃", 500, "℃", 0, 60),
            Point(water, "回水温度", "循环冷却水回水温度", 502, "℃", 0, 80),
            Point(water, "供水压力", "循环冷却水泵出口压力", 504, "MPa", 0, 1),
            Point(water, "供水流量", "循环水总管瞬时流量", 506, "m³/h", 0, 200),
            Point(water, "水箱液位", "循环水箱液位百分比，低液位报警下限 20%", 508, "%", 0, 100),
            Point(water, "水泵频率", "循环水泵变频器输出频率", 510, "Hz", 0, 50),
            Point(water, "电导率", "循环水电导率，反映循环水浓缩程度", 512, "μS/cm", 0, 3000),
            Point(water, "累计补水量", "循环系统累计新水补充量", 514, "m³", 0, 1000000),
            State(water, "循环泵运行", "循环水泵运行反馈", 40),
            State(water, "冷却塔风机运行", "冷却塔风机运行反馈", 41),

            Point(chiller, "冷冻水供水温度", "冷冻水供水温度，目标 7 ℃", 600, "℃", 0, 20),
            Point(chiller, "冷冻水回水温度", "冷冻水回水温度，反映末端负荷", 602, "℃", 0, 25),
            Point(chiller, "冷却水供水温度", "冷却塔出水温度", 604, "℃", 0, 45),
            Point(chiller, "冷却水回水温度", "冷凝器回水温度", 606, "℃", 0, 50),
            Point(chiller, "冷冻水流量", "冷冻水循环流量", 608, "m³/h", 0, 300),
            Point(chiller, "冷水机组电流", "冷水机组压缩机电流", 610, "A", 0, 300),
            Point(chiller, "冷冻水泵频率", "冷冻水泵变频器频率", 612, "Hz", 0, 50),
            Point(chiller, "冷却塔风机频率", "冷却塔风机变频器频率", 614, "Hz", 0, 50),
            Point(chiller, "冷冻水压差", "供回水压差，用于旁通阀联锁", 616, "kPa", 0, 300),
            Point(chiller, "机组负载率", "冷水机组负载率", 618, "%", 0, 100),
            State(chiller, "冷冻泵运行", "冷冻水泵运行反馈", 60),
            State(chiller, "冷却塔风机运行", "冷却塔风机运行反馈", 61),

            Point(wwtp, "进水流量", "污水处理站进水瞬时流量", 700, "m³/h", 0, 500),
            Point(wwtp, "进水 COD", "进水化学需氧量", 702, "mg/L", 0, 1000),
            Point(wwtp, "出水 COD", "出水化学需氧量，排放限值 50 mg/L", 704, "mg/L", 0, 200),
            Point(wwtp, "出水 pH", "出水酸碱度，排放范围 6 至 9", 706, "pH", 0, 14),
            Point(wwtp, "曝气池溶解氧", "好氧池溶解氧，工艺目标 2 至 4 mg/L", 708, "mg/L", 0, 10),
            Point(wwtp, "污泥浓度", "曝气池混合液污泥浓度", 710, "g/L", 0, 10),
            Point(wwtp, "提升泵电流", "污水提升泵电流", 712, "A", 0, 200),
            Point(wwtp, "鼓风机频率", "曝气鼓风机变频器频率", 714, "Hz", 0, 50),
            Point(wwtp, "出水浊度", "出水浊度", 716, "NTU", 0, 50),
            Point(wwtp, "累计处理量", "累计污水处理量", 718, "m³", 0, 10000000),
            State(wwtp, "提升泵运行", "污水提升泵运行反馈", 70),
            State(wwtp, "曝气风机运行", "曝气鼓风机运行反馈", 71),

            Point(hv, "进线电压", "10kV 进线电压", 800, "kV", 0, 12),
            Point(hv, "进线电流", "10kV 进线电流", 802, "A", 0, 600),
            Point(hv, "有功功率", "高压进线有功功率", 804, "kW", 0, 5000),
            Point(hv, "无功功率", "高压进线无功功率", 806, "kvar", 0, 2000),
            Point(hv, "功率因数", "高压进线功率因数", 808, "", 0, 1),
            Point(hv, "电网频率", "高压侧电网频率", 810, "Hz", 45, 55),
            Point(hv, "变压器温度", "主变绕组温度，高温报警上限 85 ℃", 812, "℃", 0, 120),
            Point(hv, "母线电压", "10kV 母线电压", 814, "kV", 0, 12),
            Point(hv, "电缆温度", "进线电缆接头温度", 816, "℃", 0, 90),
            State(hv, "主进线断路器", "主进线断路器合闸反馈", 80),
            State(hv, "母联断路器", "母联断路器合闸反馈", 81),

            Point(solar, "光伏发电功率", "光伏阵列实时发电功率", 900, "kW", 0, 2000),
            Point(solar, "组件温度", "光伏组件背板温度", 902, "℃", 0, 80),
            Point(solar, "辐照度", "水平面总辐照度", 904, "W/m²", 0, 1200),
            Point(solar, "逆变器效率", "逆变器转换效率", 906, "%", 0, 100),
            Point(solar, "电池 SOC", "储能电池荷电状态", 908, "%", 0, 100),
            Point(solar, "充电功率", "储能充电功率", 910, "kW", 0, 500),
            Point(solar, "放电功率", "储能放电功率", 912, "kW", 0, 500),
            Point(solar, "并网点电压", "并网点交流电压", 914, "V", 0, 450),
            Point(solar, "当日发电量", "光伏当日累计发电量", 916, "kWh", 0, 100000),
            Point(solar, "电池温度", "储能电池舱温度", 918, "℃", 0, 60),
            State(solar, "逆变器运行", "逆变器运行反馈", 90),
            State(solar, "并网状态", "并网开关状态", 91));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void SeedDemoAlarms(ConfigDbContext db)
    {
        var deviceId = db.Devices.Where(x => x.Name == "1# 锅炉").Select(x => x.Id).Single();
        var tags = db.Tags.Where(x => x.DeviceId == deviceId).ToDictionary(x => x.Name);
        db.AlarmRules.AddRange(
            new AlarmRule
            {
                TagId = tags["出口温度"].Id, Type = AlarmRuleType.HighHigh, Threshold = 118, Level = AlarmLevel.Critical, DelaySec = 3, RecoverDeadband = 1,
                Message = "过热器出口汽温高高限，立即减负荷并投入减温水",
                Cause = "燃烧过强、减温水不足或受热面结焦", Action = "立即减负荷、开大减温水；汽温不降按规程停炉"
            },
            new AlarmRule
            {
                TagId = tags["出口温度"].Id, Type = AlarmRuleType.High, Threshold = 110, Level = AlarmLevel.Warning, DelaySec = 5, RecoverDeadband = 1,
                Message = "过热器出口汽温高限，调整燃烧并检查减温水",
                Cause = "负荷偏高、风煤配比失调或减温水调节滞后", Action = "降低过量空气系数，开大减温水，跟踪汽温趋势"
            },
            new AlarmRule
            {
                TagId = tags["汽包水位"].Id, Type = AlarmRuleType.Low, Threshold = -80, Level = AlarmLevel.Warning, DelaySec = 3, RecoverDeadband = 5,
                Message = "汽包水位低，检查给水泵与给水调节阀",
                Cause = "给水流量不足、给水泵跳闸或调节阀卡涩", Action = "确认给水泵运行，切手动加大给水，必要时降负荷"
            },
            new AlarmRule
            {
                TagId = tags["汽包水位"].Id, Type = AlarmRuleType.LowLow, Threshold = -120, Level = AlarmLevel.Critical, DelaySec = 3, RecoverDeadband = 5,
                Message = "汽包水位低低，立即停炉保护并检查给水系统",
                Cause = "给水中断或严重泄漏，水位已低于保护定值", Action = "立即触发停炉保护，禁止强行上水，检查给水管路"
            },
            new AlarmRule
            {
                TagId = tags["烟气含氧量"].Id, Type = AlarmRuleType.High, Threshold = 9, Level = AlarmLevel.Info, DelaySec = 5, RecoverDeadband = 0.3,
                Message = "烟气含氧量偏高，检查送风配比与炉膛漏风",
                Cause = "过量空气系数偏大、氧量计漂移或炉膛漏风", Action = "校核氧量计，调整送风量，检查看火门与烟道漏风"
            },
            new AlarmRule
            {
                TagId = tags["给水泵运行"].Id, Type = AlarmRuleType.StateChange, Threshold = 0.5, Level = AlarmLevel.Info, DelaySec = 0, RecoverDeadband = 0,
                Message = "给水泵运行状态变化，确认是否为正常启停",
                Cause = "人为启停、联锁动作或电源/控制回路故障", Action = "核对操作记录与联锁；非预期停机立即切换备用泵"
            });
    }

    private static void SeedDemoScreen(ConfigDbContext db)
    {
        var deviceId = db.Devices.Where(x => x.Name == "1# 锅炉").Select(x => x.Id).Single();
        var tags = db.Tags.Where(x => x.DeviceId == deviceId).ToDictionary(x => x.Name);
        ScreenNode Node(string id, string type, string text, double x, double y, double width, double height, string? tagName = null, string unit = "") => new()
        {
            Id = id, Type = type, Text = text, X = x, Y = y, Width = width, Height = height,
            TagId = tagName is not null ? tags[tagName].Id : null, Unit = unit,
            Min = tagName is not null ? tags[tagName].MinValue ?? 0 : 0,
            Max = tagName is not null ? tags[tagName].MaxValue ?? 100 : 100
        };
        var document = new ScreenDocument
        {
            Height = 680,
            Nodes =
            [
                Node("title", "text", "1# 锅炉 · 工艺运行总览", 30, 20, 1140, 50),
                Node("water-pipe", "pipe", "给水", 100, 360, 330, 40, "给水泵运行"),
                Node("steam-pipe", "pipe", "蒸汽", 560, 200, 500, 40),
                Node("boiler", "tank", "汽包水位", 380, 150, 230, 270, "汽包水位", "mm"),
                Node("pump", "pump", "给水泵", 130, 320, 170, 130, "给水泵运行"),
                Node("temp-label", "text", "出口温度", 630, 130, 180, 40),
                Node("temperature", "value", "出口温度", 630, 170, 180, 40, "出口温度", "℃"),
                Node("pressure-label", "text", "出口压力", 850, 130, 180, 40),
                Node("pressure", "value", "出口压力", 850, 170, 180, 40, "出口压力", "MPa"),
                Node("flow", "gauge", "主蒸汽流量", 900, 300, 200, 160, "主蒸汽流量", "t/h"),
                Node("frequency-label", "text", "给水泵频率", 80, 480, 240, 36),
                Node("frequency", "value", "给水泵频率", 80, 520, 240, 46, "给水泵频率", "Hz"),
                Node("level", "value", "汽包水位", 380, 460, 230, 46, "汽包水位", "mm"),
                Node("oxygen-label", "text", "烟气含氧量", 650, 480, 200, 36),
                Node("oxygen", "value", "烟气含氧量", 650, 520, 200, 46, "烟气含氧量", "%"),
                Node("hint", "text", "通过画面编辑器调整图元与测点绑定，保存草稿后发布生效", 60, 610, 1080, 36)
            ]
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.ProcessScreens.Add(new ProcessScreen
        {
            Name = "锅炉运行总览", PublishedName = "锅炉运行总览", DraftJson = json, PublishedJson = json,
            Revision = 1, PublishedRevision = 1, UpdatedTs = ScadaTime.NowMs(), UpdatedBy = "系统示例"
        });
    }

    private static Tag NewTag(
        Device device,
        string name,
        string description,
        TagArea area,
        int address,
        TagDataType dataType,
        double scale,
        double offset,
        string unit,
        bool writable,
        double deadband,
        double min,
        double max)
    {
        return new Tag
        {
            DeviceId = device.Id,
            Name = name,
            Description = description,
            Area = area,
            Address = address,
            DataType = dataType,
            Scale = scale,
            Offset = offset,
            Unit = unit,
            Writable = writable,
            ArchiveMode = ArchiveMode.OnChange,
            Deadband = deadband,
            ArchivePeriodSec = 600,
            MinValue = min,
            MaxValue = max
        };
    }
}
