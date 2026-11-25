using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Configuration;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class AlarmRuleExcelRevisionTests
{
    [Fact]
    public async Task ApplyPreviewRejectsWhenRuleRevisionChangedAfterPreview()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open();
        var runtimeFactory = TestRuntime.Factory(runtimeConnection);

        var device = new Device { Name = "dev", Enabled = false, AreaId = Area.DefaultId };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        var tag = new Tag { DeviceId = device.Id, Name = "temp", Area = TagArea.HoldingRegister, Address = 1, DataType = TagDataType.UInt16 };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var rule = new AlarmRule
        {
            TagId = tag.Id, Key = "rule-a", Type = AlarmRuleType.High, Threshold = 80, Level = AlarmLevel.Warning, Revision = 1, Enabled = true
        };
        db.AlarmRules.Add(rule);
        await db.SaveChangesAsync();

        var changed = new AlarmRule
        {
            Id = rule.Id, Key = rule.Key, TagId = tag.Id, Type = AlarmRuleType.High, Threshold = 90, Level = AlarmLevel.Warning, Revision = 1, Enabled = true
        };
        var service = new AlarmRuleExcelService();
        using var source = new MemoryStream(service.ExportCsv([changed], new Dictionary<int, string> { [tag.Id] = tag.Name }));
        var preview = await service.PreviewAsync(source, "rules.csv", 7, factory, runtimeFactory, default);
        Assert.True(preview.CanApply, string.Join("; ", preview.Errors));
        Assert.True(preview.Updated >= 1, string.Join("; ", preview.Errors));

        rule.Revision = 2;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConfigRevisionConflictException>(() =>
            service.ApplyPreviewAsync(7, preview.Id, factory, runtimeFactory, 7, "engineer", default));
        db.ChangeTracker.Clear();
        Assert.Equal(80, (await db.AlarmRules.SingleAsync(x => x.Id == rule.Id)).Threshold);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
}
