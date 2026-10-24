using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Server.Services;

public sealed partial class GatewayCenterService
{
    private sealed class EngineeringPending(int gatewayId, Guid sessionId, GatewayEngineeringRequest request, long deadline)
    {
        public int GatewayId = gatewayId; public Guid SessionId = sessionId; public GatewayEngineeringRequest Request = request;
        public long Deadline = deadline; public TaskCompletionSource<GatewayEngineeringReply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly ConcurrentDictionary<Guid, EngineeringPending> _engineering = new();
    public async Task<T> EngineerAsync<T>(int deviceId, GatewayEngineeringOperation operation, object payload, int userId, CancellationToken ct, int? expectedGatewayId = null)
    {
        if (!Enum.IsDefined(operation)) throw new GatewayRequestException(400, "工程操作类型无效");
        var json = JsonSerializer.SerializeToElement(payload, GatewayProtocol.Json);
        if (Encoding.UTF8.GetByteCount(json.GetRawText()) > 8 * 1024 * 1024) throw new GatewayRequestException(413, "工程操作参数过大");
        EngineeringPending pending; int? auditId = null;
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new GatewayRequestException(404, "设备不存在");
            if (device.GatewayId is not int gatewayId) throw new GatewayRequestException(409, "设备采集位置已变化，请刷新后重试");
            if (expectedGatewayId is not null && gatewayId != expectedGatewayId) throw new GatewayRequestException(409, "设备采集位置已变化，请重新核对证书作用范围");
            if (!await EngineeringPermissionAsync(db, userId, operation, ct)) throw new GatewayRequestException(403, "没有此工程操作的权限");
            var gateway = await db.Gateways.SingleAsync(x => x.Id == gatewayId, ct);
            if (JsonSerializer.Deserialize<string[]>(gateway.CapabilitiesJson, GatewayProtocol.Json)?.Contains(GatewayProtocol.EngineeringCapability) != true)
                throw new GatewayRequestException(409, "此网关版本尚未支持远程工程操作，请升级并重新连接");
            var revision = (await BuildConfigurationAsync(db, gatewayId, ct, recordRevision: false)).Revision;
            if (!gateway.Enabled || gateway.SessionId is null || gateway.LeaseUntilTs <= Now || gateway.AppliedRevision != revision)
                throw new GatewayRequestException(409, "网关未在线确认当前配置，不能执行远程工程操作");
            if (_engineering.Count >= 16 || _engineering.Values.Count(x => x.GatewayId == gatewayId) >= 4 || _engineering.Values.Count(x => x.Request.UserId == userId) >= 4)
                throw new GatewayRequestException(429, "工程操作繁忙，请等待已有请求完成");
            var duration = Math.Clamp(device.TimeoutMs + 5000, 10000, 65000);
            var request = new GatewayEngineeringRequest(Guid.NewGuid(), deviceId, operation, json, revision, userId, Now, Now + duration);
            pending = new(gatewayId, gateway.SessionId.Value, request, Environment.TickCount64 + duration);
            if (EngineeringMutation(operation))
            {
                var user = await db.Users.SingleAsync(x => x.Id == userId, ct);
                var log = new OperationLog { UserId = userId, UserName = user.UserName, Ts = Now, Action = $"网关 #{gatewayId} 工程操作 {operation}：设备 #{deviceId}，请求 {request.Id:D}", Result = "待网关回执；修改结果未确认时须查询实际状态" };
                if (operation is GatewayEngineeringOperation.OpcTrust or GatewayEngineeringOperation.OpcUntrust && json.ValueKind == JsonValueKind.Object
                    && json.TryGetProperty("sha256", out var fingerprint) && fingerprint.ValueKind == JsonValueKind.String && fingerprint.GetString()?.Length <= 64) log.NewValue = fingerprint.GetString();
                db.OperationLogs.Add(log); await db.SaveChangesAsync(ct); auditId = log.Id;
            }
            await tx.CommitAsync(ct);
            if (!_engineering.TryAdd(request.Id, pending)) throw new InvalidOperationException("工程请求标识冲突");
        }
        finally { _mutations.Release(); }
        GatewayEngineeringReply? reply = null;
        try
        {
            var remaining = Math.Max(1, pending.Deadline - Environment.TickCount64);
            reply = await pending.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(remaining), ct);
            if (reply.StatusCode is < 200 or >= 300) throw new GatewayRequestException(reply.StatusCode, reply.Error ?? "远程工程操作失败");
            return reply.Data is { } data ? data.Deserialize<T>(GatewayProtocol.Json) ?? throw new GatewayRequestException(502, "网关工程响应缺少内容") : throw new GatewayRequestException(502, "网关工程响应缺少内容");
        }
        catch (TimeoutException) { throw new GatewayRequestException(504, "远程工程请求超时；修改类操作请查询实际状态，不能假定未执行"); }
        catch (JsonException) { throw new GatewayRequestException(502, "网关工程响应格式无效"); }
        finally
        {
            _engineering.TryRemove(pending.Request.Id, out _);
            if (auditId is int id)
            {
                try
                {
                    await using var db = await OpenAsync(CancellationToken.None); var log = await db.OperationLogs.FindAsync(id);
                    if (log is not null) { log.Result = reply is null ? "连接取消或超时，结果未确认，请查询实际状态" : reply.StatusCode is >= 200 and < 300 ? operation == GatewayEngineeringOperation.Reconnect ? "网关已接受重连请求" : "成功" : (reply.Error ?? "失败，结果未确认")[..Math.Min(500, (reply.Error ?? "失败，结果未确认").Length)]; await db.SaveChangesAsync(); }
                }
                catch (Exception ex) { _logger.LogError(ex, "保存网关工程操作审计结果失败"); }
            }
        }
    }
    public async Task<GatewayEngineeringOffer> EngineeringOfferAsync(GatewayCaller caller, Guid session, Guid? active, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); var gateway = await SessionAsync(db, caller, session, ct);
            foreach (var pending in _engineering.Values.Where(x => x.GatewayId == caller.Id && (active == null || x.Request.Id == active)).OrderBy(x => x.Request.CreatedTs))
            {
                if (pending.Completion.Task.IsCompleted) continue;
                var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pending.Request.DeviceId, ct);
                var revision = (await BuildConfigurationAsync(db, caller.Id, ct, recordRevision: false)).Revision;
                if (pending.SessionId != session || Environment.TickCount64 >= pending.Deadline || pending.Request.ExpiresTs <= Now || device?.GatewayId != caller.Id
                    || gateway.AppliedRevision != revision || pending.Request.ConfigurationRevision != revision || !await EngineeringPermissionAsync(db, pending.Request.UserId, pending.Request.Operation, ct))
                { pending.Completion.TrySetResult(new(pending.Request.Id, 409, null, "工程请求的会话、配置、权限或期限已变化；修改结果须核查")); continue; }
                return new(Now, pending.Request);
            }
            return new(Now, null, active);
        }
        finally { _mutations.Release(); }
    }
    public async Task<GatewayEngineeringAcknowledgement> EngineeringReplyAsync(GatewayCaller caller, Guid session, GatewayEngineeringReply reply, CancellationToken ct)
    {
        if (reply.Id == Guid.Empty || reply.StatusCode is < 200 or > 599 || reply.Error?.Length > 2000 || reply.Data is { } data && Encoding.UTF8.GetByteCount(data.GetRawText()) > 16 * 1024 * 1024)
            throw new GatewayRequestException(400, "工程回执参数无效");
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await SessionAsync(db, caller, session, ct);
            if (!_engineering.TryGetValue(reply.Id, out var pending)) return new(reply.Id); // Caller already cancelled/expired; never recreate a request.
            if (pending.GatewayId != caller.Id || pending.SessionId != session) throw new GatewayRequestException(403, "工程回执不属于当前网关会话");
            if (Environment.TickCount64 >= pending.Deadline || pending.Request.ExpiresTs <= Now)
            { pending.Completion.TrySetResult(new(reply.Id, 504, null, "工程回执超过期限；修改结果须核查")); return new(reply.Id); }
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pending.Request.DeviceId, ct);
            var revision = (await BuildConfigurationAsync(db, caller.Id, ct, recordRevision: false)).Revision;
            if (device?.GatewayId != caller.Id || revision != pending.Request.ConfigurationRevision)
            { pending.Completion.TrySetResult(new(reply.Id, 409, null, "工程操作期间配置或采集位置变化；修改结果须核查")); return new(reply.Id); }
            if (!await EngineeringPermissionAsync(db, pending.Request.UserId, pending.Request.Operation, ct))
                pending.Completion.TrySetResult(new(reply.Id, 403, null, "工程权限已撤销；修改结果须由管理员核查"));
            else pending.Completion.TrySetResult(reply);
            return new(reply.Id);
        }
        finally { _mutations.Release(); }
    }
    private static bool EngineeringMutation(GatewayEngineeringOperation operation) => operation is GatewayEngineeringOperation.Reconnect or GatewayEngineeringOperation.OpcTrust or GatewayEngineeringOperation.OpcUntrust;
    private static Task<bool> EngineeringPermissionAsync(Scada.Data.ConfigDbContext db, int id, GatewayEngineeringOperation operation, CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Id == id && x.Enabled && (x.Role == UserRole.Admin || operation == GatewayEngineeringOperation.Probe && x.Role == UserRole.Operator), ct);
}
