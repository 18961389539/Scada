using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Gateway.Tests;

public class GatewayTelemetryUploaderTests
{
    private const string Revision = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Token = "gateway-test-token-abcdefghijklmnopqrstuvwxyz123456";

    [Fact]
    public async Task LostAcknowledgementRetriesTheExactPacketAndOnlyAValidReplyAdvancesTheQueue()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        await store.AppendAsync(1, Revision, new[] { new TagValue(1, 10, TagQuality.Good, ScadaTime.NowMs()) });
        await store.AppendAsync(1, Revision, new[] { new TagValue(1, 20, TagQuality.Good, ScadaTime.NowMs()) });
        var seen = new List<string>(); var session = Guid.NewGuid();
        await using var server = await TestServer.StartAsync(async http =>
        {
            Assert.Equal($"Gateway 12:{Token}", http.Request.Headers.Authorization); Assert.Equal(session.ToString("D"), http.Request.Headers["X-Scada-Session"]);
            var body = await new StreamReader(http.Request.Body).ReadToEndAsync(); seen.Add(body);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))), http.Request.Headers["X-Scada-Payload-Sha256"]);
            if (seen.Count == 1) { http.Abort(); return; }
            var packet = JsonSerializer.Deserialize<GatewayTelemetry>(body, GatewayProtocol.Json)!;
            await Results.Json(new GatewayAcknowledgement(packet.StreamId, packet.Sequence), GatewayProtocol.Json).ExecuteAsync(http);
        });
        using var sender = new GatewayTelemetryUploader(store, server.Url, 12, Token, session, allowLoopbackHttp: true);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => sender.UploadNextAsync()); Assert.Equal(2, (await store.StatusAsync()).PendingBatches);
        using (var afterRestart = new GatewayTelemetryUploader(temp.Open(), server.Url, 12, Token, session, allowLoopbackHttp: true))
        { Assert.True(await afterRestart.UploadNextAsync()); }
        Assert.Equal(seen[0], seen[1]); Assert.Equal(1, (await store.StatusAsync()).LastAcknowledged);
        Assert.True(await sender.UploadNextAsync()); Assert.False(await sender.UploadNextAsync());
        Assert.Equal(0, (await store.StatusAsync()).PendingBatches); Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task RejectionAndMismatchedAcknowledgementPreserveThePacket()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        await store.AppendAsync(1, Revision, new[] { new TagValue(1, 10, TagQuality.Good, ScadaTime.NowMs()) });
        var attempts = 0;
        await using var server = await TestServer.StartAsync(async http =>
        {
            if (++attempts == 1) { http.Response.StatusCode = 503; return; }
            await Results.Json(new GatewayAcknowledgement(store.StreamId, 999), GatewayProtocol.Json).ExecuteAsync(http);
        });
        using var sender = new GatewayTelemetryUploader(store, server.Url, 12, Token, Guid.NewGuid(), allowLoopbackHttp: true);
        await Assert.ThrowsAsync<GatewayHttpException>(() => sender.UploadNextAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => sender.UploadNextAsync()); Assert.Equal(1, (await store.StatusAsync()).PendingBatches);
    }

    [Fact]
    public async Task RedirectsNeverForwardGatewayCredentialsAndTimeoutsKeepThePacket()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        await store.AppendAsync(1, Revision, new[] { new TagValue(1, 10, TagQuality.Good, ScadaTime.NowMs()) });
        var redirected = 0;
        await using var destination = await TestServer.StartAsync(http => { Interlocked.Increment(ref redirected); return Task.CompletedTask; });
        await using (var redirect = await TestServer.StartAsync(http =>
        { http.Response.StatusCode = 307; http.Response.Headers.Location = new Uri(destination.Url, "api/gateway-agent/telemetry").AbsoluteUri; return Task.CompletedTask; }))
        {
            using var sender = new GatewayTelemetryUploader(store, redirect.Url, 12, Token, Guid.NewGuid(), allowLoopbackHttp: true);
            await Assert.ThrowsAsync<GatewayHttpException>(() => sender.UploadNextAsync()); Assert.Equal(0, redirected);
        }
        await using var stalled = await TestServer.StartAsync(async http =>
        { try { await Task.Delay(Timeout.Infinite, http.RequestAborted); } catch (OperationCanceledException) { } });
        using var slow = new GatewayTelemetryUploader(store, stalled.Url, 12, Token, Guid.NewGuid(), allowLoopbackHttp: true, timeoutMs: 100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.UploadNextAsync()); Assert.Equal(1, (await store.StatusAsync()).PendingBatches);
    }

    [Theory]
    [InlineData("http://example.com/")] [InlineData("http://127.0.0.1/")] [InlineData("https://user:secret@example.com/")] [InlineData("https://example.com/?token=x")]
    public void InsecureOrCredentialBearingServerAddressesAreRejected(string address)
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        Assert.Throws<ArgumentException>(() => new GatewayTelemetryUploader(store, new Uri(address), 1, Token, Guid.NewGuid()));
    }

    private sealed class TestServer(WebApplication app, Uri url) : IAsyncDisposable
    {
        public Uri Url { get; } = url;
        public static async Task<TestServer> StartAsync(Func<HttpContext, Task> handle)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build(); app.MapPost("/api/gateway-agent/telemetry", handle); await app.StartAsync();
            return new(app, new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()));
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
