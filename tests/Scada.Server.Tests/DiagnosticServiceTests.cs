using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Engine.Realtime;

namespace Scada.Server.Tests;

public sealed class DiagnosticServiceTests
{
    [Fact]
    public async Task BundleCarriesManifestHealthAndDatabaseCheck()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();

        var bundle = await rig.Diagnostics.CreateAsync(default);

        Assert.StartsWith("diag_", bundle.FileName);
        using var zip = ZipFile.OpenRead(bundle.Path);
        var names = zip.Entries.Select(x => x.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("manifest.json", names);
        Assert.Contains("health.json", names);
        Assert.Contains("db-check.json", names);

        using var manifest = JsonDocument.Parse(Read(zip, "manifest.json"));
        Assert.Equal(1, manifest.RootElement.GetProperty("counts").GetProperty("devices").GetInt32());
        Assert.Equal(2, manifest.RootElement.GetProperty("counts").GetProperty("tags").GetInt32());
        var settingsNode = manifest.RootElement.GetProperty("settings");
        Assert.Equal(512, settingsNode.GetProperty("historySpoolMinFreeMb").GetInt64());
        Assert.Equal(5, settingsNode.GetProperty("diskFreeWarnGb").GetDouble());

        using var check = JsonDocument.Parse(Read(zip, "db-check.json"));
        Assert.Equal("ok", check.RootElement.GetProperty("quickCheck").GetString());
    }

    [Fact]
    public async Task BundleNeverContainsPasswordHashesOrCredentials()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();

        var bundle = await rig.Diagnostics.CreateAsync(default);
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(bundle.Path));

        Assert.DoesNotContain("secret-hash-value", raw);
        Assert.DoesNotContain("opc-private-credential", raw);
    }

    [Fact]
    public async Task HealthSnapshotReflectsCollectorAndBusState()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();
        rig.Bus.Subscribe();
        rig.Bus.Publish(new TagValue(1, 1, TagQuality.Good, ScadaTime.NowMs(), 0));

        var bundle = await rig.Diagnostics.CreateAsync(default);
        using var zip = ZipFile.OpenRead(bundle.Path);
        using var health = JsonDocument.Parse(Read(zip, "health.json"));

        Assert.Equal(1, health.RootElement.GetProperty("busBacklog").GetInt32());
        Assert.Equal(1, health.RootElement.GetProperty("totalDevices").GetInt32());
        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "scada-diag-test-" + Guid.NewGuid().ToString("N"));
        public RealtimeBus Bus { get; } = new();
        public DiagnosticService Diagnostics { get; private set; } = null!;

        public Rig() => Directory.CreateDirectory(Root);

        public async Task PrepareAsync()
        {
            var configPath = Path.Combine(Root, "config", "scada.db");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var factory = new Factory(configPath);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                var device = new Device { Name = "1号线 PLC", Protocol = ProtocolType.ModbusTcp, OpcPasswordProtected = "opc-private-credential" };
                db.Devices.Add(device);
                await db.SaveChangesAsync();
                db.Tags.AddRange(
                    new Tag { DeviceId = device.Id, Name = "温度", Address = 0 },
                    new Tag { DeviceId = device.Id, Name = "压力", Address = 1 });
                db.Users.Add(new User { UserName = "operator", PasswordHash = "secret-hash-value", Role = UserRole.Operator });
                await db.SaveChangesAsync();
            }

            var settings = new ScadaSettings
            {
                ConfigDbPath = configPath,
                HistoryRoot = Path.Combine(Root, "history"),
                BackupRoot = Path.Combine(Root, "backups"),
                DiagnosticsRoot = Path.Combine(Root, "diagnostics")
            };
            var spool = new HistorySpool(settings.HistoryRoot);
            var watch = new SystemWatch(Options.Create(settings), Bus, spool, NullLogger<SystemWatch>.Instance);
            Diagnostics = new DiagnosticService(
                Options.Create(settings), new Collector(), Bus, spool, watch, factory, NullLogger<DiagnosticService>.Instance);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }
    }

    private sealed class Factory(string path) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Collector : ICollectorControl
    {
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => new[] { new CollectorDeviceStatus(1, "1号线 PLC", true, null, 0, 0, 12, 0, 0, null) };
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RequestReload() { }
    }
}
