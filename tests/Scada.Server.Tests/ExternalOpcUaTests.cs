using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Scada.Collector.Opc;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.Control;
using Scada.Server.External.OpcUa;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Server.Tests;

public sealed class ExternalOpcUaTests : IClassFixture<ExternalOpcUaTests.OpcSite>
{
    private readonly OpcSite _site;
    public ExternalOpcUaTests(OpcSite site) => _site = site;

    [Fact]
    public async Task Status_Reports_Running_Endpoint()
    {
        var admin = await _site.ClientAsync("admin");
        var status = await (await admin.GetAsync("/api/external/status")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("opcUa").GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("opcUa").GetProperty("running").GetBoolean());
        Assert.Contains(_site.OpcPort.ToString(), status.GetProperty("opcUa").GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task Anonymous_Read_Subscribe_And_ApiKey_Write_Through_Control()
    {
        var cache = _site.Services.GetRequiredService<IRealtimeCache>();

        var admin = await _site.ClientAsync("admin");
        var created = await (await admin.PostAsJsonAsync("/api/external/keys", new
        {
            name = "opc-" + Guid.NewGuid().ToString("N")[..8],
            scopes = new[] { "ReadTags", "Write" },
            areaIds = new[] { _site.AreaA },
            rateLimitPerMinute = 1000
        })).Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("secret").GetString()!;
        var keyId = created.GetProperty("key").GetProperty("id").GetInt32();

        var root = Path.Combine(Path.GetTempPath(), "scada-ext-opc-client-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddOpcUa().AddClient(options =>
        {
            options.ApplicationName = "ScadaExternalOpcTestClient";
            options.ApplicationUri = "urn:localhost:scada:external-opc-test";
            options.ProductUri = "urn:scada:external-opc-test";
            options.PkiRoot = root;
            options.AutoAcceptUntrustedCertificates = true;
        });
        services.AddSingleton<IDeviceCredentials, PlainCredentials>();
        services.AddSingleton<OpcUaRuntime>();
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<OpcUaRuntime>();
        var telemetry = provider.GetRequiredService<ITelemetryContext>();

        await using var anonSession = await ConnectAsync(runtime, Device(_site.Endpoint, null, null));
        var (_, _, objects) = await anonSession.BrowseAsync(null, null, ObjectIds.ObjectsFolder, 0u, BrowseDirection.Forward,
            ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Object | (uint)NodeClass.Variable, default);
        Assert.Contains(objects.ToArray()!, r => r.BrowseName!.Name == "Scada");

        var nodeId = await FindTagNodeAsync(anonSession, _site.TagA);
        Assert.False(nodeId.IsNull);
        // 紧贴读取前写缓存：建密钥、连接会话、浏览地址空间要几百毫秒，提前写会被后续写入覆盖。
        cache.Set(new TagValue(_site.TagA, 11.5, TagQuality.Good, ScadaTime.NowMs()));
        var read = await anonSession.ReadAsync(null, 0, TimestampsToReturn.Both,
            [new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value }], default);
        Assert.Equal(StatusCodes.Good, read.Results[0].StatusCode);
        Assert.Equal(11.5, Convert.ToDouble(read.Results[0].WrappedValue.AsBoxedObject()));

        var got = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = new Subscription(telemetry) { PublishingInterval = 200, PublishingEnabled = true };
        Assert.True(anonSession.AddSubscription(sub));
        await sub.CreateAsync(default);
        sub.AddItem(new MonitoredItem(sub.DefaultItem) { StartNodeId = nodeId, AttributeId = Attributes.Value, SamplingInterval = 100 });
        sub.FastDataChangeCallback = (_, notification, _) =>
        {
            foreach (var n in notification.MonitoredItems)
            {
                var boxed = n.Value.WrappedValue.AsBoxedObject();
                if (boxed is null) continue;
                var val = Convert.ToDouble(boxed);
                // 订阅建立时可能先推当前值，只接受目标变更。
                if (Math.Abs(val - 22.25) < 1e-9) got.TrySetResult(val);
            }
        };
        await sub.ApplyChangesAsync(default);
        cache.Set(new TagValue(_site.TagA, 22.25, TagQuality.Good, ScadaTime.NowMs()));
        Assert.True(ReferenceEquals(await Task.WhenAny(got.Task, Task.Delay(8000)), got.Task), "未在超时内收到订阅通知");
        Assert.Equal(22.25, await got.Task);

        await using var writeSession = await ConnectAsync(runtime, Device(_site.Endpoint, "apikey", secret));
        var write = await writeSession.WriteAsync(null, [new WriteValue
        {
            NodeId = nodeId, AttributeId = Attributes.Value, Value = new DataValue(new Variant(7.0))
        }], default);
        Assert.True(StatusCode.IsGood(write.Results[0]), write.Results[0].ToString());

        var nodeB = await FindTagNodeAsync(writeSession, _site.TagB);
        var denied = await writeSession.WriteAsync(null, [new WriteValue
        {
            NodeId = nodeB, AttributeId = Attributes.Value, Value = new DataValue(new Variant(1.0))
        }], default);
        Assert.Equal(StatusCodes.BadUserAccessDenied, denied.Results[0]);

        await admin.PostAsJsonAsync($"/api/external/keys/{keyId}/revoke", new { });
    }

    private static Device Device(string endpoint, string? user, string? password) => new()
    {
        Id = 1, Name = "ext-opc", Protocol = ProtocolType.OpcUa, OpcEndpointUrl = endpoint,
        OpcSecurityMode = OpcSecurityMode.None, OpcSecurityPolicy = OpcSecurityPolicy.None,
        OpcUserName = user ?? "", OpcPasswordProtected = password, TimeoutMs = 15000
    };

    private static async Task<ISession> ConnectAsync(OpcUaRuntime runtime, Device device)
    {
        Exception? last = null;
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!ready.IsCancellationRequested)
        {
            try { return await runtime.ConnectAsync(device, ready.Token); }
            catch (Exception ex) when (ex is ServiceResultException or SocketException or IOException)
            {
                last = ex;
                await Task.Delay(100, ready.Token);
            }
        }
        throw new TimeoutException("无法连接对外 OPC UA：" + last);
    }

