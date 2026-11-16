namespace Scada.Desktop.Api;

/// <summary>心跳只需要"发一次并拿回策略"，抽成接口让 ClientHeartbeat 可以脱离 Http 单测。</summary>
internal interface IHeartbeatClient
{
    Task<HeartbeatResult> HeartbeatAsync(string token, HeartbeatPayload payload, CancellationToken cancellationToken = default);
}
