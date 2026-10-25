using System.Buffers;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Protocols;

namespace Scada.Collector.Mqtt;

/// <summary>MQTTnet 客户端的薄封装：连接参数、TLS/认证、订阅与收消息回调。断线后由采集引擎按退避重连。</summary>
internal sealed class MqttSession : IAsyncDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly HashSet<string> _subscribed = [];
    public event Action<string, byte[]>? Message;
    public event Action<string>? Disconnected;
    public bool IsConnected => _client.IsConnected;

    public MqttSession()
    {
        _client.ApplicationMessageReceivedAsync += e =>
        {
            Message?.Invoke(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray());
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += e => { Disconnected?.Invoke(e.Reason.ToString()); return Task.CompletedTask; };
    }

    public sealed record Settings(string ClientId, MqttVersion Version, bool UseTls, bool AllowUntrusted, int KeepAliveSec, bool CleanSession,
        (string Topic, byte[] Payload)? Will = null);

    public async Task ConnectAsync(Device device, Settings s, IDeviceCredentials credentials, CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(device.Host, device.Port)
            .WithClientId(string.IsNullOrEmpty(s.ClientId) ? $"scada-{device.Id}-{Guid.NewGuid():N}"[..23] : s.ClientId)
            .WithProtocolVersion(s.Version == MqttVersion.V500 ? MqttProtocolVersion.V500 : MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(s.KeepAliveSec))
            .WithCleanSession(s.CleanSession)
            .WithTimeout(TimeSpan.FromMilliseconds(device.TimeoutMs));
        if (!string.IsNullOrEmpty(device.OpcUserName))
            builder.WithCredentials(device.OpcUserName, device.OpcPasswordProtected is null ? "" : credentials.Unprotect(device.OpcPasswordProtected));
        if (s.UseTls)
            builder.WithTlsOptions(o =>
            {
                o.UseTls();
                if (s.AllowUntrusted) o.WithCertificateValidationHandler(_ => true);
            });
        if (s.Will is { } will)
            builder.WithWillTopic(will.Topic).WithWillPayload(will.Payload).WithWillRetain().WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(device.TimeoutMs);
        try
        {
            var result = await _client.ConnectAsync(builder.Build(), deadline.Token);
            if (result.ResultCode != MqttClientConnectResultCode.Success) throw new IOException($"MQTT 代理拒绝连接：{result.ResultCode} {result.ReasonString}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("MQTT 连接超时"); }
        _subscribed.Clear();
    }

    /// <summary>订阅尚未订阅的主题，返回新订阅数量。</summary>
    public async Task<int> EnsureSubscribedAsync(IEnumerable<string> topics, int qos, int timeoutMs, CancellationToken ct)
    {
        var pending = topics.Where(t => !_subscribed.Contains(t)).Distinct().ToArray();
        foreach (var batch in pending.Chunk(50))
        {
            var options = new MqttClientSubscribeOptionsBuilder();
            foreach (var topic in batch) options.WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
            var result = await _client.SubscribeAsync(options.Build(), deadline.Token);
            var rejected = result.Items.FirstOrDefault(x => (int)x.ResultCode > 2);
            if (rejected is not null) throw new IOException($"代理拒绝订阅 {rejected.TopicFilter.Topic}：{rejected.ResultCode}");
            foreach (var topic in batch) _subscribed.Add(topic);
        }
        return pending.Length;
    }

    public async Task PublishAsync(string topic, byte[] payload, int qos, bool retain, int timeoutMs, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos).WithRetainFlag(retain).Build();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
        try
        {
            var result = await _client.PublishAsync(message, deadline.Token);
            if (!result.IsSuccess) throw new IOException($"代理拒绝发布：{result.ReasonCode} {result.ReasonString}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("MQTT 发布超时，结果未知"); }
    }

    public async Task DisconnectAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), new CancellationTokenSource(1000).Token); }
        catch { /* 断开过程中的错误无需上报 */ }
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); _client.Dispose(); }
}
