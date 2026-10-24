using System.Text.Json;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Gateway.Hosting;

namespace Scada.Gateway.Tests;

public class GatewayEngineeringRunnerTests
{
    private static GatewayEngineeringRequest Request() { var now = ScadaTime.NowMs(); return new(Guid.NewGuid(), 1, GatewayEngineeringOperation.Diagnostics, JsonSerializer.SerializeToElement(new { }), new string('A', 64), 1, now, now + 10000); }
    [Theory]
    [InlineData("expired")] [InlineData("revision")] [InlineData("operation")] [InlineData("duration")]
    public async Task InvalidOrExpiredRequestsNeverEnterTheRuntime(string fault)
    {
        var request = Request(); var runtime = new Runtime();
        request = fault switch { "revision" => request with { ConfigurationRevision = "invalid" }, "operation" => request with { Operation = (GatewayEngineeringOperation)999 }, "duration" => request with { ExpiresTs = request.CreatedTs + 65001 }, _ => request };
        var reply = await new GatewayEngineeringRunner(new() { Token = "gateway-secret" }, runtime).ExecuteAsync(request, fault == "expired" ? request.ExpiresTs : request.CreatedTs, default);
        Assert.Equal(fault == "expired" ? 504 : 400, reply.StatusCode); Assert.Equal(0, runtime.Calls);
    }
    [Fact]
    public async Task DeadlineCancelsTheRuntimeAndReportsAnUnconfirmedOutcome()
    {
        var request = Request() with { ExpiresTs = ScadaTime.NowMs() + 200 }; var runtime = new Runtime { Run = async ct => { await Task.Delay(Timeout.Infinite, ct); return default; } };
        var reply = await new GatewayEngineeringRunner(new() { Token = "gateway-secret" }, runtime).ExecuteAsync(request, request.CreatedTs, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(504, reply.StatusCode); Assert.Equal(1, runtime.Calls); Assert.Contains("查询实际状态", reply.Error);
    }
    [Fact]
    public async Task DiagnosticsRedactCredentialsWithoutAlteringIdentifiersOrPublicCertificateBytes()
    {
        var request = Request(); const string token = "gateway-secret", password = "device-secret";
        var runtime = new Runtime { Configuration = new(request.ConfigurationRevision, [new(new Device { Id = 1 }, password)], []),
            Run = _ => Task.FromResult(JsonSerializer.SerializeToElement(new { message = token, events = new[] { new { error = password } }, connection = password,
                sha256 = new string('A', 64), nodeId = "nsu=urn:gateway-secret;s=device-secret", publicCertificate = "AQIDBA==" })) };
        var reply = await new GatewayEngineeringRunner(new() { Token = token }, runtime).ExecuteAsync(request, request.CreatedTs, default);
        Assert.Equal(200, reply.StatusCode); var data = reply.Data!.Value;
        Assert.Equal("[redacted]", data.GetProperty("message").GetString()); Assert.Equal("[redacted]", data.GetProperty("events")[0].GetProperty("error").GetString());
        Assert.Equal("[redacted]", data.GetProperty("connection").GetString()); Assert.Equal("AQIDBA==", data.GetProperty("publicCertificate").GetString());
        Assert.Equal("nsu=urn:gateway-secret;s=device-secret", data.GetProperty("nodeId").GetString()); Assert.Equal(new string('A', 64), data.GetProperty("sha256").GetString());
    }
    [Fact]
    public async Task DriverErrorsCannotLeakSecretsThroughTheErrorReply()
    {
        var request = Request(); var runtime = new Runtime { Run = _ => throw new IOException("connect gateway-secret") };
        var reply = await new GatewayEngineeringRunner(new() { Token = "gateway-secret" }, runtime).ExecuteAsync(request, request.CreatedTs, default);
        Assert.Equal(502, reply.StatusCode); Assert.DoesNotContain("gateway-secret", reply.Error!); Assert.Contains("[redacted]", reply.Error);
    }
    private sealed class Runtime : IGatewayAcquisitionRuntime
    {
        public int Calls; public Func<CancellationToken, Task<JsonElement>> Run = _ => Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
        public GatewayConfiguration? Configuration { get; set; }
        public IReadOnlyList<CollectorDeviceStatus> Statuses => []; public bool HasFaultedWorker => false;
        public void RetryFaultedWorkers() { } public void Validate(GatewayConfiguration value) { }
        public Task StartAsync(GatewayConfiguration value, CancellationToken ct) => Task.CompletedTask; public Task StopAsync() => Task.CompletedTask;
        public Task<JsonElement> EngineerAsync(GatewayEngineeringRequest request, CancellationToken ct) { Interlocked.Increment(ref Calls); return Run(ct); }
    }
}
