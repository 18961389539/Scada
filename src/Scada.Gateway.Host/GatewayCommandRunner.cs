using System.Security.Cryptography;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Models;

namespace Scada.Gateway.Hosting;

public sealed class GatewayCommandRunner(GatewayAgentStore store, GatewayAgentClient client, IGatewayAcquisitionRuntime runtime, IDataProtectionProvider protection)
{
    private readonly IDataProtector _tokens = protection.CreateProtector("Scada.Gateway.ExecutionToken.v1");
    private int _identityRejected;
    public bool TakeIdentityRejection() => Interlocked.Exchange(ref _identityRejected, 0) != 0;
    private void ObserveIdentityFailure(Exception ex)
    {
        if (ex is GatewayHttpException http && (http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || http.StatusCode == HttpStatusCode.Conflict && http.Code != "command_not_executable")) Interlocked.Exchange(ref _identityRejected, 1);
    }
    public async Task ExecuteAsync(GatewayCommand command, Guid session, long serverNow, CancellationToken ct)
    {
        GatewayStoredCommand local;
        try { local = await store.ReserveCommandAsync(command, serverNow, ct); }
        catch (GatewaySpoolFullException)
        {
            // Never obtain an execution grant when a new local reservation cannot be persisted.
            try { await client.ReportAsync(session, new(command.Id, store.StreamId, null, new(GatewayCommandOutcome.NotSent, "网关命令日志已满，未预留或执行命令")), ct); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException) { ObserveIdentityFailure(ex); }
            return;
        }
        if (local.State != "Reserved") return;
        var started = false;
        GatewayCommandResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (runtime.Configuration?.Revision != command.ConfigurationRevision || runtime.HasFaultedWorker) throw new InvalidDataException("采集配置尚未就绪");
            var requestedAt = Environment.TickCount64;
            var grant = await client.AuthorizeAsync(session, command.Id, ct);
            var elapsed = Environment.TickCount64 - requestedAt;
            if (grant.CommandId != command.Id || grant.SessionId != session || grant.ConfigurationRevision != command.ConfigurationRevision
                || grant.ExpiresTs != command.ExpiresTs || grant.ServerTs < command.CreatedTs || grant.ExecutionToken is null
                || grant.ExecutionToken.Length is < 40 or > 256 || grant.ExecutionToken.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
                throw new InvalidDataException("命令执行授权响应无效");
            // Subtract the entire round trip, never extend a grant based on the local wall clock.
            var remaining = grant.ExpiresTs - grant.ServerTs - elapsed;
            if (remaining <= 0) throw new OperationCanceledException("执行授权已到期");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            started = await store.MarkCommandStartedAsync(command.Id, _tokens.Protect(grant.ExecutionToken), grant.ServerTs + elapsed, deadline.Token);
            if (!started) return;
            deadline.Token.ThrowIfCancellationRequested();
            var execution = await runtime.WriteAsync(command, deadline.Token);
            result = new(execution.Success ? GatewayCommandOutcome.Confirmed : execution.Outcome ?? GatewayCommandOutcome.Unknown,
                SafeMessage(execution.Message), execution.ReadBack);
        }
        catch (OperationCanceledException)
        { result = new(started ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent, started ? "命令已开始后超时或取消，执行结果未知；不会自动重发" : "命令在取得并保存执行授权前取消或过期，未下发"); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or ArgumentException or CryptographicException or System.Text.Json.JsonException)
        { ObserveIdentityFailure(ex); result = new(started ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent, started ? "执行期间发生故障，结果未知；请检查现场反馈，不会自动重发" : "未取得有效的在线执行授权，命令未下发"); }
        if (started) await store.CompleteCommandAsync(command.Id, result, CancellationToken.None);
        else await store.AbandonReservedCommandAsync(command.Id, result.Message, CancellationToken.None);
    }
    public async Task ReportPendingAsync(Guid session, CancellationToken ct)
    {
        foreach (var entry in (await store.UnreportedResultsAsync(ct)).Take(10))
        {
            var token = entry.ProtectedExecutionToken is null ? null : _tokens.Unprotect(entry.ProtectedExecutionToken);
            var ack = await client.ReportAsync(session, new(entry.Command.Id, store.StreamId, token, entry.Result!), ct);
            if (ack.CommandId != entry.Command.Id) throw new InvalidDataException("命令回执确认标识不匹配");
            await store.AcknowledgeCommandResultAsync(entry.Command.Id, ct);
        }
    }
    private string SafeMessage(string value)
    {
        foreach (var device in runtime.Configuration?.Devices ?? [])
            if (!string.IsNullOrEmpty(device.OpcPassword)) value = value.Replace(device.OpcPassword, "[redacted]", StringComparison.Ordinal);
        return value.Length > 1000 ? value[..1000] : value;
    }
}
