using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private sealed class CountingRuntime(GatewayConfiguration configuration) : IGatewayAcquisitionRuntime
    {
        public int Writes;
        public GatewayConfiguration? Configuration => configuration; public IReadOnlyList<CollectorDeviceStatus> Statuses => [];
        public bool HasFaultedWorker => false; public void RetryFaultedWorkers() { } public void Validate(GatewayConfiguration value) { }
        public Task StartAsync(GatewayConfiguration value, CancellationToken ct) => Task.CompletedTask; public Task StopAsync() => Task.CompletedTask;
        public Task<CommandExecutionResult> WriteAsync(GatewayCommand command, CancellationToken ct) { Writes++; return Task.FromResult(new CommandExecutionResult(true, "success", 7)); }
    }
}
