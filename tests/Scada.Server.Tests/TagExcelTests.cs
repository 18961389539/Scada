using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public class TagExcelTests
{
    [Fact]
    public async Task ImportRejectsWorkbookAboveConfiguredRowLimit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection);
        var device = new Device { Name = "plc", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var service = new TagExcelService(Microsoft.Extensions.Options.Options.Create(new Scada.Core.Options.ScadaSettings { TagImportMaxRows = 2 }));
        var tags = Enumerable.Range(1, 3).Select(i => new Tag { DeviceId = device.Id, Name = $"t{i}", Address = i }).ToArray();
        using var stream = new MemoryStream(service.Export([device], tags));
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default);
        Assert.Equal(0, result.Created); Assert.Contains(result.Errors, x => x.Contains("行上限"));
        db.ChangeTracker.Clear(); Assert.Empty(await db.Tags.ToListAsync());
    }

    [Fact]
    public async Task CalculationsRoundTripAcrossDifferentIdsPreservesDependenciesDisabledStateAndAudit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "plc", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        db.Tags.Add(new() { Id = 100, DeviceId = device.Id, Name = "unrelated" }); await db.SaveChangesAsync();
        var physical = new Tag { Id = 1, DeviceId = device.Id, Name = "source", Address = 32 };
        var first = new Tag { Id = 2, Kind = TagKind.Calculated, Name = "first", Expression = "#1 * 2", DataType = TagDataType.Double };
        var second = new Tag { Id = 3, Kind = TagKind.Calculated, Name = "second", Expression = "#2 + #1", DataType = TagDataType.Double, CalculationEnabled = false, CalculationIntervalMs = 250, InputMaxAgeMs = 3000, Unit = "kW" };
        var service = new TagExcelService(); using var stream = new MemoryStream(service.Export([device], [physical, first, second]));
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default, 42, "engineer"); Assert.Empty(result.Errors); Assert.Equal(3, result.Created);
        db.ChangeTracker.Clear(); var source = await db.Tags.SingleAsync(x => x.Name == "source"); var a = await db.Tags.SingleAsync(x => x.Name == "first"); var b = await db.Tags.SingleAsync(x => x.Name == "second");
        Assert.True(source.Id > 100); Assert.Equal($"#{source.Id} * 2", a.Expression); Assert.Equal($"#{a.Id} + #{source.Id}", b.Expression);
        Assert.Null(b.DeviceId); Assert.False(b.CalculationEnabled); Assert.False(b.Writable); Assert.Equal(250, b.CalculationIntervalMs); Assert.Equal("kW", b.Unit);
        Assert.Equal(3, await db.TagDependencies.CountAsync());
        var logs = await runtime.OperationLogs.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.Equal(2, logs.Count(x => x.Action.StartsWith("导入计算测点：")));
        Assert.All(logs, x => { Assert.Equal(42, x.UserId); Assert.Equal("engineer", x.UserName); });
        var summary = logs.Single(x => x.Action == "导入测点 Excel");
        Assert.Equal("成功", summary.Result);
        Assert.Equal("Tag", summary.TargetType);
        Assert.Null(summary.TagId);
        Assert.Contains("\"Created\":3", summary.Details);
        Assert.Contains("\"calculationRowLogs\":2", summary.Details);
        Assert.Contains("计算测点已按行记录，本条仅为整表汇总", summary.Details);
        using var again = new MemoryStream(service.Export([device], await db.Tags.AsNoTracking().ToListAsync()));
        var update = await service.ImportAsync(again, factory, runtimeFactory, default); Assert.Empty(update.Errors); Assert.Equal(4, update.Updated);
        db.ChangeTracker.Clear(); Assert.Equal(2, (await db.Tags.FindAsync(a.Id))!.CalculationRevision); Assert.Equal(3, await db.TagDependencies.CountAsync());
    }

    [Theory]
    [InlineData("missing")][InlineData("cycle")][InlineData("invalid-enable")][InlineData("fraction-id")][InlineData("duplicate-id")][InlineData("physical-error")]
    public async Task InvalidCalculationWorkbookRollsBackPhysicalAndCalculatedChangesTogether(string fault)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "plc", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var physical = new Tag { Id = 1, DeviceId = device.Id, Name = "source", Address = 12 }; db.Tags.Add(physical); await db.SaveChangesAsync();
        var calculated = new Tag { Id = 2, Kind = TagKind.Calculated, Name = "computed", Expression = "#1+1", DataType = TagDataType.Double };
        var service = new TagExcelService(); using var exported = new MemoryStream(service.Export([device], [physical, calculated])); using var book = new XLWorkbook(exported);
        book.Worksheet(1).Cell(2, 5).Value = 99; var sheet = book.Worksheet("计算测点");
        switch (fault)
        {
            case "missing": sheet.Cell(2, 3).Value = "#999"; break;
            case "cycle": sheet.Cell(2, 3).Value = "#2"; break;
            case "invalid-enable": sheet.Cell(2, 6).Value = "maybe"; break;
            case "fraction-id": sheet.Cell(2, 1).Value = 2.5; break;
            case "duplicate-id": sheet.Cell(2, 1).Value = 1; break;
            case "physical-error": book.Worksheet(1).Cell(2, 4).Value = "unknown"; break;
        }
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default); Assert.NotEmpty(result.Errors); Assert.Equal(0, result.Created); Assert.Equal(0, result.Updated);
        db.ChangeTracker.Clear(); Assert.Equal(12, (await db.Tags.SingleAsync()).Address); Assert.Empty(await db.TagDependencies.ToListAsync()); Assert.Empty(await runtime.OperationLogs.ToListAsync());
    }

    [Fact]
    public async Task S7ExcelRoundTripPreservesDbAndBitOffsets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "S7", Protocol = ProtocolType.SiemensS7, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var tag = new Tag { DeviceId = device.Id, Name = "bit", Area = TagArea.DataBlock, Address = 100, DbNumber = 17, BitOffset = 6, DataType = TagDataType.Bool, Writable = true };
        db.Tags.Add(tag); await db.SaveChangesAsync();
        var service = new TagExcelService(); var bytes = service.Export([device], [tag]);
        using var stream = new MemoryStream(bytes); var result = await service.ImportAsync(stream, factory, runtimeFactory, default);
        Assert.Equal(1, result.Updated); Assert.Empty(result.Errors);
        db.ChangeTracker.Clear(); var read = await db.Tags.SingleAsync(); Assert.Equal(17, read.DbNumber); Assert.Equal(6, read.BitOffset); Assert.Equal(100, read.Address);
    }

    [Fact]
    public async Task InvalidImportedUpdateCannotMutateExistingTrackedTag()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "S7", Protocol = ProtocolType.SiemensS7, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var tag = new Tag { DeviceId = device.Id, Name = "bit", Area = TagArea.DataBlock, Address = 100, DbNumber = 17, BitOffset = 6, DataType = TagDataType.Bool };
        db.Tags.Add(tag); await db.SaveChangesAsync(); var service = new TagExcelService();
        using var source = new MemoryStream(service.Export([device], [tag])); using var book = new XLWorkbook(source);
        var sheet = book.Worksheet(1); sheet.Cell(2, 17).Value = 99; sheet.Cell(2, 3).Value = "invalid edit must not persist";
        sheet.Row(2).CopyTo(sheet.Row(3)); sheet.Cell(3, 2).Value = "new-valid"; sheet.Cell(3, 17).Value = 1;
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default);
        Assert.Equal(1, result.Created); Assert.Equal(0, result.Updated); Assert.Equal(1, result.Skipped); Assert.Single(result.Errors);
        db.ChangeTracker.Clear(); var read = await db.Tags.SingleAsync(x => x.Name == "bit"); Assert.Equal(6, read.BitOffset); Assert.Null(read.Description);
        Assert.Equal(1, (await db.Tags.SingleAsync(x => x.Name == "new-valid")).BitOffset);
    }

    [Fact]
    public async Task LegacyFifteenColumnExcelStillImportsModbusTags()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "modbus", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        using var source = new MemoryStream(new TagExcelService().Export([device], [new Tag { DeviceId = device.Id, Name = "legacy", Address = 123 }]));
        using var book = new XLWorkbook(source); book.Worksheet(1).Columns(16, 18).Delete();
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await new TagExcelService().ImportAsync(stream, factory, runtimeFactory, default);
        Assert.Equal(1, result.Created); Assert.Empty(result.Errors);
        var tag = await db.Tags.SingleAsync(); Assert.Equal(123, tag.Address); Assert.Equal(1, tag.DbNumber); Assert.Equal(0, tag.BitOffset);
    }

    [Fact]
    public async Task OpcNodeIdsSurviveExcelRoundTrip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "opc", Protocol = ProtocolType.OpcUa, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var tag = new Tag { DeviceId = device.Id, Name = "Temperature", Area = TagArea.OpcNode, NodeId = "nsu=urn:plant;s=Boiler.Temperature", DataType = TagDataType.Float };
        var service = new TagExcelService(); using var stream = new MemoryStream(service.Export([device], [tag]));
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default); Assert.Equal(1, result.Created); Assert.Empty(result.Errors);
        Assert.Equal(tag.NodeId, (await db.Tags.SingleAsync()).NodeId);
    }

    [Fact]
    public async Task ImportRejectsFiveEffectiveLanesIncludingDeviceDefault()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "档位泵", PollIntervalMs = 1000, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        db.Tags.AddRange(Physical(device.Id, "a", 1, 100), Physical(device.Id, "b", 2, 200), Physical(device.Id, "c", 3, 300), Physical(device.Id, "d", 4, 400));
        await db.SaveChangesAsync();

        var service = new TagExcelService();
        using var stream = new MemoryStream(service.Export([device], [
            Physical(device.Id, "a", 1, 100), Physical(device.Id, "b", 2, 200), Physical(device.Id, "c", 3, 300), Physical(device.Id, "d", 4, 400),
            Physical(device.Id, "follow", 5, null)
        ]));
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default);
        Assert.Equal(0, result.Created); Assert.Equal(0, result.Updated); Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, x => x.Contains("档位泵") && x.Contains("1000ms（设备默认）") && x.Contains("5 档"));
        db.ChangeTracker.Clear();
        Assert.Equal(4, await db.Tags.CountAsync());
        Assert.False(await db.Tags.AnyAsync(x => x.Name == "follow"));
        Assert.Empty(await runtime.OperationLogs.ToListAsync());
    }

    [Fact]
    public async Task ImportAllowsFourEffectiveLanesIncludingDeviceDefault()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "四档泵", PollIntervalMs = 1000, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var service = new TagExcelService();
        using var stream = new MemoryStream(service.Export([device], [
            Physical(device.Id, "a", 1, 100), Physical(device.Id, "b", 2, 200), Physical(device.Id, "c", 3, 300), Physical(device.Id, "follow", 4, null)
        ]));
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default);
        Assert.Equal(4, result.Created); Assert.Empty(result.Errors);
        db.ChangeTracker.Clear();
        Assert.Equal(4, await db.Tags.CountAsync());
        Assert.Null((await db.Tags.SingleAsync(x => x.Name == "follow")).ScanRateMs);
    }

    [Fact]
    public async Task PhysicalImportWritesOneSummaryAuditWithoutPerRowLogs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection); await using var runtime = runtimeFactory.CreateDbContext();
        var device = new Device { Name = "modbus", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        db.Tags.Add(new Tag { DeviceId = device.Id, Name = "temp", Area = TagArea.HoldingRegister, Address = 1, DataType = TagDataType.UInt16 });
        await db.SaveChangesAsync();
        var service = new TagExcelService();
        using var source = new MemoryStream(service.Export([device], [
            new Tag { DeviceId = device.Id, Name = "temp", Area = TagArea.HoldingRegister, Address = 2, DataType = TagDataType.UInt16 },
            new Tag { DeviceId = device.Id, Name = "flow", Area = TagArea.HoldingRegister, Address = 3, DataType = TagDataType.UInt16 }
        ]));
        using var book = new XLWorkbook(source);
        var sheet = book.Worksheet(1);
        sheet.Row(3).CopyTo(sheet.Row(4));
        sheet.Cell(4, 1).Value = "missing-device";
        sheet.Cell(4, 2).Value = "orphan";
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await service.ImportAsync(stream, factory, runtimeFactory, default, 7, "operator", "tags-plant.xlsx");
        Assert.Equal(1, result.Created); Assert.Equal(1, result.Updated); Assert.Equal(1, result.Skipped); Assert.Single(result.Errors);
        var logs = await runtime.OperationLogs.AsNoTracking().ToListAsync();
        var summary = Assert.Single(logs);
        Assert.Equal("导入测点 Excel", summary.Action);
        Assert.Equal(7, summary.UserId);
        Assert.Equal("operator", summary.UserName);
        Assert.Equal("成功", summary.Result);
        Assert.Equal("Tag", summary.TargetType);
        Assert.Null(summary.TagId);
        Assert.Contains("\"file\":\"tags-plant.xlsx\"", summary.Details);
        Assert.Contains("\"Created\":1", summary.Details);
        Assert.Contains("\"Updated\":1", summary.Details);
        Assert.Contains("\"Skipped\":1", summary.Details);
        Assert.Contains("missing-device", summary.Details);
        Assert.DoesNotContain("calculationRowLogs", summary.Details);
        Assert.DoesNotContain("计算测点已按行记录", summary.Details);
        Assert.DoesNotContain(logs, x => x.Action.StartsWith("导入计算测点："));
    }

    [Fact]
    public async Task PreviewDoesNotWriteAndApplyPersistsAfterConfirm()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection);
        var device = new Device { Name = "modbus", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var service = new TagExcelService();
        using var source = new MemoryStream(service.Export([device], [
            new Tag { DeviceId = device.Id, Name = "flow", Area = TagArea.HoldingRegister, Address = 3, DataType = TagDataType.UInt16 }
        ]));
        var preview = await service.PreviewAsync(source, "tags.xlsx", owner: 7, factory, runtimeFactory, default);
        Assert.Equal(1, preview.Created);
        Assert.Equal(0, preview.Updated);
        Assert.True(preview.CanApply);
        Assert.Contains(ExcelImportImpact.ReloadWarning, preview.Warnings);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Tags.ToListAsync());
        Assert.Empty(await runtimeFactory.CreateDbContext().OperationLogs.ToListAsync());

        var applied = await service.ApplyPreviewAsync(7, preview.Id, factory, runtimeFactory, 7, "engineer", default);
        Assert.Equal(1, applied.Created);
        db.ChangeTracker.Clear();
        Assert.Single(await db.Tags.ToListAsync());
    }

    [Fact]
    public async Task ApplyPreviewRejectsWhenTagTableChangedAfterPreview()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        await using var runtimeConnection = TestRuntime.Open(); var runtimeFactory = TestRuntime.Factory(runtimeConnection);
        var device = new Device { Name = "modbus", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var service = new TagExcelService();
        using var source = new MemoryStream(service.Export([device], [
            new Tag { DeviceId = device.Id, Name = "flow", Area = TagArea.HoldingRegister, Address = 3, DataType = TagDataType.UInt16 }
        ]));
        var preview = await service.PreviewAsync(source, "tags.xlsx", owner: 7, factory, runtimeFactory, default);
        db.Tags.Add(new Tag { DeviceId = device.Id, Name = "temp", Area = TagArea.HoldingRegister, Address = 1, DataType = TagDataType.UInt16 });
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyPreviewAsync(7, preview.Id, factory, runtimeFactory, 7, "engineer", default));
        Assert.Contains("重新预览", ex.Message);
        db.ChangeTracker.Clear();
        Assert.DoesNotContain(await db.Tags.ToListAsync(), x => x.Name == "flow");
    }

    private static Tag Physical(int deviceId, string name, int address, int? scanRateMs) => new()
    {
        DeviceId = deviceId, Name = name, Area = TagArea.HoldingRegister, Address = address,
        DataType = TagDataType.UInt16, ScanRateMs = scanRateMs
    };

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
}
