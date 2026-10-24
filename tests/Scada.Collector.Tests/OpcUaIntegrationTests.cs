using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Scada.Collector.Opc;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class OpcUaIntegrationTests
{
    [Fact]
    public async Task RealOpcServerSupportsNumericAndBitReadsWritesAndPerNodeBadQuality()
    {
        var root = Path.Combine(Path.GetTempPath(), "scada-opc-test-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var endpoint = $"opc.tcp://localhost:{port}/scada-test";
        string realAddress = "", runAddress = "";
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddOpcUa().AddServer(options =>
        {
            options.ApplicationName = "ScadaTestServer"; options.ApplicationUri = "urn:localhost:scada:test"; options.ProductUri = "urn:scada:test";
            options.EndpointUrls.Add(endpoint); options.PkiRoot = Path.Combine(root, "server"); options.IncludeUnsecurePolicyNone = true;
        }).AddNodeManager("urn:scada:test:variables", nodes =>
        {
            var folder = nodes.AddFolder("Scada");
            var real = nodes.AddVariable<float>("Real", folder.Node.NodeId); real.Node.Value = 12.5f;
            realAddress = NodeId.ToExpandedNodeId(real.Node.NodeId, nodes.Context.NamespaceUris).ToString();
            real.Node.AccessLevel = AccessLevels.CurrentReadOrWrite; real.Node.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            var bit = nodes.AddVariable<bool>("Run", folder.Node.NodeId); bit.Node.Value = false;
            runAddress = NodeId.ToExpandedNodeId(bit.Node.NodeId, nodes.Context.NamespaceUris).ToString();
            bit.Node.AccessLevel = AccessLevels.CurrentReadOrWrite; bit.Node.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
        });
        var server = builder.Build(); await using var serverLifetime = (IAsyncDisposable)server;
        var services = new ServiceCollection().AddLogging();
        services.AddOpcUa().AddClient(options => { options.ApplicationName = "ScadaTestClient"; options.ApplicationUri = "urn:localhost:scada:test-client"; options.ProductUri = "urn:scada:test-client"; options.PkiRoot = Path.Combine(root, "client"); options.AutoAcceptUntrustedCertificates = false; });
        services.AddSingleton<IDeviceCredentials, Credentials>(); services.AddSingleton<OpcUaRuntime>();
        await using var provider = services.BuildServiceProvider();
        try
        {
            await server.StartAsync();
            var device = new Device { Id = 1, Name = "opc-test", Protocol = ProtocolType.OpcUa, OpcEndpointUrl = endpoint,
                OpcSecurityMode = OpcSecurityMode.None, OpcSecurityPolicy = OpcSecurityPolicy.None, TimeoutMs = 15000 };
            await using var driver = new OpcUaDriver(provider.GetRequiredService<OpcUaRuntime>());
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // 服务端 StartAsync 返回时监听端口可能尚未绑定（连接被拒）或仍在初始化（BadServerHalted），在 20 秒内重试。
            while (true)
            {
                try { await driver.ConnectAsync(device, ready.Token); break; }
                catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadServerHalted || ex.InnerResult?.StatusCode == StatusCodes.BadNotConnected || ex.InnerException is System.Net.Sockets.SocketException) { await Task.Delay(100, ready.Token); }
            }
            var real = new Tag { Id = 1, DeviceId = 1, Name = "Real", Area = TagArea.OpcNode, NodeId = realAddress, DataType = TagDataType.Float, Writable = true };
            var run = new Tag { Id = 2, DeviceId = 1, Name = "Run", Area = TagArea.OpcNode, NodeId = runAddress, DataType = TagDataType.Bool, Writable = true };
            var unknown = real.Clone(); unknown.Id = 3; unknown.NodeId = "nsu=urn:scada:test:variables;s=missing";
            var plan = new ReadPlanBuilder().Build(device, [real, run, unknown]);
            var values = await driver.ReadAsync(device, plan);
            Assert.True(values[0].Quality == TagQuality.Good, values[0].Error); Assert.Equal(12.5, values[0].Value);
            Assert.Equal(TagQuality.Good, values[1].Quality); Assert.Equal(0, values[1].Value);
            Assert.Equal(TagQuality.Bad, values[2].Quality); Assert.True(driver.IsConnected);
            await driver.WriteAsync(device, real, 25.25); await driver.WriteAsync(device, run, 1);
            values = await driver.ReadAsync(device, plan); Assert.Equal(25.25, values[0].Value); Assert.Equal(1, values[1].Value);
            var runtime = provider.GetRequiredService<OpcUaRuntime>();
            await using var catalog = new OpcCatalogSession(runtime, await runtime.ConnectAsync(device, default));
            var objects = await catalog.BrowseAsync("i=85", 100, default, default);
            var folder = Assert.Single(objects.Nodes, x => x.DisplayName == "Scada");
            var page = await catalog.BrowseAsync(folder.NodeId, 1, default, default);
            Assert.Single(page.Nodes); Assert.False(page.Continuation.IsEmpty);
            var next = await catalog.BrowseAsync(folder.NodeId, 1, page.Continuation, default);
            Assert.Single(next.Nodes); Assert.NotEqual(page.Nodes[0].NodeId, next.Nodes[0].NodeId);
            Assert.All(page.Nodes.Concat(next.Nodes), x => Assert.True(x.Readable && x.Writable && x.DataType is not null, x.Error));
        }
        finally { await server.StopAsync(); }
    }
    private sealed class Credentials : IDeviceCredentials
    { public string Protect(string password) => password; public string Unprotect(string protectedPassword) => protectedPassword; }
}