    private static async Task<NodeId> FindTagNodeAsync(ISession session, int tagId)
    {
        async Task<ReferenceDescription[]> Browse(NodeId id)
        {
            var (_, _, refs) = await session.BrowseAsync(null, null, id, 0u, BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Object | (uint)NodeClass.Variable, default);
            return refs.ToArray()!;
        }

        static string NameOf(ReferenceDescription r) => r.BrowseName.Name ?? "";

        var scada = (await Browse(ObjectIds.ObjectsFolder)).First(r => NameOf(r) == "Scada");
        foreach (var area in await Browse(ExpandedNodeId.ToNodeId(scada.NodeId, session.NamespaceUris)))
        {
            foreach (var child in await Browse(ExpandedNodeId.ToNodeId(area.NodeId, session.NamespaceUris)))
            {
                if (NameOf(child).EndsWith("_" + tagId, StringComparison.Ordinal))
                    return ExpandedNodeId.ToNodeId(child.NodeId, session.NamespaceUris);
                var hit = (await Browse(ExpandedNodeId.ToNodeId(child.NodeId, session.NamespaceUris)))
                    .FirstOrDefault(r => NameOf(r).EndsWith("_" + tagId, StringComparison.Ordinal));
                if (hit is not null) return ExpandedNodeId.ToNodeId(hit.NodeId, session.NamespaceUris);
            }
        }
        throw new InvalidOperationException("未找到测点节点 " + tagId);
    }

    private sealed class PlainCredentials : IDeviceCredentials
    {
        public string Protect(string password) => password;
        public string Unprotect(string protectedPassword) => protectedPassword;
    }

    private sealed class SucceedingCollector : ICollectorControl
    {
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public void RequestReload() { }
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandExecutionResult(true, "ok", value, GatewayCommandOutcome.Confirmed));
        public Task<CommandExecutionResult> WriteIdentifiedAsync(int deviceId, Tag tag, double value, CollectorCommandContext context, CancellationToken ct = default) =>
            WriteAsync(deviceId, tag, value, ct);
    }

    public sealed class OpcSite : AuthorizationTests.Site
    {
        // 取系统分配的空闲端口，而不是在固定区段里随机：本机若已有服务占用该端口，
        // 客户端会静默连到别人的 OPC UA 服务器，读到无关数值而不是本用例写入的缓存值。
        public int OpcPort { get; } = FreePort();

        private static int FreePort()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        public string Endpoint => $"opc.tcp://127.0.0.1:{OpcPort}/Scada";

        protected override void ConfigureSettings(IWebHostBuilder b)
        {
            // 先关着，等测点建好再打开，避免地址空间绑到空配置。
            b.UseSetting("External:OpcUa:Enabled", "false");
            b.UseSetting("External:OpcUa:EndpointUrl", Endpoint);
            b.UseSetting("External:OpcUa:SecurityModes", "None");
            b.UseSetting("External:OpcUa:AllowAnonymous", "true");
            b.UseSetting("External:OpcUa:AllowUsernamePassword", "true");
            b.UseSetting("External:OpcUa:PublishingIntervalMs", "200");
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.RemoveAll<ICollectorControl>();
            services.RemoveAll<ControlCommandService>();
            services.AddSingleton<ICollectorControl>(_ => new SucceedingCollector());
            services.AddSingleton<ControlCommandService>();
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            var admin = await ClientAsync("admin");
            foreach (var id in new[] { DeviceA, DeviceB })
            {
                var raw = await (await admin.GetAsync($"/api/devices/{id}")).Content.ReadFromJsonAsync<JsonElement>();
                var body = DeviceBody(raw, raw.GetProperty("areaId").GetInt32());
                // DeviceBody keeps enabled from raw; force enable for control checks.
                await admin.PutAsJsonAsync($"/api/devices/{id}", new
                {
                    name = raw.GetProperty("name").GetString(),
                    protocol = "ModbusTcp",
                    host = "127.0.0.1",
                    port = 1502,
                    stationNumber = 1,
                    byteOrder = "ABCD",
                    pollIntervalMs = 1000,
                    timeoutMs = 500,
                    enabled = true,
                    areaId = raw.GetProperty("areaId").GetInt32()
                });
            }
            var opt = Services.GetRequiredService<IOptionsMonitor<ExternalIntegrationOptions>>().CurrentValue;
            opt.OpcUa.Enabled = true;
            opt.OpcUa.EndpointUrl = Endpoint;
            opt.OpcUa.SecurityModes = "None";
            opt.OpcUa.AllowAnonymous = true;
            opt.OpcUa.AllowUsernamePassword = true;
            opt.OpcUa.PublishingIntervalMs = 200;
            var host = Services.GetRequiredService<ExternalOpcUaHost>();
            for (var i = 0; i < 100; i++)
            {
                if (host.IsRunning) return;
                await Task.Delay(100);
            }
            throw new TimeoutException("对外 OPC UA 未启动");
        }
    }
}
