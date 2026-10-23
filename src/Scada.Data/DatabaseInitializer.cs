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

public static class DatabaseInitializer
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
            await SeedDemoDataAsync(db, cancellationToken);
            SeedDemoAlarms(db);
            SeedDemoScreen(db);
            await db.SaveChangesAsync(cancellationToken);
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
    }

    private static void SeedDemoAlarms(ConfigDbContext db)
    {
        var tags = db.Tags.Where(x => x.DeviceId == db.Devices.Select(d => d.Id).First()).ToDictionary(x => x.Name);
        db.AlarmRules.AddRange(
            new AlarmRule { TagId = tags["出口温度"].Id, Type = AlarmRuleType.HighHigh, Threshold = 118, Level = AlarmLevel.Critical, DelaySec = 3, RecoverDeadband = 1, Message = "出口温度高高限" },
            new AlarmRule { TagId = tags["出口温度"].Id, Type = AlarmRuleType.High, Threshold = 110, Level = AlarmLevel.Warning, DelaySec = 5, RecoverDeadband = 1, Message = "出口温度高限" },
            new AlarmRule { TagId = tags["汽包水位"].Id, Type = AlarmRuleType.Low, Threshold = -80, Level = AlarmLevel.Warning, DelaySec = 3, RecoverDeadband = 5, Message = "汽包水位低" },
            new AlarmRule { TagId = tags["汽包水位"].Id, Type = AlarmRuleType.LowLow, Threshold = -120, Level = AlarmLevel.Critical, DelaySec = 3, RecoverDeadband = 5, Message = "汽包水位低低" },
            new AlarmRule { TagId = tags["烟气含氧量"].Id, Type = AlarmRuleType.High, Threshold = 9, Level = AlarmLevel.Info, DelaySec = 5, RecoverDeadband = 0.3, Message = "烟气含氧量偏高" },
            new AlarmRule { TagId = tags["给水泵运行"].Id, Type = AlarmRuleType.StateChange, Threshold = 0.5, Level = AlarmLevel.Info, DelaySec = 0, RecoverDeadband = 0, Message = "给水泵状态变化" });
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
