using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using HistoryValue = Scada.Core.Models.HistoryValue;
using Scada.Data;
using Scada.Data.Infrastructure;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task FullRestorePreservesCalculatedDefinitionsRevisionsAndDependencyProtection()
    {
        using var rig = new Rig(); await rig.PrepareAsync();
        var calculation = new Tag { Kind = TagKind.Calculated, Name = "calculated power", Expression = "#1*2", DataType = TagDataType.Double, CalculationRevision = 7 };
        await using (var db = new ConfigDbContext(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={rig.ConfigPath};Pooling=False").Options))
        {
            db.Tags.Add(calculation); await db.SaveChangesAsync(); db.TagDependencies.Add(new() { TagId = calculation.Id, InputTagId = 1 }); await db.SaveChangesAsync();
        }
        var summary = await rig.Backups.CreateAsync(default); var restored = Path.Combine(rig.Root, "restored-calculations");
        await rig.Backups.RestoreAsync(summary.FileName, restored, default);
        await using var read = new ConfigDbContext(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={Path.Combine(restored, "scada.db")};Pooling=False").Options);
        var tag = await read.Tags.SingleAsync(x => x.Kind == TagKind.Calculated); Assert.Equal("#1*2", tag.Expression); Assert.Equal(7, tag.CalculationRevision); Assert.Null(tag.DeviceId);
        var edge = await read.TagDependencies.SingleAsync(); Assert.Equal(calculation.Id, edge.TagId); Assert.Equal(1, edge.InputTagId);
        await Assert.ThrowsAsync<SqliteException>(() => read.Database.ExecuteSqlRawAsync("DELETE FROM Tag WHERE Id=1"));
    }

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
        Assert.Contains(names, n => n.StartsWith("history/_spool/history-spool", StringComparison.Ordinal) && n.EndsWith(".ndjson", StringComparison.Ordinal));
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
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(restored, "history", "_spool"), "history-spool*.ndjson"));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(rig.Root, "config", "keys", "key-1.xml")),
            await File.ReadAllTextAsync(Path.Combine(restored, "keys", "key-1.xml")));

        Assert.Equal(await rig.FingerprintAsync(rig.Root), await rig.FingerprintAsync(restored));
    }

    [Fact]
    public async Task RestoredBacklogIsDiscoveredAndReplayedByTheHistoryWriter()
    {
        using var rig = new Rig(); await rig.PrepareAsync();
        var summary = await rig.Backups.CreateAsync(default);
        var restored = Path.Combine(rig.Root, "restored");
        await rig.Backups.RestoreAsync(summary.FileName, restored, default);
        var history = Path.Combine(restored, "history");
        var spool = new HistorySpool(history);
        var store = new HistoryStore(new HistoryDbFactory(Options.Create(new ScadaSettings { HistoryRoot = history })));
        await spool.DrainAsync(batch => store.WriteRawAsync(batch, default), default);
        var series = await store.QueryRawAsync([1], Rig.BacklogTs, Rig.BacklogTs, default);
        Assert.Equal(42.5, Assert.Single(Assert.Single(series).Points).Value);
        Assert.Equal(0, spool.PendingBytes);
    }

    [Fact]
    public async Task LegacyBackupRestoresItsFlattenedSpoolToTheRuntimeDirectory()
    {
        using var rig = new Rig(); await rig.PrepareAsync();
        var summary = await rig.Backups.CreateAsync(default);
        var work = Path.Combine(rig.Root, "legacy-package"); ZipFile.ExtractToDirectory(summary.Path, work);
        var spoolEntry = Directory.GetFiles(Path.Combine(work, "history", "_spool"), "history-spool*.ndjson").Single();
        var spoolRel = "history/_spool/" + Path.GetFileName(spoolEntry);
        File.Move(spoolEntry, Path.Combine(work, "history", "history-spool.ndjson"));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifestPath = Path.Combine(work, "manifest.json");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath), json)!;
        manifest = manifest with { Files = manifest.Files.Select(x => x.Path == spoolRel ? x with { Path = "history/history-spool.ndjson" } : x).ToArray() };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, json));
        ZipFile.CreateFromDirectory(work, Path.Combine(rig.Root, "backups", "full_legacy.zip"));
        var restored = Path.Combine(rig.Root, "restored-legacy");
        await rig.Backups.RestoreAsync("full_legacy.zip", restored, default);
        var spool = new HistorySpool(Path.Combine(restored, "history")); var recovered = new List<HistoryValue>();
        await spool.DrainAsync(batch => { recovered.AddRange(batch); return Task.CompletedTask; }, default);
        Assert.Equal(42.5, Assert.Single(recovered).Value);
        Assert.False(File.Exists(Path.Combine(restored, "history", "history-spool.ndjson")));
    }

    [Fact]
    public async Task BackToBackBackupRequestsKeepBothCompleteArchives()
    {
        using var rig = new Rig(); await rig.PrepareAsync();
        var summaries = await Task.WhenAll(rig.Backups.CreateAsync(default), rig.Backups.CreateAsync(default));
        Assert.Equal(2, summaries.Select(x => x.FileName).Distinct().Count());
        Assert.Equal(2, rig.Backups.List().Count);
        foreach (var summary in summaries) Assert.True((await rig.Backups.VerifyAsync(summary.FileName, default)).Valid);
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

    internal sealed class Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "scada-backup-test-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Root, "config", "scada.db");
        public BackupService Backups { get; private set; } = null!;
        public static long BacklogTs => new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

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

        public ScadaSettings Settings { get; private set; } = null!;

        public async Task PrepareAsync(Action<ScadaSettings>? configure = null)
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

            var spool = new HistorySpool(history);
            await spool.AppendAsync([new HistoryValue(1, BacklogTs, 42.5, TagQuality.Good)], default);
            var keys = Path.Combine(configDir, "keys");
            Directory.CreateDirectory(keys);
            await File.WriteAllTextAsync(Path.Combine(keys, "key-1.xml"), "<key />");

            var settings = new ScadaSettings
            {
                ConfigDbPath = ConfigPath,
                HistoryRoot = history,
                BackupRoot = Path.Combine(Root, "backups")
            };
            configure?.Invoke(settings);
            Settings = settings;
            Backups = new BackupService(Options.Create(settings), NullLogger<BackupService>.Instance, spool);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }
    }
}
