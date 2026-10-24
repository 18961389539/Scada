using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Gateway.Hosting;

namespace Scada.Gateway.Tests;

public sealed class GatewayHostTests
{
    private const string Token = "gateway-test-token-abcdefghijklmnopqrstuvwxyz123456";
    private static GatewayAgentOptions Options(string root, int id = 1, string token = Token)
    {
        var options = new GatewayAgentOptions { ServerUrl = "https://center.example/", GatewayId = id, Token = token, DataRoot = root };
        options.Validate(); return options;
    }
    [Fact]
    public async Task OfflineConfigurationIsEncryptedBoundToInstallationAndPreservesRevocation()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        var keys = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(temp.Root, "keys")), b => b.SetApplicationName("gateway-host-test"));
        var options = Options(temp.Root); var cache = new GatewayConfigurationCache(store, keys, options);
        var saved = await cache.LoadAsync(default);
        var configuration = new GatewayConfiguration(new string('A', 64), [new(new Device { Id = 4, GatewayId = 1 }, "private-opc-password")], []);
        await cache.SaveAsync(saved with { Configuration = configuration, Confirmed = true, Blocked = true }, default);
        Assert.DoesNotContain("private-opc-password", (await store.LoadProtectedConfigurationAsync())!);
        var reopened = new GatewayConfigurationCache(temp.Open(), keys, options); var restored = await reopened.LoadAsync(default);
        Assert.Equal("private-opc-password", restored.Configuration!.Devices[0].OpcPassword); Assert.True(restored.Blocked);
        var other = new GatewayConfigurationCache(temp.Open(), keys, Options(temp.Root, id: 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => other.LoadAsync(default));
        var rotated = new GatewayConfigurationCache(temp.Open(), keys, Options(temp.Root, token: new string('x', 43)));
        Assert.NotEqual(rotated.KeyStamp, (await rotated.LoadAsync(default)).KeyStamp);
        Assert.Equal(store.StreamId, temp.Open().StreamId);
    }
    [Fact]
    public async Task AnExistingUnboundQueueCannotBeSilentlyAdoptedByANewInstallation()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        await store.AppendAsync(1, new string('A', 64), [new(1, 3, Core.Enums.TagQuality.Good, ScadaTime.NowMs())]);
        var cache = new GatewayConfigurationCache(store, new EphemeralDataProtectionProvider(), Options(temp.Root));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.LoadAsync(default)); Assert.Equal(1, (await store.StatusAsync()).PendingBatches);
    }
    [Fact]
    public void RuntimeRejectsForeignDeviceOwnershipAndDuplicatePointsBeforeCreatingDrivers()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = new GatewayAcquisitionRuntime(Options(temp.Root), temp.Open(), new EphemeralDataProtectionProvider());
        var device = new Device { Id = 1, Name = "Valid device", GatewayId = 2, Host = "localhost" };
        Assert.Throws<InvalidDataException>(() => runtime.Validate(new(new string('A', 64), [new(device, null)], [])));
        device.GatewayId = 1;
        var tag = new Tag { Id = 1, DeviceId = 1, Name = "value" };
        Assert.Throws<InvalidDataException>(() => runtime.Validate(new(new string('A', 64), [new(device, null)], [tag, tag.Clone()])));
    }
    [Fact]
    public async Task DenialStatusRemainsAuthoritativeWhenItsBodyIsUnreadable()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StreamContent(new UnreadableStream()) };
        var failure = await GatewayHttp.FailureAsync(response, default);
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode); Assert.Equal("identity_revoked", failure.Code);
        using var conflict = new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StreamContent(new UnreadableStream()) };
        Assert.Equal(HttpStatusCode.Conflict, (await GatewayHttp.FailureAsync(conflict, default)).StatusCode);
        using var array = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("[]") };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GatewayHttp.FailureAsync(array, default)).StatusCode);
    }
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("broken body");
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
