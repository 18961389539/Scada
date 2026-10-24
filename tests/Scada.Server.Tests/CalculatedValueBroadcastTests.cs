using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Hubs;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public class CalculatedValueBroadcastTests
{
    [Fact]
    public async Task QueuedOldDefinitionsAndDeletedTagsAreNotPushedAndDisabledValuesCannotRemainGood()
    {
        var bus = new Bus(); var client = new Client(); var subscriptions = new RealtimeSubscriptionStore(); subscriptions.Subscribe("browser", [1, 2, 3, 4]);
        var sessions = new SessionRegistry(); sessions.Register("browser", 1, "Admin", "v", () => { });
        var access = new AccessModelProvider(_ => Task.FromResult(AccessModel.Build([], [], [], [(1, UserRole.Admin, true)], [], ScadaTime.NowMs())));
        using var service = new SignalRRealtimeBroadcaster(bus, new Hub(client), subscriptions, Options.Create(new ScadaSettings { PushIntervalMs = 100 }), NullLogger<SignalRRealtimeBroadcaster>.Instance, new Snapshot(), access, sessions);
        await service.StartAsync(default); await bus.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bus.Publish(new(1, 10, TagQuality.Good, 2000, DefinitionRevision: 1));
        bus.Publish(new(1, 20, TagQuality.Good, 1000, DefinitionRevision: 2));
        bus.Publish(new(1, 30, TagQuality.Good, 2001, DefinitionRevision: 2));
        bus.Publish(new(2, 40, TagQuality.Good, 2001, DefinitionRevision: 3));
        bus.Publish(new(3, 50, TagQuality.Good, 2001)); bus.Publish(new(4, 60, TagQuality.Good, 2001));
        var values = await client.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2, 3 }, values.Select(x => x.TagId)); Assert.Equal(30, values[0].Value);
        Assert.Equal(TagQuality.Uncertain, values[1].Quality); Assert.Equal(TagQuality.Good, values[2].Quality);
        await service.StopAsync(default);
    }
    private sealed class Bus : IRealtimeBus
    {
        private readonly Channel<TagValue> _channel = Channel.CreateUnbounded<TagValue>();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Backlog => 0; public long DroppedTotal => 0;
        public void Publish(TagValue value) => _channel.Writer.TryWrite(value);
        public ChannelReader<TagValue> Subscribe() { Ready.TrySetResult(); return _channel.Reader; }
    }
    private sealed class Client : IClientProxy
    {
        public TaskCompletionSource<IReadOnlyList<TagValue>> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        { if (method == "values") Received.TrySetResult((IReadOnlyList<TagValue>)args[0]!); return Task.CompletedTask; }
    }
    private sealed class Hub(Client client) : IHubContext<RealtimeHub>
    { public IHubClients Clients { get; } = new HubClients(client); public IGroupManager Groups => throw new NotSupportedException(); }
    private sealed class HubClients(Client client) : IHubClients
    {
        public IClientProxy All => client;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => client;
        public IClientProxy Client(string connectionId) => client;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => client;
        public IClientProxy Group(string groupName) => client;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => client;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => client;
        public IClientProxy User(string userId) => client;
        public IClientProxy Users(IReadOnlyList<string> userIds) => client;
    }
    private sealed class Snapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<Tag> Tags => [new() { Id = 1, Kind = TagKind.Calculated, CalculationRevision = 2 }, new() { Id = 2, Kind = TagKind.Calculated, CalculationRevision = 3, CalculationEnabled = false }, new() { Id = 3, DeviceId = 1 }];
        public IReadOnlyList<AlarmRule> AlarmRules => []; public int Version => 1; public long LoadedAt => 1000;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) { tag = Tags.FirstOrDefault(x => x.Id == id)!; return tag != null; }
        public bool TryGetDevice(int id, out Device device) { device = null!; return false; }
    }
}
