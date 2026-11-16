using Scada.Desktop.Api;
using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class ClientHeartbeatTests
{
    private static readonly SessionData Session = new("token", "2099-01-01", "{}", null, false);

    [Fact]
    public async Task ReturnsNullWithoutApiOrSession()
    {
        using var withoutApi = new ClientHeartbeat(() => null, () => Session, Payload);
        Assert.Null(await withoutApi.SendNowAsync());

        var api = new FakeApi();
        using var withoutSession = new ClientHeartbeat(() => api, () => null, Payload);
        Assert.Null(await withoutSession.SendNowAsync());
        Assert.Equal(0, api.Calls);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(45, 45)]
    [InlineData(9999, 600)]
    public void StartClampsToSupportedRange(int requested, int expected)
    {
        using var heartbeat = new ClientHeartbeat(() => new FakeApi(), () => Session, Payload);
        heartbeat.Start(requested);
        Assert.Equal(expected, heartbeat.IntervalSeconds);
    }

    /// <summary>服务端把周期改成 60 秒后，客户端下一次心跳要按新值重排，而不是继续 30 秒。</summary>
    [Fact]
    public async Task ServerIntervalRearmsTimer()
    {
        var api = new FakeApi { Result = new HeartbeatResult(true, 0, "n", "Primary", null, 60) };
        using var heartbeat = new ClientHeartbeat(() => api, () => Session, Payload);
        heartbeat.Start(30);

        var result = await heartbeat.SendNowAsync();

        Assert.NotNull(result);
        Assert.Equal(60, heartbeat.IntervalSeconds);
    }

    [Fact]
    public async Task OutOfRangeServerIntervalIsIgnored()
    {
        var api = new FakeApi { Result = new HeartbeatResult(true, 0, "n", "Primary", null, 5) };
        using var heartbeat = new ClientHeartbeat(() => api, () => Session, Payload);
        heartbeat.Start(30);

        await heartbeat.SendNowAsync();

        Assert.Equal(30, heartbeat.IntervalSeconds);
    }

    /// <summary>心跳失败只能记日志，不能把异常抛给画面线程。</summary>
    [Fact]
    public async Task FailuresAreSwallowed()
    {
        var api = new FakeApi { Error = new ApiException("离线") };
        using var heartbeat = new ClientHeartbeat(() => api, () => Session, Payload);

        Assert.Null(await heartbeat.SendNowAsync());
    }

    private static HeartbeatPayload Payload() => new(
        Guid.NewGuid().ToString("D"), "PC", "operator", "1.0.0", "131.0", "boot", "screen", 1, "总览", "user", null);

    private sealed class FakeApi : IHeartbeatClient
    {
        public HeartbeatResult? Result { get; init; }
        public Exception? Error { get; init; }
        public int Calls { get; private set; }

        public Task<HeartbeatResult> HeartbeatAsync(string token, HeartbeatPayload payload, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Error is not null
                ? Task.FromException<HeartbeatResult>(Error)
                : Task.FromResult(Result ?? new HeartbeatResult(true, 0, "n", "Primary", null, 30));
        }
    }
}
