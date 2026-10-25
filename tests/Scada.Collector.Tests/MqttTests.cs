using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MQTTnet;
using MQTTnet.Server;
using Scada.Collector.Mqtt;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

internal sealed class PlainCredentials : IDeviceCredentials
{
    public string Protect(string password) => "p:" + password;
    public string Unprotect(string protectedPassword) => protectedPassword[2..];
}

/// <summary>进程内 MQTT 代理 + 一个测试客户端，用于模拟现场设备/边缘节点。</summary>
internal sealed class BrokerFixture : IAsyncDisposable
{
    public MqttServer Server { get; }
    public int Port { get; }
    public IMqttClient Peer { get; }
    public ConcurrentQueue<(string Topic, byte[] Payload)> PeerInbox { get; } = new();
    public event Func<string, byte[], Task>? PeerMessage;
    public string? RequiredPassword { get; init; }

    private BrokerFixture(MqttServer server, int port, IMqttClient peer) { Server = server; Port = port; Peer = peer; }

    public static async Task<BrokerFixture> StartAsync(string? user = null, string? password = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var server = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());
        if (user is not null)
            server.ValidatingConnectionAsync += e =>
            {
                if (e.ClientId != "peer" && (e.UserName != user || e.Password != password)) e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
                return Task.CompletedTask;
            };
        await server.StartAsync();
        var peer = new MqttClientFactory().CreateMqttClient();
        var fixture = new BrokerFixture(server, port, peer);
        peer.ApplicationMessageReceivedAsync += async e =>
        {
            var payload = e.ApplicationMessage.Payload.ToArray();
            fixture.PeerInbox.Enqueue((e.ApplicationMessage.Topic, payload));
            if (fixture.PeerMessage is { } handler) await handler(e.ApplicationMessage.Topic, payload);
        };
        await peer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithClientId("peer").Build());
        return fixture;
    }

    public Task SubscribeAsync(string filter) => Peer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(filter)).Build());

    public Task PublishAsync(string topic, byte[] payload, bool retain = false) =>
        Peer.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build());

    public Task PublishAsync(string topic, string payload, bool retain = false) => PublishAsync(topic, Encoding.UTF8.GetBytes(payload), retain);

    public async ValueTask DisposeAsync()
    {
        try { await Peer.DisconnectAsync(); } catch { }
        Peer.Dispose();
        await Server.StopAsync(); Server.Dispose();
    }
}

