using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Models;
using Scada.Gateway.Hosting;

namespace Scada.Gateway.Tests;

public class GatewayAgentLifecycleTests
{
    [Fact]
    public async Task SlowOldIoCannotConfirmNewConfigOrLosePersistedRevocationDuringShutdown()
    {
        await using var rig = await Rig.CreateAsync(); await rig.Agent.StartAsync(default);
        try
        {
            await Until(async () => (await rig.Cache.LoadAsync(default)).Confirmed);
            rig.Runtime.HoldStop = true; rig.Configuration = new(new string('B', 64), [], []);
            await rig.Runtime.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False((await rig.Cache.LoadAsync(default)).Confirmed);
            await Until(() => Task.FromResult(rig.Heartbeats.TryPeek(out _) && rig.LastApplied is null));
            Assert.DoesNotContain(rig.Configuration.Revision, rig.Heartbeats.Where(x => x is not null));
            rig.Revoked = true;
            await Until(async () => (await rig.Cache.LoadAsync(default)).Blocked);
            Assert.Throws<IOException>(() => rig.Store.AcquireProcessLease());
            var stopping = rig.Agent.StopAsync(default);
            Assert.False(stopping.IsCompleted); Assert.Throws<IOException>(() => rig.Store.AcquireProcessLease());
            rig.Runtime.ReleaseStop.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(rig.Runtime.Configuration); Assert.Equal(1, rig.Runtime.Starts);
            Assert.True((await rig.Cache.LoadAsync(default)).Blocked);
            using var freeLease = rig.Store.AcquireProcessLease();
        }
        finally { rig.Runtime.ReleaseStop.TrySetResult(); await rig.Agent.StopAsync(default); }
    }
    [Fact]
    public async Task NormalStopQuiescesAcquisitionBeforeGoodbyeAndReleasesTheInstallationLease()
    {
        await using var rig = await Rig.CreateAsync(); await rig.Agent.StartAsync(default);
        try
        {
            await Until(async () => (await rig.Cache.LoadAsync(default)).Confirmed);
            await rig.Agent.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(rig.Goodbye); Assert.Null(rig.Runtime.Configuration);
            using var freeLease = rig.Store.AcquireProcessLease();
        }
        finally { await rig.Agent.StopAsync(default); }
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(25, timeout.Token);
    }
    private sealed class Rig : IAsyncDisposable
    {
        private readonly GatewayAgentStoreTests.StoreDirectory _temp = new();
        private WebApplication _server = null!;
        private GatewayAgentClient _client = null!;
        public GatewayAgentStore Store = null!;
        public GatewayConfigurationCache Cache = null!;
        public GatewayAgentService Agent = null!;
        public readonly FakeRuntime Runtime = new();
        public GatewayConfiguration Configuration = new(new string('A', 64), [], []);
        public volatile bool Revoked;
        public bool Goodbye;
        public string? LastApplied;
        public readonly ConcurrentQueue<string?> Heartbeats = new();
        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig(); rig.Store = rig._temp.Open();
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(x => x.Listen(IPAddress.Loopback, 0));
            rig._server = builder.Build(); var sessionId = Guid.NewGuid();
            rig._server.Map("/api/gateway-agent/{action}", async (HttpContext http, string action) =>
            {
                if (rig.Revoked) return Results.Unauthorized();
                var now = ScadaTime.NowMs();
                switch (action)
                {
                    case "hello": return Results.Json(new GatewaySession(1, sessionId, now, now + 30000, 0), GatewayProtocol.Json);
                    case "configuration": return Results.Json(rig.Configuration, GatewayProtocol.Json);
                    case "commands": return Results.Json(new GatewayCommandOffers(now, []), GatewayProtocol.Json);
                    case "heartbeat":
                        var heartbeat = await http.Request.ReadFromJsonAsync<GatewayHeartbeat>(GatewayProtocol.Json);
                        rig.LastApplied = heartbeat!.AppliedRevision; rig.Heartbeats.Enqueue(heartbeat.AppliedRevision);
                        return Results.Json(new GatewaySession(1, sessionId, now, now + 30000, 0), GatewayProtocol.Json);
                    case "goodbye":
                        Assert.Null(rig.Runtime.Configuration); rig.Goodbye = true;
                        return Results.Json(new GatewayAcknowledgement(rig.Store.StreamId, 0), GatewayProtocol.Json);
                    default: return Results.NotFound();
                }
            });
            await rig._server.StartAsync();
            var url = rig._server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var options = new GatewayAgentOptions { ServerUrl = url, GatewayId = 1, Token = new string('x', 43), DataRoot = rig._temp.Root,
                AllowLoopbackHttp = true, HeartbeatIntervalMs = 500, ConfigurationPollMs = 500, HttpTimeoutMs = 1000, RetryDelayMs = 100 };
            options.Validate(); rig.Cache = new(rig.Store, new EphemeralDataProtectionProvider(), options); rig._client = new(options);
            rig.Agent = new(options, rig.Store, rig.Cache, rig.Runtime, rig._client, new GatewayCommandRunner(rig.Store, rig._client, rig.Runtime, new EphemeralDataProtectionProvider()), new Lifetime(), NullLogger<GatewayAgentService>.Instance);
            return rig;
        }
        public async ValueTask DisposeAsync()
        {
            Runtime.ReleaseStop.TrySetResult(); await Agent.StopAsync(default); Agent.Dispose(); _client.Dispose();
            await _server.DisposeAsync(); _temp.Dispose();
        }
    }
    private sealed class FakeRuntime : IGatewayAcquisitionRuntime
    {
        public GatewayConfiguration? Configuration { get; private set; }
        public IReadOnlyList<CollectorDeviceStatus> Statuses => [];
        public bool HasFaultedWorker => false;
        public volatile bool HoldStop;
        public readonly TaskCompletionSource StopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
        public void RetryFaultedWorkers() { }
        public void Validate(GatewayConfiguration configuration) { }
        public Task StartAsync(GatewayConfiguration configuration, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Configuration = configuration; Starts++; return Task.CompletedTask; }
        public async Task StopAsync()
        {
            if (Configuration is not null && HoldStop) { StopEntered.TrySetResult(); await ReleaseStop.Task; }
            Configuration = null;
        }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default; public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default; public void StopApplication() { }
    }
}
