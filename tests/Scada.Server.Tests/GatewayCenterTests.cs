using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Data.Infrastructure;
using Scada.Engine.History;
using Scada.Engine.Realtime;
using Scada.Server.Services;
using Scada.Server.Auth;
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Server.Tests;

public class GatewayCenterTests
{
    [Fact]
    public async Task GatewayAuthenticationRequiresTlsUnlessLoopbackIsExplicitlyEnabled()
    {
        await using var rig = await Rig.CreateAsync(); var identity = await rig.Service.CreateAsync("auth-edge", 1, "test", default);
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IDbContextFactory<ConfigDbContext>>(rig.Factory)
            .Configure<GatewaySettings>(_ => { }).AddAuthentication().AddScheme<AuthenticationSchemeOptions, GatewayAuthenticationHandler>(GatewayAuthenticationHandler.SchemeName, _ => { })
            .Services.BuildServiceProvider();
        async Task<bool> Authenticated(string scheme, System.Net.IPAddress? remoteAddress = null)
        {
            await using var scope = services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Request.Scheme = scheme; context.Connection.RemoteIpAddress = remoteAddress ?? System.Net.IPAddress.Loopback;
            context.Request.Headers.Authorization = $"Gateway {identity.Id}:{identity.Token}";
            return (await context.AuthenticateAsync(GatewayAuthenticationHandler.SchemeName)).Succeeded;
        }
        Assert.False(await Authenticated("http")); Assert.True(await Authenticated("https"));
        services.GetRequiredService<IOptions<GatewaySettings>>().Value.AllowLoopbackHttp = true;
        Assert.True(await Authenticated("http")); Assert.False(await Authenticated("http", System.Net.IPAddress.Parse("198.51.100.1")));
    }

