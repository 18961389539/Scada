using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Maintenance;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task BackupBundleCarriesConfigurationHistorySpoolKeysAndManifest()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();

        var summary = await rig.Backups.CreateAsync(default);

        Assert.StartsWith("full_", summary.FileName);
        Assert.True(summary.Bytes > 0);
        Assert.Single(rig.Backups.List());

        using var zip = ZipFile.OpenRead(summary.Path);
        var names = zip.Entries.Select(x => x.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("manifest.json", names);
        Assert.Contains("config.db", names);
        Assert.Contains("history/raw_202610.db", names);
        Assert.Contains("history/history-spool.ndjson", names);
        Assert.Contains("keys/key-1.xml", names);

        var verification = await rig.Backups.VerifyAsync(summary.FileName, default);
        Assert.True(verification.Valid, string.Join("; ", verification.Problems));
        Assert.NotNull(verification.Manifest);
        Assert.Equal(1, verification.Manifest!.Devices);
        Assert.Equal(2, verification.Manifest.Tags);
        Assert.Equal(1, verification.Manifest.HistoryFiles);
    }

    [Fact]
    public async Task VerificationRejectsTamperedHistoryContent()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();
        var summary = await rig.Backups.CreateAsync(default);

        var tampered = Path.Combine(rig.Root, "backups", "full_tampered.zip");
        var work = Path.Combine(rig.Root, "tamper");
        ZipFile.ExtractToDirectory(summary.Path, work);
        var history = Path.Combine(work, "history", "raw_202610.db");
        await File.AppendAllTextAsync(history, "not-a-database-anymore");

        using (var source = new SqliteConnection($"Data Source={history};Pooling=False"))
        {
            source.Open();
        }

        File.Delete(tampered);
        ZipFile.CreateFromDirectory(work, tampered);

        var verification = await rig.Backups.VerifyAsync("full_tampered.zip", default);
        Assert.False(verification.Valid);
        Assert.Contains(verification.Problems, x => x.Contains("raw_202610.db"));
    }

    [Fact]
    public async Task BackupFileNamesRejectPathTraversal()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();
        await rig.Backups.CreateAsync(default);

        Assert.Throws<ArgumentException>(() => rig.Backups.Resolve("../secrets.zip"));
        Assert.Throws<ArgumentException>(() => rig.Backups.Resolve("notes.zip"));
        Assert.Throws<FileNotFoundException>(() => rig.Backups.Resolve("full_missing.zip"));
    }

    [Fact]
    public async Task RestoringIntoAnEmptyDirectoryReproducesConfigurationHistoryAndKeys()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();
        var summary = await rig.Backups.CreateAsync(default);

        var restored = Path.Combine(rig.Root, "restored");
        var report = await rig.Backups.RestoreAsync(summary.FileName, restored, default);

        Assert.Equal(1, report.Devices);
        Assert.Equal(2, report.Tags);
        Assert.Equal(1, report.AlarmRules);
        Assert.True(File.Exists(Path.Combine(restored, "scada.db")));
        Assert.True(File.Exists(Path.Combine(restored, "history", "raw_202610.db")));
        Assert.True(File.Exists(Path.Combine(restored, "history", "history-spool.ndjson")));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(rig.Root, "config", "keys", "key-1.xml")),
            await File.ReadAllTextAsync(Path.Combine(restored, "keys", "key-1.xml")));

        Assert.Equal(await rig.FingerprintAsync(rig.Root), await rig.FingerprintAsync(restored));
    }

    [Fact]
    public async Task RestoringRefusesToOverwriteANonEmptyDirectory()
    {
        using var rig = new Rig();
        await rig.PrepareAsync();
        var summary = await rig.Backups.CreateAsync(default);
        var occupied = Path.Combine(rig.Root, "occupied");
        Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Combine(occupied, "keep.txt"), "现场数据");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Backups.RestoreAsync(summary.FileName, occupied, default));
        Assert.Contains("目标目录非空", failure.Message);
        Assert.True(File.Exists(Path.Combine(occupied, "keep.txt")));
    }

    private sealed class Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "scada-backup-test-" + Guid.NewGuid().ToString("N"));
        private string ConfigPath => Path.Combine(Root, "config", "scada.db");
        public BackupService Backups { get; private set; } = null!;

        // 用“设备名 + 测点名 + 规则阈值”拼接指纹，避免只比数量掩盖内容差异。
        public async Task<string> FingerprintAsync(string root)
        {
            var config = Path.Combine(root, "config", "scada.db");
            if (!File.Exists(config)) config = Path.Combine(root, "scada.db");
            var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={config};Mode=ReadOnly;Pooling=False").Options;
            await using var db = new ConfigDbContext(options);
            var devices = await db.Devices.OrderBy(x => x.Id).Select(x => x.Name).ToListAsync();
            var tags = await db.Tags.OrderBy(x => x.Id).Select(x => x.Name).ToListAsync();
            var rules = await db.AlarmRules.OrderBy(x => x.Id).Select(x => x.Type + ":" + x.Threshold).ToListAsync();
            return string.Join("|", devices) + "#" + string.Join("|", tags) + "#" + string.Join("|", rules);
        }

        public Rig() => Directory.CreateDirectory(Root);

        public async Task PrepareAsync()
        {
            var configDir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(configDir);
            var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={ConfigPath};Pooling=False").Options;
            await using (var db = new ConfigDbContext(options))
            {
                await db.Database.MigrateAsync();
                var device = new Device { Name = "1号线 PLC", Protocol = ProtocolType.ModbusTcp };
                db.Devices.Add(device);
                await db.SaveChangesAsync();
                var temperature = new Tag { DeviceId = device.Id, Name = "温度", Address = 0 };
                var pressure = new Tag { DeviceId = device.Id, Name = "压力", Address = 1 };
                db.Tags.AddRange(temperature, pressure);
                await db.SaveChangesAsync();
                db.AlarmRules.Add(new AlarmRule { TagId = temperature.Id, Type = AlarmRuleType.High, Threshold = 80, Level = AlarmLevel.Warning, Message = "高温" });
                await db.SaveChangesAsync();
            }

            var history = Path.Combine(Root, "history");
            Directory.CreateDirectory(Path.Combine(history, "_spool"));
            using (var db = new SqliteConnection($"Data Source={Path.Combine(history, "raw_202610.db")};Pooling=False"))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE Marker(Value TEXT);"; command.ExecuteNonQuery();
            }

            await File.WriteAllTextAsync(Path.Combine(history, "_spool", "history-spool.ndjson"), "[]\n");
            var keys = Path.Combine(configDir, "keys");
            Directory.CreateDirectory(keys);
            await File.WriteAllTextAsync(Path.Combine(keys, "key-1.xml"), "<key />");

            var settings = new ScadaSettings
            {
                ConfigDbPath = ConfigPath,
                HistoryRoot = history,
                BackupRoot = Path.Combine(Root, "backups")
            };
            Backups = new BackupService(Options.Create(settings), NullLogger<BackupService>.Instance);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }
    }
}