public class MqttTests
{
    private static Tag T(int id, string address, TagDataType type = TagDataType.Double, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = address, DataType = type, Writable = writable };

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> ok, int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs; T last;
        do { last = await read(); if (ok(last)) return last; await Task.Delay(50); } while (Environment.TickCount64 < until);
        return last;
    }

    [Theory]
    [InlineData("plant/t1", true)]
    [InlineData("plant/t1|$.value;w=plant/t1/set;wpath=$.cmd.value;ts=$.ts", true)]
    [InlineData("plant/+/t1", false)]
    [InlineData("plant/#", false)]
    [InlineData("$SYS/x", false)]
    [InlineData("a|value", false)]
    [InlineData("a;foo=1", false)]
    [InlineData("a;wpath=$.v", false)]
    public void AddressSyntax(string text, bool valid) => Assert.Equal(valid, MqttAddress.TryParse(text, out _, out _));

    [Fact]
    public void ValidationCoversOptionsCredentialsAndWritableTopics()
    {
        var device = new Device { Name = "m", Protocol = ProtocolType.Mqtt, Host = "broker", Port = 1883 };
        Assert.Null(ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"qos\":3}"; Assert.Contains("QoS", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"unknown\":1}"; Assert.Contains("JSON", ConfigurationValidator.Device(device));
        device.ProtocolOptions = ""; device.OpcUserName = "u"; device.OpcPasswordProtected = "x"; Assert.Contains("TLS", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"allowPlaintextPassword\":true}"; Assert.Null(ConfigurationValidator.Device(device));
        Assert.Contains("写入主题", ConfigurationValidator.Tag(T(1, "a/b", writable: true), device));
        Assert.Null(ConfigurationValidator.Tag(T(1, "a/b;w=a/b/set", writable: true), device));
        var modbusTag = new Tag { Id = 1, DeviceId = 1, Name = "x", Area = TagArea.HoldingRegister };
        Assert.Contains("不匹配", ConfigurationValidator.Tag(modbusTag, device));
        Assert.Contains("不匹配", ConfigurationValidator.Tag(T(1, "a"), new Device { Name = "mb", Protocol = ProtocolType.ModbusTcp }));
    }

    [Fact]
    public void PayloadDecodingAndEncoding()
    {
        Assert.True(MqttPayload.TryValue("12.5"u8.ToArray(), null, out var v, out _)); Assert.Equal(12.5, v);
        Assert.True(MqttPayload.TryValue("true"u8.ToArray(), null, out v, out _)); Assert.Equal(1, v);
        Assert.True(MqttPayload.TryValue("{\"a\":{\"b\":[1,\"7\"]}}"u8.ToArray(), "$.a.b[1]", out v, out _)); Assert.Equal(7, v);
        Assert.False(MqttPayload.TryValue("{\"a\":1}"u8.ToArray(), "$.b", out _, out var error)); Assert.Contains("不存在", error);
        Assert.False(MqttPayload.TryValue("abc"u8.ToArray(), null, out _, out _));
        Assert.Equal(1_700_000_000_000, MqttPayload.Timestamp("{\"ts\":1700000000}"u8.ToArray(), "$.ts"));
        Assert.Equal(1_700_000_000_123, MqttPayload.Timestamp("{\"ts\":1700000000123}"u8.ToArray(), "$.ts"));
        Assert.Equal("{\"cmd\":{\"v\":true}}", Encoding.UTF8.GetString(MqttPayload.Encode(1, TagDataType.Bool, "$.cmd.v")));
        Assert.Equal("42", Encoding.UTF8.GetString(MqttPayload.Encode(42, TagDataType.Int32, null)));
    }

    [Fact]
    public async Task ReadsRetainedJsonWritesWithConfirmationAndReportsUnknownWithoutEcho()
    {
        await using var broker = await BrokerFixture.StartAsync();
        await broker.PublishAsync("plant/boiler", "{\"temp\":81.5,\"ts\":1700000000000,\"run\":true}", retain: true);
        await broker.SubscribeAsync("plant/boiler/set");
        // 模拟设备：收到设定后回报状态主题。
        broker.PeerMessage += async (topic, payload) =>
        {
            if (topic != "plant/boiler/set") return;
            var sp = Encoding.UTF8.GetString(payload);
            await broker.PublishAsync("plant/boiler/sp", sp, retain: true);
        };
        var device = new Device { Id = 1, Name = "m", Protocol = ProtocolType.Mqtt, Host = "127.0.0.1", Port = broker.Port, TimeoutMs = 1500 };
        var temp = T(1, "plant/boiler|$.temp;ts=$.ts");
        var run = T(2, "plant/boiler|$.run", TagDataType.Bool);
        var sp = T(3, "plant/boiler/sp;w=plant/boiler/set", TagDataType.Float, writable: true);
        var noEcho = T(4, "plant/other;w=plant/other/set", TagDataType.Float, writable: true);
        await using var driver = new MqttDriver(new PlainCredentials());
        await driver.ConnectAsync(device);
        var plan = new ReadPlan { DeviceId = 1, Tags = [temp, run, sp] };
        var values = await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Quality == TagQuality.Good);
        Assert.Equal(81.5, values[0].Value); Assert.Equal(1_700_000_000_000, values[0].SourceTs);
        Assert.Equal(1, values[1].Value);
        Assert.Equal(TagQuality.Bad, values[2].Quality); Assert.Contains("尚未收到", values[2].Error);
        await driver.WriteAsync(device, sp, 55.5);
        values = await driver.ReadAsync(device, plan);
        Assert.Equal(55.5, values[2].Value, 3); Assert.Equal(TagQuality.Good, values[2].Quality);
        var ex = await Assert.ThrowsAsync<IOException>(() => driver.WriteAsync(device, noEcho, 1));
        Assert.Contains("结果未知", ex.Message);
    }

    [Fact]
    public async Task AuthenticationAndReconnectAfterBrokerRestart()
    {
        await using var broker = await BrokerFixture.StartAsync("scada", "secret");
        var device = new Device { Id = 1, Name = "m", Protocol = ProtocolType.Mqtt, Host = "127.0.0.1", Port = broker.Port, TimeoutMs = 1500, OpcUserName = "scada", OpcPasswordProtected = "p:wrong" };
        await using var driver = new MqttDriver(new PlainCredentials());
        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync(device));
        Assert.False(driver.IsConnected);
        device.OpcPasswordProtected = "p:secret";
        await driver.ConnectAsync(device);
        Assert.True(driver.IsConnected);
        await broker.Server.StopAsync();
        await Eventually(() => Task.FromResult(driver.IsConnected), x => !x);
        Assert.False(driver.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, new ReadPlan { Tags = [T(1, "a")] }));
        await broker.Server.StartAsync();
        await driver.ConnectAsync(device); // 采集引擎在 IsConnected=false 后按退避重新调用 ConnectAsync
        Assert.True(driver.IsConnected);
    }

    [Fact]
    public async Task StaleValuesBecomeUncertain()
    {
        await using var broker = await BrokerFixture.StartAsync();
        await broker.PublishAsync("s/v", "1", retain: true);
        var device = new Device { Id = 1, Name = "m", Protocol = ProtocolType.Mqtt, Host = "127.0.0.1", Port = broker.Port, TimeoutMs = 1500, ProtocolOptions = "{\"staleAfterMs\":200}" };
        await using var driver = new MqttDriver(new PlainCredentials());
        await driver.ConnectAsync(device);
        var plan = new ReadPlan { Tags = [T(1, "s/v")] };
        Assert.Equal(TagQuality.Good, (await Eventually(() => driver.ReadAsync(device, plan), x => x[0].Quality == TagQuality.Good))[0].Quality);
        await Task.Delay(350);
        Assert.Equal(TagQuality.Uncertain, (await driver.ReadAsync(device, plan))[0].Quality);
    }
}
