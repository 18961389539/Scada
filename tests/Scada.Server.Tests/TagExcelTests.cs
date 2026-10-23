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
        using var book = new XLWorkbook(source); book.Worksheet(1).Columns(16, 17).Delete();
        using var stream = new MemoryStream(); book.SaveAs(stream); stream.Position = 0;
        var result = await new TagExcelService().ImportAsync(stream, factory, default);
        Assert.Equal(1, result.Created); Assert.Empty(result.Errors);
        var tag = await db.Tags.SingleAsync(); Assert.Equal(123, tag.Address); Assert.Equal(1, tag.DbNumber); Assert.Equal(0, tag.BitOffset);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
}
