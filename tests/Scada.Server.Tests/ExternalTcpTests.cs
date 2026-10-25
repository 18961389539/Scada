using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Server.External.Tcp;

namespace Scada.Server.Tests;

public sealed class ExternalTcpTests : IClassFixture<ExternalTcpTests.TcpSite>, IAsyncLifetime
{
    private readonly TcpSite _site;
    public ExternalTcpTests(TcpSite site) => _site = site;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Auth_Subscribe_Poll_Write_And_PartialFrame()
    {
        var admin = await _site.ClientAsync("admin");
        var created = await (await admin.PostAsJsonAsync("/api/external/keys", new
        {
            name = "tcp-" + Guid.NewGuid().ToString("N")[..8],
            scopes = new[] { "ReadTags", "Write" },
            areaIds = new[] { _site.AreaA },
            rateLimitPerMinute = 1000
        })).Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("secret").GetString()!;
        var keyId = created.GetProperty("key").GetProperty("id").GetInt32();

        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        cache.Set(new TagValue(_site.TagA, 3.14, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _site.TcpPort);
        await using var stream = client.GetStream();

        var hello = await ReadMessageAsync(stream);
        Assert.Equal("hello", hello.GetProperty("type").GetString());

        await WriteMessageAsync(stream, new { type = "auth", apiKey = secret, id = "1" });
        var auth = await ReadMessageAsync(stream);
        Assert.Equal("authOk", auth.GetProperty("type").GetString());

        await WriteMessageAsync(stream, new { type = "subscribe", tagIds = new[] { _site.TagA, _site.TagB }, id = "2" });
        var sub = await ReadMessageAsync(stream);
        Assert.Equal("subscribed", sub.GetProperty("type").GetString());
        var subIds = sub.GetProperty("tagIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.Contains(_site.TagA, subIds);
        Assert.DoesNotContain(_site.TagB, subIds);

        await WriteMessageAsync(stream, new { type = "poll", id = "3" });
        var snap = await ReadMessageAsync(stream);
        Assert.Equal("snapshot", snap.GetProperty("type").GetString());
        Assert.Contains(snap.GetProperty("data").EnumerateArray(), x => x.GetProperty("tagId").GetInt32() == _site.TagA);

        await WriteMessageAsync(stream, new { type = "write", tagId = _site.TagA, value = 1, confirmed = true, requestId = Guid.NewGuid(), id = "4" });
        var write = await ReadMessageAsync(stream);
        Assert.Equal("writeResult", write.GetProperty("type").GetString());

        // 半包：先写长度与一半 JSON，再补齐，应能解析 ping。
        var pingBody = Encoding.UTF8.GetBytes("""{"type":"ping","id":"5"}""");
        var len = new byte[] { 0, 0, 0, (byte)pingBody.Length };
        await stream.WriteAsync(len);
        await stream.WriteAsync(pingBody.AsMemory(0, 5));
        await Task.Delay(50);
        await stream.WriteAsync(pingBody.AsMemory(5));
        var pong = await ReadMessageAsync(stream);
        Assert.Equal("pong", pong.GetProperty("type").GetString());

        await admin.PostAsJsonAsync($"/api/external/keys/{keyId}/revoke", new { });
    }

    [Fact]
    public void Framing_Rejects_Oversized_And_Assembles()
    {
        var buffer = new byte[32];
        var length = 0;
        var frame = TcpFraming.Encode(new { type = "ping" });
        Buffer.BlockCopy(frame, 0, buffer, 0, frame.Length);
        length = frame.Length;
        Assert.True(TcpFraming.TryReadFrame(ref buffer, ref length, 1024, out var payload, out var err));
        Assert.Null(err);
        Assert.NotNull(payload);
        Assert.Equal(0, length);

        buffer = new byte[] { 0, 0, 0x10, 0x00 }; // 4096 bytes claimed
        length = 4;
        Assert.False(TcpFraming.TryReadFrame(ref buffer, ref length, 100, out _, out err));
        Assert.Equal("frame_too_large", err);
    }

    private static async Task WriteMessageAsync(Stream stream, object message)
    {
        var frame = TcpFraming.Encode(message);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task<JsonElement> ReadMessageAsync(Stream stream)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header);
        var size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        Assert.InRange(size, 1, 256 * 1024);
        var body = new byte[size];
        await ReadExactAsync(stream, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (n <= 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    public sealed class TcpSite : AuthorizationTests.Site
    {
        public int TcpPort { get; private set; } = 18000 + Random.Shared.Next(2000);

        protected override void ConfigureSettings(IWebHostBuilder b)
        {
            b.UseSetting("External:Tcp:Enabled", "true");
            b.UseSetting("External:Tcp:ListenAddress", "127.0.0.1");
            b.UseSetting("External:Tcp:Port", TcpPort.ToString());
            b.UseSetting("External:Tcp:MaxConnections", "16");
            b.UseSetting("External:Tcp:IdleTimeoutSec", "30");
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            // 等待监听起来
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    using var c = new TcpClient();
                    await c.ConnectAsync(IPAddress.Loopback, TcpPort);
                    return;
                }
                catch { await Task.Delay(50); }
            }
            throw new TimeoutException("TCP 服务未在预期端口监听");
        }
    }
}