    [Fact]
    public async Task FailedStreamRetainsItsPacketWhileAnotherGatewayContinuesProcessing()
    {
        await using var rig = await Rig.CreateAsync(); await rig.AcceptAsync(rig.Packet(1, 12));
        var identity = await rig.Service.CreateAsync("second-edge", 1, "test", default); var caller = new GatewayCaller(identity.Id, GatewayCenterService.TokenHash(identity.Token));
        int deviceId, tagId;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var device = new Device { Name = "second-remote", GatewayId = identity.Id }; db.Devices.Add(device); await db.SaveChangesAsync(); deviceId = device.Id;
            var tag = new Tag { DeviceId = deviceId, Name = "second-value", ArchiveMode = ArchiveMode.OnChange }; db.Tags.Add(tag); await db.SaveChangesAsync(); tagId = tag.Id;
        }
        await rig.Config.ReloadAsync(); var stream = Guid.NewGuid(); var session = await rig.Service.HelloAsync(caller, new(1, Guid.NewGuid(), stream, "test"), default);
        var configuration = await rig.Service.ConfigurationAsync(caller, session.SessionId, default);
        await rig.Service.HeartbeatAsync(caller, session.SessionId, new(configuration.Revision, 0, 0, null, null), default);
        var packet = new GatewayTelemetry(1, stream, 1, deviceId, configuration.Revision, rig.Clock.Now, [new(tagId, 33, TagQuality.Good, rig.Clock.Now)]);
        var payload = JsonSerializer.Serialize(packet, GatewayProtocol.Json);
        await rig.Service.AcceptAsync(caller, session.SessionId, payload, GatewayCenterService.TokenHash(payload), default);
        var service = rig.MakeService(new FailOneTag(rig.History, rig.TagId));
        await Assert.ThrowsAsync<IOException>(() => service.ProcessOneAsync(default));
        Assert.True(await service.ProcessOneAsync(default)); Assert.Equal(33, rig.Cache.Get(tagId)!.Value);
        await using var verify = rig.Factory.CreateDbContext(); var pending = await verify.GatewayInbox.SingleAsync();
        Assert.Equal(rig.Caller.Id, pending.GatewayId); Assert.NotNull(pending.ProcessingError); Assert.True(pending.NextAttemptTs > rig.Clock.Now);
    }
    [Fact]
    public async Task LeaseExpiryAndKeyRotationFencePreviouslyAuthorizedCallers()
    {
        await using var rig = await Rig.CreateAsync(); var original = rig.Session;
        rig.Clock.Now += 31_000;
        var expired = await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.HeartbeatAsync(rig.Caller, original.SessionId, new(rig.Revision, 0, 0, null, null), default));
        Assert.Equal(409, expired.StatusCode);
        var fresh = await rig.Service.HelloAsync(rig.Caller, new(1, rig.Instance, rig.Stream, "test"), default); Assert.NotEqual(original.SessionId, fresh.SessionId);
        await rig.Service.ChangeAsync(rig.Caller.Id, null, null, true, false, 1, "test", default);
        var revoked = await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.ConfigurationAsync(rig.Caller, fresh.SessionId, default)); Assert.Equal(401, revoked.StatusCode);
    }

    [Fact]
    public async Task AcceptedPacketSurvivesServiceRecreationBeforeConsumerRuns()
    {
        await using var rig = await Rig.CreateAsync(); var packet = rig.Packet(1, 12.5);
        var receipt = await rig.AcceptAsync(packet); Assert.Equal(1, receipt.Sequence); Assert.Null(rig.Cache.Get(rig.TagId));
        await using (var db = rig.Factory.CreateDbContext()) { Assert.Equal(1, await db.GatewayInbox.CountAsync()); Assert.Equal(1, (await db.GatewayStreams.SingleAsync()).ReceivedThrough); }
        var restored = rig.MakeService(rig.History); Assert.True(await restored.ProcessOneAsync(default));
        Assert.Equal(12.5, rig.Cache.Get(rig.TagId)!.Value);
        Assert.Single(await rig.ReadHistoryAsync());
        await rig.AcceptAsync(packet); Assert.False(await restored.ProcessOneAsync(default)); Assert.Single(await rig.ReadHistoryAsync());
    }

    [Fact]
    public async Task FailureAfterHistoryCommitKeepsInboxAndRetryDoesNotDuplicateHistory()
    {
        await using var rig = await Rig.CreateAsync(); await rig.AcceptAsync(rig.Packet(1, 22));
        var failing = rig.MakeService(new FailAfterWrite(rig.History));
        await Assert.ThrowsAsync<IOException>(() => failing.ProcessOneAsync(default));
        await using (var db = rig.Factory.CreateDbContext())
        { Assert.Equal(1, await db.GatewayInbox.CountAsync()); Assert.Equal(0, await db.GatewayArchiveCursors.CountAsync()); Assert.Equal(0, (await db.GatewayStreams.SingleAsync()).ProcessedThrough); }
        Assert.Single(await rig.ReadHistoryAsync()); Assert.Null(rig.Cache.Get(rig.TagId));
        rig.Clock.Now += 1001;
        Assert.True(await rig.Service.ProcessOneAsync(default)); Assert.Single(await rig.ReadHistoryAsync()); Assert.Equal(22, rig.Cache.Get(rig.TagId)!.Value);
    }

    [Fact]
    public async Task BackfillAndOldConfigurationArchiveWithoutPublishingCurrentAlarmInputs()
    {
        await using var rig = await Rig.CreateAsync(); var reader = rig.Bus.Subscribe();
        await rig.AcceptAsync(rig.Packet(1, 10)); Assert.True(await rig.Service.ProcessOneAsync(default)); Assert.True(reader.TryRead(out _));
        await rig.AcceptAsync(rig.Packet(2, 999, rig.Clock.Now - 3600000)); Assert.True(await rig.Service.ProcessOneAsync(default));
        Assert.False(reader.TryRead(out _)); Assert.Equal(10, rig.Cache.Get(rig.TagId)!.Value);
        await using (var db = rig.Factory.CreateDbContext()) { (await db.Tags.SingleAsync()).Scale = 2; await db.SaveChangesAsync(); }
        await rig.Config.ReloadAsync(); rig.Clock.Now += 10;
        await rig.AcceptAsync(rig.Packet(3, 88)); Assert.True(await rig.Service.ProcessOneAsync(default));
        Assert.False(reader.TryRead(out _)); Assert.Equal(10, rig.Cache.Get(rig.TagId)!.Value); Assert.Equal(3, (await rig.ReadHistoryAsync()).Count);
        await using var verify = rig.Factory.CreateDbContext(); Assert.Single(await verify.GatewayConfigurations.ToListAsync());
    }

    [Fact]
    public async Task FullInboxRefusesAcknowledgementAndCanResumeWithoutSkippingASequence()
    {
        await using var rig = await Rig.CreateAsync(1024); long sequence = 1;
        while (true)
        {
            try { await rig.AcceptAsync(rig.Packet(sequence, sequence)); sequence++; }
            catch (GatewayRequestException ex) when (ex.StatusCode == 429) { break; }
        }
        Assert.True(sequence > 1);
        await using (var db = rig.Factory.CreateDbContext()) Assert.Equal(sequence - 1, (await db.GatewayStreams.SingleAsync()).ReceivedThrough);
        Assert.True(await rig.Service.ProcessOneAsync(default)); Assert.Equal(sequence, (await rig.AcceptAsync(rig.Packet(sequence, sequence))).Sequence);
    }

    private sealed class FailAfterWrite(IGatewayHistoryWriter history) : IGatewayHistoryWriter
    {
        public async Task WriteGatewayHistoryAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken ct)
        { await history.WriteGatewayHistoryAsync(values, ct); throw new IOException("simulated crash after history commit"); }
    }
    private sealed class FailOneTag(IGatewayHistoryWriter history, int tagId) : IGatewayHistoryWriter
    {
        public Task WriteGatewayHistoryAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken ct)
        { if (values.Any(x => x.TagId == tagId)) throw new IOException("simulated isolated history failure"); return history.WriteGatewayHistoryAsync(values, ct); }
    }
    private sealed class Clock : TimeProvider
    { public long Now = ScadaTime.NowMs(); public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now); }
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "scada-center-test-" + Guid.NewGuid().ToString("N"));
        public Factory Factory { get; private set; } = null!; public ConfigSnapshot Config { get; private set; } = null!;
        public RealtimeCache Cache { get; } = new(); public RealtimeBus Bus { get; } = new(); public Clock Clock { get; } = new();
        public HistoryStore History { get; private set; } = null!; public GatewayCenterService Service { get; private set; } = null!;
        public GatewayCaller Caller { get; private set; } = null!; public GatewaySession Session { get; private set; } = null!;
        public Guid Instance { get; } = Guid.NewGuid(); public Guid Stream { get; } = Guid.NewGuid(); public string Revision { get; private set; } = "";
        public int DeviceId, TagId; private CollectorEngine _local = null!; private IDeviceCredentials _credentials = null!; private GatewaySettings _settings = null!;
        public static async Task<Rig> CreateAsync(long maxInboxBytes = 1024 * 1024)
        {
            var rig = new Rig(); Directory.CreateDirectory(rig._root); rig.Factory = new Factory(Path.Combine(rig._root, "config.db"));
            await using (var db = rig.Factory.CreateDbContext()) await db.Database.MigrateAsync();
            rig.Config = new ConfigSnapshot(rig.Factory); await rig.Config.ReloadAsync();
            rig._settings = new GatewaySettings { MaxInboxPendingBytes = maxInboxBytes };
            rig.History = new HistoryStore(new HistoryFactory(rig._root));
            rig._credentials = new DeviceCredentials(new EphemeralDataProtectionProvider());
            rig._local = new CollectorEngine(rig.Config, new NoDriver(), new ReadPlanBuilder(), rig.Cache, rig.Bus, new Sink(), Options.Create(new ScadaSettings()), NullLogger<CollectorEngine>.Instance);
            rig.Service = rig.MakeService(rig.History);
            var identity = await rig.Service.CreateAsync("edge", 1, "test", default); rig.Caller = new(identity.Id, GatewayCenterService.TokenHash(identity.Token));
            await using (var db = rig.Factory.CreateDbContext())
            {
                var device = new Device { Name = "remote", GatewayId = identity.Id, Enabled = true }; db.Devices.Add(device); await db.SaveChangesAsync(); rig.DeviceId = device.Id;
                var tag = new Tag { DeviceId = device.Id, Name = "value", DataType = TagDataType.Float, ArchiveMode = ArchiveMode.Periodic, Deadband = .1 }; db.Tags.Add(tag); await db.SaveChangesAsync(); rig.TagId = tag.Id;
            }
            await rig.Config.ReloadAsync(); rig.Session = await rig.Service.HelloAsync(rig.Caller, new(1, rig.Instance, rig.Stream, "test"), default);
            var configuration = await rig.Service.ConfigurationAsync(rig.Caller, rig.Session.SessionId, default); rig.Revision = configuration.Revision;
            await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(rig.Revision, 0, 0, null, null), default); return rig;
        }
        public GatewayCenterService MakeService(IGatewayHistoryWriter history) => new(Factory, _credentials, Config, Cache, Bus, history, _local, Options.Create(_settings), NullLogger<GatewayCenterService>.Instance, Clock);
        public GatewayTelemetry Packet(long sequence, double value, long? timestamp = null) => new(1, Stream, sequence, DeviceId, Revision, Clock.Now, [new(TagId, value, TagQuality.Good, timestamp ?? Clock.Now)]);
        public Task<GatewayAcknowledgement> AcceptAsync(GatewayTelemetry packet)
        { var payload = JsonSerializer.Serialize(packet, GatewayProtocol.Json); return Service.AcceptAsync(Caller, Session.SessionId, payload, GatewayCenterService.TokenHash(payload), default); }
        public async Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync() => (await History.QueryRawAsync([TagId], Clock.Now - 7200000, Clock.Now + 10000, default)).SelectMany(x => x.Points).ToList();
        public ValueTask DisposeAsync()
        {
            Service.Dispose(); _local.Dispose(); Bus.Dispose();
            var path = Path.GetFullPath(_root); var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("scada-center-test-")) Directory.Delete(path, true);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Factory(string path) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class HistoryFactory(string root) : IHistoryDbFactory
    {
        public string GetRawPath(int year, int month) => Path.Combine(root, $"raw_{year}{month:00}.db");
        public string GetAggregatePath(int year) => Path.Combine(root, $"agg_{year}.db");
        public HistoryDbContext CreateRaw(int year, int month) => Create(GetRawPath(year, month));
        public HistoryDbContext CreateAggregate(int year) => Create(GetAggregatePath(year));
        private HistoryDbContext Create(string path) { var db = new HistoryDbContext(new DbContextOptionsBuilder<HistoryDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options); EnsureCreated(db); return db; }
        public void EnsureCreated(HistoryDbContext db) => db.Database.Migrate();
    }
    private sealed class NoDriver : IDeviceDriverFactory { public IDeviceDriver Create(Device device) => throw new InvalidOperationException("remote devices must never run locally"); }
    private sealed class Sink : IAlarmSink
    {
        public Task<int> RaiseDeviceOfflineAsync(Device device, string message, long ts, CancellationToken ct = default) => Task.FromResult(1);
        public Task RecoverDeviceOfflineAsync(int id, long ts, CancellationToken ct = default) => Task.CompletedTask;
    }
}
