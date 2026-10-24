using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Models;

namespace Scada.Gateway.Hosting;

public sealed class GatewayAgentService(GatewayAgentOptions options, GatewayAgentStore store, GatewayConfigurationCache cache,
    IGatewayAcquisitionRuntime runtime, GatewayAgentClient client, IHostApplicationLifetime lifetime, ILogger<GatewayAgentService> logger) : BackgroundService
{
    private readonly Guid _instance = Guid.NewGuid();
    private GatewaySavedState _saved = null!;
    private GatewaySession? _session;
    private GatewayTelemetryUploader? _uploader;
    private Task<GatewaySavedState>? _applying;
    private CancellationTokenSource? _applyCancellation;
    private long _leaseDeadline, _heartbeatDue, _configurationDue, _retryDue, _statusDue, _workerRetryDue;
    private bool _checkedConfiguration, _online;
    private string? _error, _lastState;
    private static long Tick => Environment.TickCount64;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        GatewayProcessLease? lease = null;
        try
        {
            lease = store.AcquireProcessLease();
            await store.RecoverInterruptedCommandsAsync(lease, stoppingToken);
            _saved = await cache.LoadAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CompleteApplyAsync();
                if (_session is not null && Tick >= _leaseDeadline) ResetSession();
                if (Tick >= _retryDue)
                {
                    try { await ExchangeAsync(stoppingToken); _retryDue = 0; }
                    catch (GatewayHttpException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        || ex.StatusCode == HttpStatusCode.Conflict)
                    {
                        await BlockAsync("中心拒绝网关身份或会话，采集已停止");
                        _retryDue = Tick + options.RetryDelayMs;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        if (ex is GatewayHttpException { StatusCode: HttpStatusCode.BadRequest })
                        { _checkedConfiguration = false; _heartbeatDue = _configurationDue = 0; }
                        _online = false; _error = ex is GatewayHttpException http ? $"中心返回 HTTP {(int)http.StatusCode!}，待传数据保留" : "中心连接暂时不可用，待传数据保留";
                        _retryDue = Tick + options.RetryDelayMs;
                    }
                }
                // Offline restart is allowed only after a previously acknowledged installation, with the same key.
                if (_session is null && _applying is null && runtime.Configuration is null && _saved.Confirmed && !_saved.Blocked
                    && _saved.KeyStamp == cache.KeyStamp && _saved.Configuration is not null)
                    BeginApply(_saved.Configuration, preserveConfirmation: true, stoppingToken);
                var spool = await store.StatusAsync(stoppingToken);
                if (_applying is null && runtime.HasFaultedWorker && spool.PendingBytes < options.MaxPendingBytes * .8 && Tick >= _workerRetryDue)
                { runtime.RetryFaultedWorkers(); _workerRetryDue = Tick + 5000; }
                await WriteStatusAsync(spool, stoppingToken);
                await Task.Delay(_online && spool.PendingBatches > 0 ? 1 : 100, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Do not log raw configuration, network bodies, or driver exceptions containing credentials.
            Environment.ExitCode = 1; logger.LogError("网关无法安全启动或保存本地状态；保留数据目录，检查安装归属、密钥文件、磁盘及进程占用。");
            lifetime.StopApplication();
        }
        finally
        {
            if (lease is not null)
            {
                _applyCancellation?.Cancel();
                try { if (_applying is not null) await _applying; } catch (Exception) { }
                // Hold the OS lease until the previous driver I/O has actually exited.
                await runtime.StopAsync();
                if (_session is not null)
                    try { await client.GoodbyeAsync(_session.SessionId, CancellationToken.None); } catch (Exception) { }
                _uploader?.Dispose(); _applyCancellation?.Dispose(); lease.Dispose();
            }
        }
    }

    private async Task ExchangeAsync(CancellationToken ct)
    {
        if (_session is null)
        {
            var session = await client.HelloAsync(_instance, store.StreamId, ct);
            var spool = await store.StatusAsync(ct);
            if (session.ReceivedThrough < spool.LastAcknowledged || session.ReceivedThrough >= spool.NextSequence)
            {
                await BlockAsync("中心与本地的持久接收序号不一致，保留队列并停止采集");
                throw new InvalidDataException("中心接收序号不一致");
            }
            AcceptSession(session); _session = session; _checkedConfiguration = false;
            _uploader = new(store, options.Server, options.GatewayId, options.Token, session.SessionId, options.AllowLoopbackHttp, options.HttpTimeoutMs);
            _heartbeatDue = _configurationDue = 0;
        }
        if (Tick >= _heartbeatDue)
        {
            var spool = await store.StatusAsync(ct);
            var revision = _applying is null && _checkedConfiguration ? runtime.Configuration?.Revision : null;
            var reply = await client.HeartbeatAsync(_session.SessionId, new(revision, spool.PendingBatches, spool.PendingBytes, spool.OldestTs,
                runtime.HasFaultedWorker ? "采集任务异常暂停，待传数据保留" : null, revision is null ? [] : SafeStatuses()), ct);
            AcceptSession(reply); _heartbeatDue = Tick + options.HeartbeatIntervalMs;
            if (revision is not null && (!_saved.Confirmed || _saved.Blocked || _saved.KeyStamp != cache.KeyStamp))
            {
                _saved = _saved with { Confirmed = true, Blocked = false, KeyStamp = cache.KeyStamp };
                await cache.SaveAsync(_saved, ct);
            }
        }
        if (_applying is null && Tick >= _configurationDue)
        {
            var known = _saved.Confirmed && !_saved.Blocked && _saved.KeyStamp == cache.KeyStamp ? runtime.Configuration?.Revision : null;
            GatewayConfiguration? configuration;
            try { configuration = await client.ConfigurationAsync(_session.SessionId, known, ct); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            { await BlockAsync("中心配置响应无效，采集已停止"); throw; }
            _checkedConfiguration = true; _configurationDue = Tick + options.ConfigurationPollMs;
            if (configuration is not null)
            {
                // Stop old acquisition before validating or persisting a newly issued configuration.
                BeginApply(configuration, preserveConfirmation: false, ct);
            }
            else _heartbeatDue = 0;
        }
        if (_uploader is not null) await _uploader.UploadNextAsync(ct);
        _online = true; _error = null;
    }

    private void AcceptSession(GatewaySession reply)
    {
        var duration = reply.LeaseUntilTs - reply.ServerTs;
        if (reply.Version != GatewayProtocol.Version || reply.SessionId == Guid.Empty || duration is < 1000 or > 120000 || reply.ReceivedThrough < 0
            || _session is not null && _session.SessionId != reply.SessionId) throw new InvalidDataException("中心会话响应无效");
        _leaseDeadline = Tick + duration;
    }
    private void ResetSession()
    {
        _session = null; _checkedConfiguration = false; _online = false; _uploader?.Dispose(); _uploader = null;
    }
    private void BeginApply(GatewayConfiguration configuration, bool preserveConfirmation, CancellationToken ct)
    {
        _applyCancellation?.Dispose(); _applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _applying = ApplyAsync(configuration, preserveConfirmation, _applyCancellation.Token);
    }
    private async Task<GatewaySavedState> ApplyAsync(GatewayConfiguration configuration, bool preserveConfirmation, CancellationToken ct)
    {
        // Revoke offline restart permission before waiting for potentially slow old driver I/O.
        if (!preserveConfirmation) await cache.SaveAsync(_saved with { Confirmed = false }, ct);
        await runtime.StopAsync();
        runtime.Validate(configuration);
        var saved = _saved with { Configuration = configuration, Confirmed = preserveConfirmation && _saved.Confirmed, KeyStamp = cache.KeyStamp };
        await cache.SaveAsync(saved, ct);
        await runtime.StartAsync(configuration, ct);
        return saved;
    }
    private async Task CompleteApplyAsync()
    {
        if (_applying?.IsCompleted != true) return;
        try { _saved = await _applying; _heartbeatDue = 0; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or CryptographicException)
        { await runtime.StopAsync(); _saved = _saved with { Confirmed = false, Blocked = true }; await cache.SaveAsync(_saved, CancellationToken.None); _error = "中心配置无效或无法解密，采集已停止"; }
        finally { _applying = null; }
    }
    private async Task BlockAsync(string error)
    {
        ResetSession(); _applyCancellation?.Cancel();
        // Persist denial before waiting for a driver that may be slow to cancel.
        _saved = _saved with { Confirmed = false, Blocked = true };
        await cache.SaveAsync(_saved, CancellationToken.None); _error = error;
        if (_applying is not null)
        { try { await _applying; } catch (Exception) { } finally { _applying = null; } }
        await runtime.StopAsync();
    }
    private string Redact(string? value)
    {
        var result = (value ?? "").Replace(options.Token, "[redacted]", StringComparison.Ordinal);
        foreach (var item in _saved.Configuration?.Devices ?? [])
            if (!string.IsNullOrEmpty(item.OpcPassword)) result = result.Replace(item.OpcPassword, "[redacted]", StringComparison.Ordinal);
        return result.Length > 1000 ? result[..1000] : result;
    }
    private IReadOnlyList<CollectorDeviceStatus> SafeStatuses() => runtime.Statuses.Select(x => x with { Error = x.Error is null ? null : Redact(x.Error) }).ToArray();
    private async Task WriteStatusAsync(GatewaySpoolStatus spool, CancellationToken ct)
    {
        if (Tick < _statusDue) return; _statusDue = Tick + 1000;
        var paused = _applying is null && runtime.HasFaultedWorker;
        var state = _applying is not null ? "ApplyingConfiguration" : _saved.Blocked ? "Blocked" : paused ? "AcquisitionPaused" : _online ? "Running" : runtime.Configuration is not null ? "OfflineCollecting" : "Connecting";
        if (state != _lastState) { logger.LogInformation("网关状态：{State}", state); _lastState = state; }
        var status = new { gatewayId = options.GatewayId, state, revision = runtime.Configuration?.Revision, streamId = spool.StreamId,
            confirmed = _applying is null && _saved.Confirmed, blocked = _saved.Blocked, centerReachable = _online, acquisitionPaused = paused,
            spool.PendingBatches, spool.PendingBytes, spool.LastAcknowledged, spool.NextSequence,
            devices = _applying is null ? SafeStatuses() : [], error = _error, updatedTs = ScadaTime.NowMs() };
        try
        {
            var path = Path.Combine(options.DataRoot, "status.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(status, GatewayProtocol.Json), ct); File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { /* The durable queue, rather than this optional diagnostic file, governs sampling. */ }
    }
}
