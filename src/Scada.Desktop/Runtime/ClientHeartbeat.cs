using Scada.Desktop.Api;
using Scada.Desktop.Configuration;

namespace Scada.Desktop.Runtime;

/// <summary>
/// 30 秒心跳只做在线/版本上报，失败绝不能影响画面。同一错误 5 分钟内只写一条日志。
/// </summary>
internal sealed class ClientHeartbeat : IDisposable
{
    private readonly Func<IHeartbeatClient?> _api;
    private readonly Func<SessionData?> _session;
    private readonly Func<HeartbeatPayload> _payload;
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _logGate = new();
    private string? _lastError;
    private DateTime _lastErrorUtc;
    private int _intervalSeconds = 30;

    /// <summary>当前生效周期（秒），供单测断言服务端下发值是否被采用。</summary>
    internal int IntervalSeconds => _intervalSeconds;
    private bool _disposed;

    public ClientHeartbeat(Func<IHeartbeatClient?> api, Func<SessionData?> session, Func<HeartbeatPayload> payload)
    {
        _api = api;
        _session = session;
        _payload = payload;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<string>? PrimaryUrlDiscovered;

    /// <summary>
    /// 按服务端下发的周期启动。周期由服务端配置（DesktopClient.HeartbeatSeconds），
    /// 客户端不再写死 30 秒；响应里的值变化时会自动重排。
    /// </summary>
    public void Start(int seconds = 30)
    {
        if (_disposed) return;
        Rearm(seconds);
    }

    private void Rearm(int seconds)
    {
        _intervalSeconds = Math.Clamp(seconds, 10, 600);
        var interval = TimeSpan.FromSeconds(_intervalSeconds);
        _timer.Change(interval, interval);
    }

    public async Task<HeartbeatResult?> SendNowAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !_gate.Wait(0)) return null;
        try
        {
            var api = _api();
            var session = _session();
            if (api is null || session is null) return null;
            var result = await api.HeartbeatAsync(session.Token, _payload(), cancellationToken).ConfigureAwait(false);
            if (!_disposed && result.HeartbeatSeconds is >= 10 and <= 600 && result.HeartbeatSeconds != _intervalSeconds)
                Rearm(result.HeartbeatSeconds);
            lock (_logGate)
            {
                _lastError = null;
            }
            return result;
        }
        catch (ApiException exception)
        {
            if (!string.IsNullOrWhiteSpace(exception.PrimaryUrl))
            {
                try { PrimaryUrlDiscovered?.Invoke(exception.PrimaryUrl!); }
                catch (Exception inner) { DesktopLogger.Warn("处理主备切换信号失败：" + inner.Message); }
            }
            LogOnce(exception.Message);
            return null;
        }
        catch (Exception exception)
        {
            LogOnce(exception.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TickAsync()
    {
        try
        {
            await SendNowAsync().ConfigureAwait(false);
        }
        catch
        {
            // 心跳失败已在 SendNowAsync 内记录。
        }
    }

    private void LogOnce(string message)
    {
        lock (_logGate)
        {
            var now = DateTime.UtcNow;
            if (string.Equals(_lastError, message, StringComparison.Ordinal) && now - _lastErrorUtc < TimeSpan.FromMinutes(5))
                return;
            _lastError = message;
            _lastErrorUtc = now;
        }
        DesktopLogger.Warn("桌面客户端心跳失败：" + message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
