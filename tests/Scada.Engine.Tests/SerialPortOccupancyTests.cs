using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;
using Scada.Core.Scripting;
using Scada.Data;
using Scada.Data.Infrastructure;

namespace Scada.Engine.Tests;

public sealed class SerialPortOccupancyTests
{
    [Fact]
    public void ConflictReportsScannerAndIgnoresSameDevice()
    {
        var owners = SerialPortOccupancy.FromScanners([(1, "扫码", "COM3")])
            .Concat(SerialPortOccupancy.FromExclusiveDevices([
                new Device { Id = 7, Name = "PLC", Protocol = ProtocolType.ModbusRtu, SerialPort = "COM3", Enabled = true }
            ]))
            .ToList();
        var hit = SerialPortOccupancy.Conflict(owners, "COM3", ignore: owner => owner.Kind == SerialPortOccupancy.Device);
        Assert.Contains("追溯扫码", hit);
        Assert.Null(SerialPortOccupancy.Conflict(owners, "COM4"));
    }

    [Fact]
    public async Task LoadAsyncIncludesConfiguredSerialProfiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ConfigDbContext(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var profiles = new StaticProfiles(new Dictionary<string, SerialProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["script"] = new SerialProfile { Port = "COM9" }
        });
        var owners = await SerialPortOccupancyQuery.LoadAsync(db, profiles, default);
        Assert.Contains("配置文件", SerialPortOccupancy.Conflict(owners, "COM9") ?? "");
    }

    private sealed class StaticProfiles(IReadOnlyDictionary<string, SerialProfile> current) : IConfiguredSerialProfiles
    {
        public IReadOnlyDictionary<string, SerialProfile> Current { get; } = current;
    }
}
