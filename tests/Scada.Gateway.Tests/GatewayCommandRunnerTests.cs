using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Gateway.Hosting;

namespace Scada.Gateway.Tests;

public class GatewayCommandRunnerTests
{
    [Theory]
    [InlineData("expired")] [InlineData("session")] [InlineData("revision")] [InlineData("identity")] [InlineData("capacity")]
    public async Task AnInvalidOnlineGrantNeverReachesTheDriver(string fault)
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = fault == "capacity" ? new GatewayAgentStore(temp.Path, maxCommands: 1) : temp.Open(); var now = ScadaTime.NowMs(); var revision = new string('A', 64);
        var command = new GatewayCommand(Guid.NewGuid(), 1, 1, 7, revision, now, now + 3000); var session = Guid.NewGuid();
        if (fault == "capacity") await store.ReserveCommandAsync(command with { Id = Guid.NewGuid() }, now);
        GatewayCommandReceipt? reported = null; var grants = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(x => x.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/api/gateway-agent/commands/{id:guid}/authorize", () => { Interlocked.Increment(ref grants); return fault == "identity" ? (IResult)Results.Unauthorized() : Results.Json(new GatewayCommandGrant(command.Id,
            fault == "session" ? Guid.NewGuid() : session, fault == "revision" ? new string('B', 64) : revision, new string('t', 43),
            fault == "expired" ? command.ExpiresTs : now, command.ExpiresTs), GatewayProtocol.Json); });
        app.MapPost("/api/gateway-agent/command-results", async (HttpContext http) => { reported = await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(reported!.CommandId), GatewayProtocol.Json); });
        await app.StartAsync();
        var options = new GatewayAgentOptions { ServerUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(), GatewayId = 1,
            Token = new string('x', 43), DataRoot = temp.Root, AllowLoopbackHttp = true }; options.Validate();
        using var client = new GatewayAgentClient(options); var runtime = new CountingRuntime(new(revision, [], []));
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());
        await runner.ExecuteAsync(command, session, now, default); await runner.ExecuteAsync(command, session, now, default);
        Assert.Equal(0, runtime.Writes);
        if (fault == "capacity") { Assert.Equal(0, grants); Assert.Equal(GatewayCommandOutcome.NotSent, reported!.Result.Outcome); Assert.Empty(await store.UnreportedResultsAsync()); }
        else Assert.Equal(GatewayCommandOutcome.NotSent, Assert.Single(await store.UnreportedResultsAsync()).Result!.Outcome);
        Assert.Equal(fault == "identity", runner.TakeIdentityRejection());
    }

    [Fact]
    public async Task AValidGrantExecutesOnceThenReportsAndAcknowledgesTheResult()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        var grants = 0; var reports = 0; GatewayCommandReceipt? reported = null;
        var (app, options) = await StartCenterAsync(
            () => { grants++; return Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json); },
            async http => { reports++; reported = await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(reported!.CommandId), GatewayProtocol.Json); },
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runtime = new CountingRuntime(new(revision, [], []));
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());

        await runner.ExecuteAsync(command, session, now, default);

        Assert.Equal(1, grants);
        Assert.Equal(1, runtime.Writes);
        var pending = Assert.Single(await store.UnreportedResultsAsync());
        Assert.Equal(GatewayCommandOutcome.Confirmed, pending.Result!.Outcome);
        Assert.False(string.IsNullOrEmpty(pending.ProtectedExecutionToken));

        await runner.ReportPendingAsync(session, default);

        Assert.Equal(1, reports);
        Assert.Equal(command.Id, reported!.CommandId);
        Assert.Empty(await store.UnreportedResultsAsync());
    }

    [Fact]
    public async Task AnAcknowledgementForAnotherCommandIsRejectedAndTheResultStaysPending()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        var (app, options) = await StartCenterAsync(
            () => Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json),
            async http => { await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(Guid.NewGuid()), GatewayProtocol.Json); },
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runner = new GatewayCommandRunner(store, client, new CountingRuntime(new(revision, [], [])), new EphemeralDataProtectionProvider());
        await runner.ExecuteAsync(command, session, now, default);

        await Assert.ThrowsAsync<InvalidDataException>(() => runner.ReportPendingAsync(session, default));

        Assert.Single(await store.UnreportedResultsAsync());
    }

    [Fact]
    public async Task ADriverReportedOutcomeIsCarriedIntoTheReceipt()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        var (app, options) = await StartCenterAsync(
            () => Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json),
            async http => { var receipt = await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(receipt!.CommandId), GatewayProtocol.Json); },
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runtime = new CountingRuntime(new(revision, [], []), (_, _) => Task.FromResult(new CommandExecutionResult(false, "设备拒绝写入", Outcome: GatewayCommandOutcome.Failed)));
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());

        await runner.ExecuteAsync(command, session, now, default);

        Assert.Equal(GatewayCommandOutcome.Failed, Assert.Single(await store.UnreportedResultsAsync()).Result!.Outcome);
    }

    [Fact]
    public async Task AFailureAfterTheGrantIsUnknownAndNeverRetried()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        var grants = 0;
        var (app, options) = await StartCenterAsync(
            () => { grants++; return Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json); },
            async http => { var receipt = await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(receipt!.CommandId), GatewayProtocol.Json); },
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runtime = new CountingRuntime(new(revision, [], []), (_, _) => Task.FromException<CommandExecutionResult>(new IOException("driver down")));
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());

        await runner.ExecuteAsync(command, session, now, default);
        await runner.ExecuteAsync(command, session, now, default);

        Assert.Equal(1, grants);
        Assert.Equal(1, runtime.Writes);
        var pending = Assert.Single(await store.UnreportedResultsAsync());
        Assert.Equal(GatewayCommandOutcome.Unknown, pending.Result!.Outcome);
        Assert.Contains("结果未知", pending.Result.Message);
    }

    [Fact]
    public async Task ACancellationAfterTheGrantIsAlsoUnknownAndDoesNotReleaseTheToken()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        var (app, options) = await StartCenterAsync(
            () => Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json),
            async http => { var receipt = await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(receipt!.CommandId), GatewayProtocol.Json); },
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runtime = new CountingRuntime(new(revision, [], []), (_, _) => Task.FromException<CommandExecutionResult>(new OperationCanceledException()));
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());

        await runner.ExecuteAsync(command, session, now, default);

        var pending = Assert.Single(await store.UnreportedResultsAsync());
        Assert.Equal(GatewayCommandOutcome.Unknown, pending.Result!.Outcome);
        Assert.Contains("命令已开始后超时或取消", pending.Result.Message);
        Assert.False(string.IsNullOrEmpty(pending.ProtectedExecutionToken));
    }

    [Fact]
    public async Task AFaultedAcquisitionWorkerStopsBeforeAnyGrantIsRequested()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        var grants = 0;
        var (app, options) = await StartCenterAsync(
            () => { grants++; return Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json); },
            async http => { var receipt = await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(receipt!.CommandId), GatewayProtocol.Json); },
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runtime = new CountingRuntime(new(revision, [], [])) { Faulted = true };
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());

        await runner.ExecuteAsync(command, session, now, default);

        Assert.Equal(0, grants);
        Assert.Equal(0, runtime.Writes);
        Assert.Equal(GatewayCommandOutcome.NotSent, Assert.Single(await store.UnreportedResultsAsync()).Result!.Outcome);
    }

    [Fact]
    public async Task DriverMessagesAreRedactedAndTruncatedBeforeTheyReachTheCenter()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        var device = new GatewayDeviceConfiguration(new Device { Id = 7, GatewayId = 1, Name = "Pump" }, "opc-secret");
        var (app, options) = await StartCenterAsync(
            () => Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json),
            async http => { var receipt = await http.Request.ReadFromJsonAsync<GatewayCommandReceipt>(GatewayProtocol.Json); return Results.Json(new GatewayCommandReceiptAcknowledgement(receipt!.CommandId), GatewayProtocol.Json); },
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runtime = new CountingRuntime(new(revision, [device], []), (_, _) =>
            Task.FromResult(new CommandExecutionResult(true, "opc-secret " + new string('x', 1200), 1)));
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());

        await runner.ExecuteAsync(command, session, now, default);

        var message = Assert.Single(await store.UnreportedResultsAsync()).Result!.Message;
        Assert.DoesNotContain("opc-secret", message);
        Assert.StartsWith("[redacted]", message);
        Assert.Equal(1000, message.Length);
    }

    [Fact]
    public async Task ASpoolFullReportFailureNeverMasksTheMissingReservation()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = new GatewayAgentStore(temp.Path, maxCommands: 1); var now = ScadaTime.NowMs();
        var revision = new string('A', 64); var session = Guid.NewGuid();
        var command = new GatewayCommand(Guid.NewGuid(), 7, 11, 42.5, revision, now, now + 60_000);
        await store.ReserveCommandAsync(command with { Id = Guid.NewGuid() }, now);
        var grants = 0;
        var (app, options) = await StartCenterAsync(
            () => { grants++; return Results.Json(new GatewayCommandGrant(command.Id, session, revision, new string('t', 43), now, command.ExpiresTs), GatewayProtocol.Json); },
            _ => Task.FromResult<IResult>(Results.StatusCode(500)),
            temp.Root);
        await using var _ = app;
        using var client = new GatewayAgentClient(options);
        var runtime = new CountingRuntime(new(revision, [], []));
        var runner = new GatewayCommandRunner(store, client, runtime, new EphemeralDataProtectionProvider());

        await runner.ExecuteAsync(command, session, now, default);

        Assert.Equal(0, grants);
        Assert.Equal(0, runtime.Writes);
        Assert.Empty(await store.UnreportedResultsAsync());
        Assert.False(runner.TakeIdentityRejection());
    }

    private static async Task<(WebApplication App, GatewayAgentOptions Options)> StartCenterAsync(
        Func<IResult> authorize, Func<HttpContext, Task<IResult>> results, string root, int gatewayId = 1)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(x => x.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/api/gateway-agent/commands/{id:guid}/authorize", () => authorize());
        app.MapPost("/api/gateway-agent/command-results", results);
        await app.StartAsync();
        var options = new GatewayAgentOptions
        {
            ServerUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(),
            GatewayId = gatewayId,
            Token = new string('x', 43),
            DataRoot = root,
            AllowLoopbackHttp = true
        };
        options.Validate();
        return (app, options);
    }

    private sealed class CountingRuntime(GatewayConfiguration configuration, Func<GatewayCommand, CancellationToken, Task<CommandExecutionResult>>? write = null) : IGatewayAcquisitionRuntime
    {
        public int Writes;
        public bool Faulted { get; set; }
        public GatewayConfiguration? Configuration => configuration; public IReadOnlyList<CollectorDeviceStatus> Statuses => [];
        public bool HasFaultedWorker => Faulted; public void RetryFaultedWorkers() { } public void Validate(GatewayConfiguration value) { }
        public Task StartAsync(GatewayConfiguration value, CancellationToken ct) => Task.CompletedTask; public Task StopAsync() => Task.CompletedTask;
        public Task<CommandExecutionResult> WriteAsync(GatewayCommand command, CancellationToken ct)
        {
            Writes++;
            return write is null ? Task.FromResult(new CommandExecutionResult(true, "success", 7)) : write(command, ct);
        }
    }
}