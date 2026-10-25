using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Engine.Control;

namespace Scada.Server.External.Tcp;

public sealed class ExternalTcpHost(
    IOptionsMonitor<ExternalIntegrationOptions> options,
    ExternalApiKeyService keys,
    IRealtimeCache cache,
    IServiceScopeFactory scopes,
    ILogger<ExternalTcpHost> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private TcpListener? _listener;
    private X509Certificate2? _tlsCert;

    public int ConnectionCount => _sessions.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => PushLoopAsync(stoppingToken), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue.Tcp;
            if (!opt.Enabled)
            {
                await StopListenerAsync();
                await Task.Delay(1500, stoppingToken);
                continue;
            }
            try { await AcceptLoopAsync(opt, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "对外 TCP 监听异常，即将重试");
                await StopListenerAsync();
                await Task.Delay(2000, stoppingToken);
            }
        }
        await StopListenerAsync();
    }

    private async Task AcceptLoopAsync(ExternalTcpOptions opt, CancellationToken ct)
    {
        if (opt.Tls)
        {
            _tlsCert?.Dispose();
            _tlsCert = LoadCert(opt);
        }
        var ip = IPAddress.TryParse(opt.ListenAddress, out var parsed) ? parsed : IPAddress.Any;
        _listener = new TcpListener(ip, opt.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start(Math.Clamp(opt.MaxConnections, 1, 1000));
        logger.LogInformation("对外 TCP 监听 {Addr}:{Port} tls={Tls}", opt.ListenAddress, opt.Port, opt.Tls);
        while (!ct.IsCancellationRequested && options.CurrentValue.Tcp.Enabled)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private Task StopListenerAsync()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        return Task.CompletedTask;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken hostCt)
    {
        var opt = options.CurrentValue.Tcp;
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        if (opt.IpAllowlist is { Length: > 0 } && remote is not null)
        {
            var addr = remote.Address;
            var ip = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
            var loopbackOk = IPAddress.IsLoopback(addr) && opt.IpAllowlist.Any(a => a is "127.0.0.1" or "::1" or "localhost");
            if (!loopbackOk && !opt.IpAllowlist.Any(a => a == "*" || a == ip))
            {
                logger.LogWarning("TCP 拒绝未在白名单的地址 {Ip}", addr);
                client.Dispose();
                return;
            }
        }
        if (_sessions.Count >= Math.Max(1, opt.MaxConnections))
        {
            client.Dispose();
            return;
        }

        Stream stream = client.GetStream();
        try
        {
            if (opt.Tls)
            {
                if (_tlsCert is null) throw new InvalidOperationException("TLS 证书未加载");
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _tlsCert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, hostCt);
                stream = ssl;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TCP TLS 握手失败");
            client.Dispose();
            return;
        }

        var session = new Session(Guid.NewGuid(), remote?.Address.ToString(), stream);
        _sessions[session.Id] = session;
        var buffer = new byte[Math.Max(8192, Math.Min(opt.MaxFrameBytes, 1024 * 1024))];
        var length = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        idle.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.IdleTimeoutSec, 10, 3600)));
        try
        {
            await SendAsync(session, new TcpMessage { Type = "hello", Data = new { protocol = 1, framing = "be32+json", session = session.Id } }, idle.Token);
            while (!idle.IsCancellationRequested)
            {
                if (length >= buffer.Length)
                {
                    await SendAsync(session, Err(null, TcpErrorCodes.FrameTooLarge, "缓冲溢出"), idle.Token);
                    break;
                }
                var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), idle.Token);
                if (read <= 0) break;
                length += read;
                idle.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.Tcp.IdleTimeoutSec, 10, 3600)));
                while (true)
                {
                    var ok = TcpFraming.TryReadFrame(ref buffer, ref length, options.CurrentValue.Tcp.MaxFrameBytes, out var payload, out var frameError);
                    if (frameError is not null)
                    {
                        await SendAsync(session, Err(null, TcpErrorCodes.FrameTooLarge, "帧过大"), idle.Token);
                        return;
                    }
                    if (!ok) break;
                    await DispatchAsync(session, payload!, idle.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "TCP 会话结束 {Id}", session.Id); }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            try { await stream.DisposeAsync(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private async Task DispatchAsync(Session session, byte[] payload, CancellationToken ct)
    {
        TcpMessage? msg;
        try { msg = JsonSerializer.Deserialize<TcpMessage>(payload, TcpFraming.Json); }
        catch
        {
            await SendAsync(session, Err(null, TcpErrorCodes.BadRequest, "JSON 无效"), ct);
            return;
        }
        if (msg is null || string.IsNullOrWhiteSpace(msg.Type))
        {
            await SendAsync(session, Err(null, TcpErrorCodes.BadRequest, "缺少 type"), ct);
            return;
        }
        try
        {
            switch (msg.Type.ToLowerInvariant())
            {
                case "auth":
                    await AuthAsync(session, msg, ct);
                    break;
                case "ping":
                    await SendAsync(session, new TcpMessage { Type = "pong", Id = msg.Id }, ct);
                    break;
                case "subscribe":
                    EnsureAuth(session);
                    session.Subscriptions.UnionWith(FilterTags(session, msg.TagIds ?? []));
                    await SendAsync(session, new TcpMessage { Type = "subscribed", Id = msg.Id, TagIds = session.Subscriptions.ToArray() }, ct);
                    break;
                case "unsubscribe":
                    EnsureAuth(session);
                    foreach (var id in msg.TagIds ?? []) session.Subscriptions.Remove(id);
                    await SendAsync(session, new TcpMessage { Type = "unsubscribed", Id = msg.Id, TagIds = session.Subscriptions.ToArray() }, ct);
                    break;
                case "poll":
                    EnsureAuth(session);
                    EnsureScope(session, ExternalApiScope.ReadTags);
                    var ids = msg.TagIds is { Length: > 0 } ? FilterTags(session, msg.TagIds) : session.Subscriptions.ToArray();
                    var values = cache.GetMany(ids).Select(v => new { v.TagId, v.Value, quality = v.Quality.ToString(), v.Ts });
                    await SendAsync(session, new TcpMessage { Type = "snapshot", Id = msg.Id, Data = values }, ct);
                    break;
                case "write":
                    await WriteAsync(session, msg, ct);
                    break;
                default:
                    await SendAsync(session, Err(msg.Id, TcpErrorCodes.BadRequest, "未知 type：" + msg.Type), ct);
                    break;
            }
        }
        catch (TcpSessionException ex)
        {
            await SendAsync(session, Err(msg.Id, ex.Code, ex.Message), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TCP 处理失败");
            await SendAsync(session, Err(msg.Id, TcpErrorCodes.Internal, "内部错误"), ct);
        }
    }

    private async Task WriteAsync(Session session, TcpMessage msg, CancellationToken ct)
    {
        EnsureAuth(session);
        EnsureScope(session, ExternalApiScope.Write);
        if (msg.TagId is not int tagId || msg.Value is not double value)
        {
            await SendAsync(session, Err(msg.Id, TcpErrorCodes.BadRequest, "需要 tagId 与 value"), ct);
            return;
        }
        if (!session.Access!.CanTag(Permission.Control, tagId))
        {
            await SendAsync(session, Err(msg.Id, TcpErrorCodes.Forbidden, "无权控制该测点"), ct);
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<ControlCommandService>();
        var result = await commands.SubmitAsync(new ControlSubmission(tagId, value,
            new ControlActor(null, "apikey:" + session.Key!.Name), ControlOrigin.Operator, "外部 TCP",
            msg.Confirmed ?? true, msg.RequestId), ct);
        await SendAsync(session, new TcpMessage
        {
            Type = "writeResult",
            Id = msg.Id,
            Data = new
            {
                result.Success,
                result.Message,
                result.ReadBack,
                result.CommandId,
                outcome = result.Outcome?.ToString(),
                result.Pending,
                result.ApprovalId,
                result.Refusals
            }
        }, ct);
    }

    private async Task AuthAsync(Session session, TcpMessage msg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(msg.ApiKey))
        {
            await SendAsync(session, Err(msg.Id, TcpErrorCodes.Unauthorized, "缺少 apiKey"), ct);
            return;
        }
        var key = await keys.FindBySecretAsync(msg.ApiKey, ct);
        if (key is null)
        {
            await SendAsync(session, Err(msg.Id, TcpErrorCodes.Unauthorized, "无效密钥"), ct);
            return;
        }
        if (!keys.TryAcquire(key, out _))
        {
            await SendAsync(session, Err(msg.Id, TcpErrorCodes.RateLimited, "请求过于频繁"), ct);
            return;
        }
        session.Key = key;
        session.Access = await keys.AccessForKeyAsync(key, ct);
        session.Scopes = key.Scopes;
        _ = keys.TouchAsync(key.Id, CancellationToken.None);
        await SendAsync(session, new TcpMessage
        {
            Type = "authOk",
            Id = msg.Id,
            Data = new
            {
                key = key.Name,
                scopes = ExternalApiKeyService.View(key).Scopes,
                areas = ExternalApiKeyService.ParseAreas(key.AreaIdsJson)
            }
        }, ct);
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        var last = new ConcurrentDictionary<(Guid Sid, int TagId), (double Value, long Ts)>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct);
                foreach (var session in _sessions.Values.Where(s => s.Key is not null && s.Subscriptions.Count > 0).ToArray())
                {
                    if (!ExternalApiKeyService.HasScope(session.Scopes, ExternalApiScope.ReadTags)) continue;
                    var updates = new List<object>();
                    foreach (var tagId in session.Subscriptions.ToArray())
                    {
                        var v = cache.Get(tagId);
                        if (v is null) continue;
                        var key = (session.Id, tagId);
                        if (last.TryGetValue(key, out var prev) && prev.Value == v.Value && prev.Ts == v.Ts) continue;
                        last[key] = (v.Value, v.Ts);
                        updates.Add(new { v.TagId, v.Value, quality = v.Quality.ToString(), v.Ts });
                    }
                    if (updates.Count == 0) continue;
                    try { await SendAsync(session, new TcpMessage { Type = "update", Data = updates }, ct); }
                    catch { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private static void EnsureAuth(Session session)
    {
        if (session.Key is null || session.Access is null) throw new TcpSessionException(TcpErrorCodes.Unauthorized, "请先 auth");
    }

    private static void EnsureScope(Session session, ExternalApiScope need)
    {
        if (!ExternalApiKeyService.HasScope(session.Scopes, need))
            throw new TcpSessionException(TcpErrorCodes.Forbidden, "缺少权限：" + need);
    }

    private static int[] FilterTags(Session session, IEnumerable<int> ids) =>
        ids.Where(id => session.Access!.CanTag(Permission.View, id)).Distinct().ToArray();

    private static TcpMessage Err(string? id, string code, string message) => new() { Type = "error", Id = id, Code = code, Message = message };

    private static async Task SendAsync(Session session, object message, CancellationToken ct)
    {
        var frame = TcpFraming.Encode(message);
        await session.WriteGate.WaitAsync(ct);
        try
        {
            await session.Stream.WriteAsync(frame, ct);
            await session.Stream.FlushAsync(ct);
        }
        finally { session.WriteGate.Release(); }
    }

    private static X509Certificate2 LoadCert(ExternalTcpOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.TlsCertPath) && File.Exists(opt.TlsCertPath))
            return X509CertificateLoader.LoadPkcs12FromFile(opt.TlsCertPath, opt.TlsCertPassword);
        throw new InvalidOperationException("启用 TCP TLS 时必须配置存在的 TlsCertPath");
    }

    private sealed class Session(Guid id, string? remoteIp, Stream stream)
    {
        public Guid Id { get; } = id;
        public string? RemoteIp { get; } = remoteIp;
        public Stream Stream { get; } = stream;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public ExternalApiKey? Key { get; set; }
        public AccessContext? Access { get; set; }
        public ExternalApiScope Scopes { get; set; }
        public HashSet<int> Subscriptions { get; } = [];
    }

    private sealed class TcpSessionException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
