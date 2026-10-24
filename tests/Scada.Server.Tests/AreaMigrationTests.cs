using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Scada.Core.Entities;
using Scada.Core.Security;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class AreaMigrationTests
{
    [Fact]
    public async Task UpgradingAnOldDatabaseKeepsDevicesTagsAndPutsThemInTheDefaultArea()
    {
        var path = Path.Combine(Path.GetTempPath(), "scada-migrate-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var old = new ConfigDbContext(options))
            {
                // 迁移到本次之前的最后一个版本，模拟旧库。
                await old.GetService<IMigrator>().MigrateAsync("20261024111537_AddCalculatedTags");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO Device (Name, Protocol, Host, Port, StationNumber, ByteOrder, PollIntervalMs, TimeoutMs, Enabled, DisabledSinceTs, Parity, Rack, OpcSecurityPolicy) VALUES ('旧锅炉', 0, '10.0.0.5', 502, 1, 0, 1000, 3000, 1, 0, 0, 0, 0)");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO Tag (DeviceId, Kind, Name, Area, Address, DbNumber, BitOffset, DataType, Scale, Offset, Writable, ArchiveMode, Deadband, ArchivePeriodSec) VALUES (1, 0, '旧温度', 3, 0, 1, 0, 6, 1, 0, 0, 1, 0, 600)");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO User (UserName, PasswordHash, DisplayName, Role, Enabled) VALUES ('op', 'x', 'op', 1, 1)");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO OperationLog (UserName, Ts, Action, Result) VALUES ('op', 1, '旧日志', '成功')");
            }

            await using (var db = new ConfigDbContext(options))
            {
                await db.Database.MigrateAsync();
                var area = await db.Areas.SingleAsync();
                Assert.Equal(Area.DefaultId, area.Id);
                var device = await db.Devices.SingleAsync();
                Assert.Equal("旧锅炉", device.Name);
                Assert.Equal(Area.DefaultId, device.AreaId);
                var tag = await db.Tags.SingleAsync();
                Assert.Equal("旧温度", tag.Name);
                Assert.Null(tag.AreaId);
                Assert.Equal("旧日志", (await db.OperationLogs.SingleAsync()).Action);

                // 旧的内置角色语义保持：操作员仍可在默认区域控制。
                var model = await AccessModelProvider.BuildAsync(db, default);
                var op = model.For((await db.Users.SingleAsync()).Id);
                Assert.True(op.CanTag(Permission.Control, tag.Id));
                Assert.False(op.CanTag(Permission.Configure, tag.Id));
                Assert.True(await AccessModelProvider.UserHasForDeviceAsync(db, op.UserId!.Value, Permission.Control, device.Id, default));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }
}
