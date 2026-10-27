using System.Text.Json;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Realtime;
using Scada.Engine.Traceability;

namespace Scada.Engine.Tests;

public sealed class TraceabilityTests
{
    [Theory]
    [InlineData(TraceabilityScanTerminator.CrLf, "\r\n")]
    [InlineData(TraceabilityScanTerminator.Lf, "\n")]
    [InlineData(TraceabilityScanTerminator.Cr, "\r")]
    public async Task ScannerFramesHandleSplitAndCombinedPackets(TraceabilityScanTerminator terminator, string suffix)
    {
        await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes($"SN001{suffix}{suffix}SN002{suffix}partial"));
        var received = new List<string>();
        await TraceabilityScannerConnection.ReadFramesAsync(stream, new() { Terminator = terminator }, sn => { received.Add(sn); return Task.CompletedTask; }, default);
        Assert.Equal(["SN001", "SN002"], received);
    }

    [Fact]
    public async Task ScannerRejectsUnboundedFrames()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('A', 1000)));
        await Assert.ThrowsAsync<IOException>(() => TraceabilityScannerConnection.ReadFramesAsync(stream, new(), _ => Task.CompletedTask, default));
    }

    [Theory]
    [InlineData(TraceabilityTcpMode.Client)]
    [InlineData(TraceabilityTcpMode.Server)]
    public async Task TcpScannerReceivesSnInBothConnectionModes(TraceabilityTcpMode mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (mode == TraceabilityTcpMode.Server) listener.Stop();
        var received = new List<string>();
        var reader = TraceabilityScannerConnection.ReceiveTcpAsync(new() { TcpMode = mode, Port = port }, _ => { },
            sn => { received.Add(sn); return Task.CompletedTask; }, timeout.Token);
        using var sender = mode == TraceabilityTcpMode.Client ? await listener.AcceptTcpClientAsync(timeout.Token) : new TcpClient();
        if (mode == TraceabilityTcpMode.Server) await sender.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await sender.GetStream().WriteAsync(Encoding.UTF8.GetBytes("TCP-SN-001\r\n"), timeout.Token);
        sender.Client.Shutdown(SocketShutdown.Send);
        await reader;
        Assert.Equal(["TCP-SN-001"], received);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }

    [Fact]
    public async Task ScannerHostBindsCapturesAndReleasesDisabledConnection()
    {
        await using var fixture = await Fixture.CreateAsync(TraceabilitySnSource.Tcp);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = new TraceabilityScannerOptions { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var job = await db.TraceabilityJobs.SingleAsync();
            job.ScannerOptionsJson = JsonSerializer.Serialize(options, TraceabilityService.Json);
            await db.SaveChangesAsync();
        }
        using var host = new TraceabilityScannerHost(fixture.Factory, fixture.Service, new StandaloneNodeRole(), NullLogger<TraceabilityScannerHost>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await host.RefreshAsync(timeout.Token);
            using var sender = await listener.AcceptTcpClientAsync(timeout.Token);
            await sender.GetStream().WriteAsync(Encoding.UTF8.GetBytes("HOST-SN\r\n"), timeout.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (fixture.Service.Status(fixture.Job.Id).PendingSn is null) await timer.WaitForNextTickAsync(timeout.Token);
            Assert.Equal("connected", host.Status(fixture.Job.Id).State);
            await fixture.Service.ProcessOnceAsync(timeout.Token);
            await using var db = fixture.Factory.CreateDbContext();
            Assert.Equal("HOST-SN", (await db.TraceabilityRecords.SingleAsync()).Sn);
            var job = await db.TraceabilityJobs.SingleAsync();
            job.Enabled = false;
            await db.SaveChangesAsync();
            await host.RefreshAsync(timeout.Token);
            Assert.Equal("stopped", host.Status(fixture.Job.Id).State);
            Assert.Equal(0, await sender.GetStream().ReadAsync(new byte[1], timeout.Token));
        }
        finally { await host.StopAsync(default); }
    }

    [Theory]
    [InlineData(TraceabilitySnSource.Tcp)]
    [InlineData(TraceabilitySnSource.Serial)]
    public async Task RejectsInvalidScannerSettings(TraceabilitySnSource source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        fixture.Job.ScannerOptionsJson = JsonSerializer.Serialize(new TraceabilityScannerOptions { Port = 0, BaudRate = 0 }, TraceabilityService.Json);
        Assert.NotEmpty(fixture.Service.Validate(fixture.Job));
    }

    [Theory]
    [InlineData(TraceabilitySnFormat.AsciiLowByteFirst, 0x4241, 0x0031)]
    [InlineData(TraceabilitySnFormat.AsciiHighByteFirst, 0x4142, 0x3100)]
    public void ReadsAsciiSn(TraceabilitySnFormat format, int first, int second)
    {
        Assert.Equal("AB1", TraceabilityCapture.ReadSn([new(1, first, TagQuality.Good, 1), new(2, second, TagQuality.Good, 1)], format));
    }

    [Fact]
    public void RejectsBadSnRegister()
    {
        Assert.Throws<InvalidOperationException>(() => TraceabilityCapture.ReadSn([new(1, 123, TagQuality.Bad, 1)], TraceabilitySnFormat.Numeric));
    }

    [Fact]
    public async Task RepliesTwoOnlyAfterCommit()
    {
        var events = new List<string>();
        var result = await TraceabilityCapture.RunAsync(
            _ => Task.FromResult(new TraceabilityRecord { Sn = "SN001" }),
            (_, _) => { events.Add("commit"); return Task.CompletedTask; },
            (code, _) => { events.Add($"reply:{code}"); return Task.CompletedTask; }, default);
        Assert.Equal(["commit", "reply:2"], events);
        Assert.NotNull(result.Record);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepliesThreeWhenReadOrCommitFails(bool readFails)
    {
        var replies = new List<int>();
        var result = await TraceabilityCapture.RunAsync(
            _ => readFails ? Task.FromException<TraceabilityRecord>(new IOException("read failed")) : Task.FromResult(new TraceabilityRecord { Sn = "SN001" }),
            (_, _) => Task.FromException(new IOException("commit failed")),
            (code, _) => { replies.Add(code); return Task.CompletedTask; }, default);
        Assert.Equal([3], replies);
        Assert.Null(result.Record);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task FailedSuccessReplyDoesNotSendThree()
    {
        var replies = new List<int>();
        var result = await TraceabilityCapture.RunAsync(
            _ => Task.FromResult(new TraceabilityRecord { Sn = "SN001" }),
            (_, _) => Task.CompletedTask,
            (code, _) => { replies.Add(code); throw new IOException("disconnected"); }, default);
        Assert.Equal([2], replies);
        Assert.NotNull(result.Record);
        Assert.Null(result.Error);
        Assert.NotNull(result.ReplyError);
    }

    [Fact]
    public async Task MissingSnIsNeverStored()
    {
        var stored = false;
        var result = await TraceabilityCapture.RunAsync(
            _ => Task.FromResult(new TraceabilityRecord()),
            (_, _) => { stored = true; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, default);
        Assert.False(stored);
        Assert.Equal(3, result.Reply);
    }

    [Theory]
    [InlineData(TraceabilitySnSource.Plc)]
    [InlineData(TraceabilitySnSource.WebScan)]
    [InlineData(TraceabilitySnSource.Tcp)]
    [InlineData(TraceabilitySnSource.Serial)]
    public async Task ServiceCapturesBothSnSourcesAndConsumesScanOnce(TraceabilitySnSource source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        if (source == TraceabilitySnSource.WebScan) await fixture.Service.ScanAsync(fixture.Job.Id, "QR-SN-001", default);
        if (source is TraceabilitySnSource.Tcp or TraceabilitySnSource.Serial)
            await fixture.Service.ReceiveScannerAsync(fixture.Job.Id, "QR-SN-001", source, fixture.Job.Revision, default);
        await fixture.Service.ProcessOnceAsync(default);
        await fixture.Service.ProcessOnceAsync(default);
        await using var db = fixture.Factory.CreateDbContext();
        var record = await db.TraceabilityRecords.SingleAsync();
        Assert.Equal(source == TraceabilitySnSource.Plc ? "12345" : "QR-SN-001", record.Sn);
        Assert.Equal(2, TraceabilityService.Values(record).Count);
        Assert.True(record.Acknowledged);
        Assert.Equal([2], fixture.Collector.Replies);
        Assert.Null(fixture.Service.Status(fixture.Job.Id).PendingSn);
        if (source != TraceabilitySnSource.Plc)
        {
            fixture.Collector.Trigger(1);
            await fixture.Service.ProcessOnceAsync(default);
            Assert.Equal([2, 3], fixture.Collector.Replies);
            Assert.Equal(1, await db.TraceabilityRecords.CountAsync());
        }
    }

    [Theory]
    [InlineData(TraceabilitySnSource.Tcp)]
    [InlineData(TraceabilitySnSource.Serial)]
    public async Task ScannerRejectsWrongSourceStaleRevisionAndOverwrite(TraceabilitySnSource source)
    {
        await using var fixture = await Fixture.CreateAsync(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ScanAsync(fixture.Job.Id, "WEB", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReceiveScannerAsync(fixture.Job.Id, "STALE", source, fixture.Job.Revision + 1, default));
        var wrongSource = source == TraceabilitySnSource.Tcp ? TraceabilitySnSource.Serial : TraceabilitySnSource.Tcp;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReceiveScannerAsync(fixture.Job.Id, "WRONG", wrongSource, fixture.Job.Revision, default));
        await fixture.Service.ReceiveScannerAsync(fixture.Job.Id, "FIRST", source, fixture.Job.Revision, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReceiveScannerAsync(fixture.Job.Id, "SECOND", source, fixture.Job.Revision, default));
        Assert.Equal("FIRST", fixture.Service.Status(fixture.Job.Id).PendingSn);
    }

    [Fact]
    public async Task RestartRetriesReplyWithoutReadingOrInsertingAgain()
    {
        await using var fixture = await Fixture.CreateAsync(TraceabilitySnSource.Plc);
        fixture.Collector.FailReply = true;
        await fixture.Service.ProcessOnceAsync(default);
        Assert.Equal(1, fixture.Collector.Captures);
        fixture.Collector.FailReply = false;
        await fixture.NewService().ProcessOnceAsync(default);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(1, fixture.Collector.Captures);
        Assert.Single(await db.TraceabilityRecords.ToListAsync());
        Assert.True((await db.TraceabilityRecords.SingleAsync()).Acknowledged);
        Assert.Equal([2, 2], fixture.Collector.Replies);
    }

    [Fact]
    public async Task DatabaseCommitFailureRepliesThreeAndStoresNoProductionRecord()
    {
        await using var fixture = await Fixture.CreateAsync(TraceabilitySnSource.Plc);
        fixture.Factory.Failure.Enabled = true;
        await fixture.Service.ProcessOnceAsync(default);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal([3], fixture.Collector.Replies);
        Assert.Empty(await db.TraceabilityRecords.ToListAsync());
        Assert.Equal(3, (await db.TraceabilityAttempts.SingleAsync()).Reply);
    }

    [Fact]
    public async Task InvalidDataRepliesThree()
    {
        await using var fixture = await Fixture.CreateAsync(TraceabilitySnSource.Plc);
        fixture.Collector.BadData = true;
        await fixture.Service.ProcessOnceAsync(default);
        Assert.Equal([3], fixture.Collector.Replies);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.TraceabilityRecords.ToListAsync());
    }

    [Fact]
    public async Task ScanSourcesAreMutuallyExclusiveAndPendingSnCannotBeOverwritten()
    {
        await using var fixture = await Fixture.CreateAsync(TraceabilitySnSource.WebScan);
        await fixture.Service.ScanAsync(fixture.Job.Id, "FIRST", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ScanAsync(fixture.Job.Id, "SECOND", default));
        fixture.Job.SnTagIdsJson = "[2]";
        Assert.Contains(fixture.Service.Validate(fixture.Job), error => error.Contains("mutually exclusive"));
        fixture.Job.SnSource = TraceabilitySnSource.Plc;
        fixture.Job.SnTagIdsJson = "[1]";
        Assert.NotEmpty(fixture.Service.Validate(fixture.Job));
    }

    private sealed class CommitFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<TraceabilityRecord>().Any(entry => entry.State == EntityState.Added))
                throw new IOException("Database commit failed.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public CommitFailure Failure { get; } = new();
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).AddInterceptors(Failure).Options);
    }

    private sealed class FakeCollector(RealtimeCache cache) : ICollectorControl
    {
        public bool FailReply { get; set; }
        public bool BadData { get; set; }
        public int Captures { get; private set; }
        public List<int> Replies { get; } = [];
        private double _trigger = 1;
        public void Trigger(double value) { _trigger = value; cache.Set(new(1, value, TagQuality.Good, ScadaTime.NowMs())); }
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public void RequestReload() { }
        public Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
        {
            if (tagIds.Count > 1) Captures++;
            return Task.FromResult<IReadOnlyList<TagValue>>(tagIds.Select(id => new TagValue(id, id == 1 ? _trigger : id == 2 ? 12345 : id == 3 ? 12.3 : 220,
                BadData && id == 3 ? TagQuality.Bad : TagQuality.Good, ScadaTime.NowMs())).ToArray());
        }
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
        {
            Replies.Add((int)value);
            if (FailReply) return Task.FromResult(new CommandExecutionResult(false, "Reply failed."));
            Trigger(value);
            return Task.FromResult(new CommandExecutionResult(true, "ok", value));
        }
    }

    private sealed class Fixture(SqliteConnection connection, Factory factory, ConfigSnapshot config, RealtimeCache cache, FakeCollector collector, TraceabilityJob job) : IAsyncDisposable
    {
        public Factory Factory => factory;
        public FakeCollector Collector => collector;
        public TraceabilityJob Job => job;
        public TraceabilityService Service { get; private set; } = null!;
        public TraceabilityService NewService() => new(factory, config, cache, collector, new StandaloneNodeRole(), NullLogger<TraceabilityService>.Instance);
        public static async Task<Fixture> CreateAsync(TraceabilitySnSource source)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(connection);
            await using var db = factory.CreateDbContext();
            await db.Database.MigrateAsync();
            var device = new Device { Name = "PLC", Enabled = true, AreaId = Area.DefaultId };
            db.Devices.Add(device); await db.SaveChangesAsync();
            var tags = Enumerable.Range(1, 4).Select(id => new Tag { Id = id, Name = $"point-{id}", DeviceId = device.Id, DataType = TagDataType.UInt16, Writable = id == 1 }).ToArray();
            db.Tags.AddRange(tags); await db.SaveChangesAsync();
            var job = new TraceabilityJob { Name = "station", Enabled = true, TriggerTagId = 1, SnSource = source,
                SnTagIdsJson = source == TraceabilitySnSource.Plc ? "[2]" : "[]",
                FieldsJson = JsonSerializer.Serialize(new[] { new TraceabilityField("Current", 3), new TraceabilityField("Voltage", 4) }, TraceabilityService.Json) };
            db.TraceabilityJobs.Add(job); await db.SaveChangesAsync();
            var config = new ConfigSnapshot(factory); await config.ReloadAsync();
            var cache = new RealtimeCache();
            var collector = new FakeCollector(cache); collector.Trigger(1);
            var fixture = new Fixture(connection, factory, config, cache, collector, job);
            fixture.Service = fixture.NewService();
            return fixture;
        }
        public async ValueTask DisposeAsync() { Service.Dispose(); await connection.DisposeAsync(); }
    }
}