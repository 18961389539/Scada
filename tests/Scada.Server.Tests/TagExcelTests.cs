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
    public async Task CalculationsRoundTripAcrossDifferentIdsPreservesDependenciesDisabledStateAndAudit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        var device = new Device { Name = "plc", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        db.Tags.Add(new() { Id = 100, DeviceId = device.Id, Name = "unrelated" }); await db.SaveChangesAsync();
        var physical = new Tag { Id = 1, DeviceId = device.Id, Name = "source", Address = 32 };
        var first = new Tag { Id = 2, Kind = TagKind.Calculated, Name = "first", Expression = "#1 * 2", DataType = TagDataType.Double };
        var second = new Tag { Id = 3, Kind = TagKind.Calculated, Name = "second", Expression = "#2 + #1", DataType = TagDataType.Double, CalculationEnabled = false, CalculationIntervalMs = 250, InputMaxAgeMs = 3000, Unit = "kW" };
        var service = new TagExcelService(); using var stream = new MemoryStream(service.Export([device], [physical, first, second]));
        var result = await service.ImportAsync(stream, factory, default, 42, "engineer"); Assert.Empty(result.Errors); Assert.Equal(3, result.Created);
        db.ChangeTracker.Clear(); var source = await db.Tags.SingleAsync(x => x.Name == "source"); var a = await db.Tags.SingleAsync(x => x.Name == "first"); var b = await db.Tags.SingleAsync(x => x.Name == "second");
        Assert.True(source.Id > 100); Assert.Equal($"#{source.Id} * 2", a.Expression); Assert.Equal($"#{a.Id} + #{source.Id}", b.Expression);
        Assert.Null(b.DeviceId); Assert.False(b.CalculationEnabled); Assert.False(b.Writable); Assert.Equal(250, b.CalculationIntervalMs); Assert.Equal("kW", b.Unit);
        Assert.Equal(3, await db.TagDependencies.CountAsync()); Assert.Equal(2, await db.OperationLogs.CountAsync()); Assert.Equal(42, (await db.OperationLogs.FirstAsync()).UserId);
        using var again = new MemoryStream(service.Export([device], await db.Tags.AsNoTracking().ToListAsync()));
        var update = await service.ImportAsync(again, factory, default); Assert.Empty(update.Errors); Assert.Equal(4, update.Updated);
        db.ChangeTracker.Clear(); Assert.Equal(2, (await db.Tags.FindAsync(a.Id))!.CalculationRevision); Assert.Equal(3, await db.TagDependencies.CountAsync());
    }

    [Theory]
    [InlineData("missing")][InlineData("cycle")][InlineData("invalid-enable")][InlineData("fraction-id")][InlineData("duplicate-id")][InlineData("physical-error")]
    public async Task InvalidCalculationWorkbookRollsBackPhysicalAndCalculatedChangesTogether(string fault)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
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
        var result = await service.ImportAsync(stream, factory, default); Assert.NotEmpty(result.Errors); Assert.Equal(0, result.Created); Assert.Equal(0, result.Updated);
        db.ChangeTracker.Clear(); Assert.Equal(12, (await db.Tags.SingleAsync()).Address); Assert.Empty(await db.TagDependencies.ToListAsync()); Assert.Empty(await db.OperationLogs.ToListAsync());
    }

    [Fact]
    public async Task S7ExcelRoundTripPreservesDbAndBitOffsets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        var device = new Device { Name = "S7", Protocol = ProtocolType.SiemensS7, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var tag = new Tag { DeviceId = device.Id, Name = "bit", Area = TagArea.DataBlock, Address = 100, DbNumber = 17, BitOffset = 6, DataType = TagDataType.Bool, Writable = true };
        db.Tags.Add(tag); await db.SaveChangesAsync();
        var service = new TagExcelService(); var bytes = service.Export([device], [tag]);
        using var stream = new MemoryStream(bytes); var result = await service.ImportAsync(stream, factory, default);
        Assert.Equal(1, result.Updated); Assert.Empty(result.Errors);
        db.ChangeTracker.Clear(); var read = await db.Tags.SingleAsync(); Assert.Equal(17, read.DbNumber); Assert.Equal(6, read.BitOffset); Assert.Equal(100, read.Address);
    }

    [Fact]
    public async Task InvalidImportedUpdateCannotMutateExistingTrackedTag()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        var device = new Device { Name = "S7", Protocol = ProtocolType.SiemensS7, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var tag = new Tag { DeviceId = device.Id, Name = "bit", Area = TagArea.DataBlock, Address = 100, DbNumber = 17, BitOffset = 6, DataType = TagDataType.Bool };
        db.Tags.Add(tag); await db.SaveChangesAsync(); var service = new TagExcelService();
        using var source = new MemoryStream(service.Export([device], [tag])); using var book = new XLWorkbook(source);
        var sheet = book.Worksheet(1); sheet.Cell(2, 17).Value = 99; sheet.Cell(2, 3).Value = "invalid edit must not persist";
        sheet.Row(2).CopyTo(sheet.Row(3)); sheet.Cell(3, 2).Value = "new-valid"; sheet.Cell(3, 17).Value = 1;
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await service.ImportAsync(stream, factory, default);
        Assert.Equal(1, result.Created); Assert.Equal(0, result.Updated); Assert.Equal(1, result.Skipped); Assert.Single(result.Errors);
        db.ChangeTracker.Clear(); var read = await db.Tags.SingleAsync(x => x.Name == "bit"); Assert.Equal(6, read.BitOffset); Assert.Null(read.Description);
        Assert.Equal(1, (await db.Tags.SingleAsync(x => x.Name == "new-valid")).BitOffset);
    }

    [Fact]
    public async Task LegacyFifteenColumnExcelStillImportsModbusTags()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        var device = new Device { Name = "modbus", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        using var source = new MemoryStream(new TagExcelService().Export([device], [new Tag { DeviceId = device.Id, Name = "legacy", Address = 123 }]));
        using var book = new XLWorkbook(source); book.Worksheet(1).Columns(16, 18).Delete();
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await new TagExcelService().ImportAsync(stream, factory, default);
        Assert.Equal(1, result.Created); Assert.Empty(result.Errors);
        var tag = await db.Tags.SingleAsync(); Assert.Equal(123, tag.Address); Assert.Equal(1, tag.DbNumber); Assert.Equal(0, tag.BitOffset);
    }

    [Fact]
    public async Task OpcNodeIdsSurviveExcelRoundTrip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection); await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        var device = new Device { Name = "opc", Protocol = ProtocolType.OpcUa, Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync();
        var tag = new Tag { DeviceId = device.Id, Name = "Temperature", Area = TagArea.OpcNode, NodeId = "nsu=urn:plant;s=Boiler.Temperature", DataType = TagDataType.Float };
        var service = new TagExcelService(); using var stream = new MemoryStream(service.Export([device], [tag]));
        var result = await service.ImportAsync(stream, factory, default); Assert.Equal(1, result.Created); Assert.Empty(result.Errors);
        Assert.Equal(tag.NodeId, (await db.Tags.SingleAsync()).NodeId);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
}
