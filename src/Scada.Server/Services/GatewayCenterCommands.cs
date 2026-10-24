using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Services;

public sealed partial class GatewayCenterService
{
    public async Task<CommandExecutionResult> ExecuteCommandAsync(int deviceId, int tagId, double value, CollectorCommandContext actor, CancellationToken ct)
    {
        var id = actor.RequestId ?? Guid.NewGuid();
        if (id == Guid.Empty || actor.UserId is null || !double.IsFinite(value)) return new(false, "命令标识、操作账户或数值无效", Outcome: GatewayCommandOutcome.NotSent);
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var existing = await db.GatewayCommands.FindAsync([id], ct);
            if (existing is not null)
            {
                if (existing.UserId != actor.UserId || existing.DeviceId != deviceId || existing.TagId != tagId || existing.Value != value)
                    return new(false, "相同命令 ID 的账户或内容不一致，未新增下发", Outcome: GatewayCommandOutcome.NotSent);
            }
            else
            {
                var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, ct);
                var tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == tagId && x.DeviceId == deviceId, ct);
                if (device?.GatewayId is not int gatewayId || tag is null || !await OperatorAsync(db, actor.UserId.Value, ct))
                    return new(false, "设备、测点或操作账户无效，命令未下发", Outcome: GatewayCommandOutcome.NotSent);
                var gateway = await db.Gateways.SingleAsync(x => x.Id == gatewayId, ct);
                var revision = (await BuildConfigurationAsync(db, gatewayId, ct, recordRevision: false)).Revision;
                var command = new GatewayControlCommand { Id = id, GatewayId = gatewayId, StreamId = gateway.ActiveStreamId ?? Guid.Empty,
                    SessionId = gateway.SessionId ?? Guid.Empty, DeviceId = deviceId, TagId = tagId, Value = value, Revision = revision,
                    CreatedTs = Now, ExpiresTs = Now + _settings.CommandTimeoutMs, UserId = actor.UserId.Value, UserName = actor.UserName };
                db.GatewayCommands.Add(command);
                if (!Ready(gateway, device, revision) || !tag.Writable || !CommandValuePolicy.TryGetRawValue(tag, value, out _, out _))
                    Finish(command, new(GatewayCommandOutcome.NotSent, "网关或设备未在线确认当前配置，命令未下发"));
                else if (await db.GatewayCommands.CountAsync(x => x.GatewayId == gatewayId && x.ResultJson == null, ct) >= 100)
                    Finish(command, new(GatewayCommandOutcome.NotSent, "此网关待处理命令已达到限额，命令未下发"));
                Audit(db, actor.UserId.Value, actor.UserName, $"受理远程命令 {id:D}：测点 #{tagId}，值 {value:R}");
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally { _mutations.Release(); }
        try
        {
            while (true)
            {
                var status = await CommandStatusAsync(id, actor.UserId, false, ct);
                if (status?.Result is { } result) return new(result.Outcome == GatewayCommandOutcome.Confirmed, result.Message, result.ReadBack, result.Outcome, id);
                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelCommandAsync(id, actor.UserId.Value);
            var status = await CommandStatusAsync(id, actor.UserId, false, CancellationToken.None);
            var result = status!.Result!; return new(result.Outcome == GatewayCommandOutcome.Confirmed, result.Message, result.ReadBack, result.Outcome, id);
        }
    }

    public async Task<GatewayCommandStatus?> CommandStatusAsync(Guid id, int? userId, bool admin, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var command = await db.GatewayCommands.FindAsync([id], ct);
            if (command is null || !admin && command.UserId != userId) return null;
            var gateway = await db.Gateways.SingleAsync(x => x.Id == command.GatewayId, ct); Expire(command, gateway);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Status(command);
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewayCommandOffers> CommandOffersAsync(GatewayCaller caller, Guid sessionId, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var gateway = await SessionAsync(db, caller, sessionId, ct);
            var pending = await db.GatewayCommands.Where(x => x.GatewayId == caller.Id && x.ResultJson == null).OrderBy(x => x.CreatedTs).ToListAsync(ct);
            GatewayCommand? offer = null;
            foreach (var command in pending)
            {
                Expire(command, gateway);
                if (command.ResultJson is not null || command.State == "Authorized" || offer is not null) continue;
                var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == command.DeviceId, ct);
                var revision = (await BuildConfigurationAsync(db, caller.Id, ct, recordRevision: false)).Revision;
                if (device is null || !Ready(gateway, device, revision) || command.Revision != revision)
                { Finish(command, new(GatewayCommandOutcome.NotSent, "设备或配置已变化，命令未下发")); continue; }
                command.State = "Reserved"; offer = Wire(command);
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(Now, offer is null ? [] : [offer]);
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewayCommandGrant> AuthorizeCommandAsync(GatewayCaller caller, Guid sessionId, Guid id, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var gateway = await SessionAsync(db, caller, sessionId, ct);
            var command = await db.GatewayCommands.SingleOrDefaultAsync(x => x.Id == id && x.GatewayId == caller.Id, ct)
                ?? throw new GatewayRequestException(404, "命令不存在");
            Expire(command, gateway);
            var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == command.DeviceId, ct);
            var tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == command.TagId && x.DeviceId == command.DeviceId, ct);
            var revision = (await BuildConfigurationAsync(db, caller.Id, ct, recordRevision: false)).Revision;
            if (command.ResultJson is not null || command.State is not ("Reserved" or "Authorized") || command.SessionId != sessionId
                || device is null || !Ready(gateway, device, revision) || revision != command.Revision || tag?.Writable != true
                || !CommandValuePolicy.TryGetRawValue(tag, command.Value, out _, out _) || !await OperatorAsync(db, command.UserId, ct))
            {
                if (command.ResultJson is null) Finish(command, new(command.State == "Authorized" ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent, "命令已失去在线、配置、权限或期限条件，不再授权执行"));
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); throw new GatewayRequestException(409, "命令不可执行", "command_not_executable");
            }
            if (command.State == "Reserved")
            {
                var token = NewToken(); command.ExecutionTokenHash = TokenHash(token); command.ProtectedExecutionToken = _credentials.Protect(token); command.State = "Authorized";
            }
            var grant = new GatewayCommandGrant(id, sessionId, revision, _credentials.Unprotect(command.ProtectedExecutionToken!), Now, command.ExpiresTs);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return grant;
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewayCommandReceiptAcknowledgement> ReceiveCommandResultAsync(GatewayCaller caller, Guid sessionId, GatewayCommandReceipt receipt, CancellationToken ct)
    {
        if (receipt.CommandId == Guid.Empty || receipt.StreamId == Guid.Empty || receipt.Result is null || !Enum.IsDefined(receipt.Result.Outcome)
            || string.IsNullOrWhiteSpace(receipt.Result.Message) || receipt.Result.Message.Length > 2000 || receipt.ExecutionToken?.Length > 256
            || receipt.Result.Outcome == GatewayCommandOutcome.Confirmed && receipt.Result.ReadBack is null
            || receipt.Result.ReadBack is double value && !double.IsFinite(value)) throw new GatewayRequestException(400, "命令回执无效");
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var gateway = await SessionAsync(db, caller, sessionId, ct);
            var command = await db.GatewayCommands.SingleOrDefaultAsync(x => x.Id == receipt.CommandId && x.GatewayId == caller.Id, ct)
                ?? throw new GatewayRequestException(404, "命令不存在");
            if (command.StreamId != receipt.StreamId || gateway.ActiveStreamId != receipt.StreamId) throw new GatewayRequestException(403, "回执不属于当前安装数据流");
            if (receipt.Result.Outcome != GatewayCommandOutcome.NotSent && (command.ExecutionTokenHash is null || receipt.ExecutionToken is null
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(command.ExecutionTokenHash), Convert.FromHexString(TokenHash(receipt.ExecutionToken)))))
                throw new GatewayRequestException(403, "命令执行授权无效");
            var hash = TokenHash(JsonSerializer.Serialize(receipt, GatewayProtocol.Json));
            if (command.ReceiptHash is not null)
            { if (command.ReceiptHash != hash) throw new GatewayRequestException(409, "已确认命令回执不能改写"); }
            else
            {
                Finish(command, receipt.Result); command.ReceiptHash = hash;
                var auditResult = $"{receipt.Result.Outcome}：{receipt.Result.Message}";
                db.OperationLogs.Add(new() { UserId = command.UserId, UserName = command.UserName, Ts = Now, Action = $"远程命令回执 {command.Id:D}", TagId = command.TagId,
                    NewValue = command.Value.ToString("R"), Result = auditResult[..Math.Min(500, auditResult.Length)] });
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(command.Id);
        }
        finally { _mutations.Release(); }
    }

    private async Task CancelCommandAsync(Guid id, int userId)
    {
        await _mutations.WaitAsync();
        try
        {
            await using var db = await OpenAsync(CancellationToken.None); await using var tx = await db.Database.BeginTransactionAsync();
            var command = await db.GatewayCommands.SingleAsync(x => x.Id == id && x.UserId == userId);
            if (command.ResultJson is null) Finish(command, new(command.State == "Authorized" ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent,
                command.State == "Authorized" ? "请求已取消，执行结果未知；请查询命令回执，不会自动重发" : "请求在执行授权前取消，命令未下发"));
            await db.SaveChangesAsync(); await tx.CommitAsync();
        }
        finally { _mutations.Release(); }
    }
    private bool Ready(AcquisitionGateway gateway, Device device, string revision) => gateway.Enabled && gateway.SessionId is not null && gateway.LeaseUntilTs > Now
        && gateway.LastSeenTs >= Now - 10000 && gateway.AppliedRevision == revision && device.GatewayId == gateway.Id && device.Enabled
        && _deviceStatuses.TryGetValue(device.Id, out var status) && status.Online && status.LastPollTs >= Now - Math.Max(3000L, device.PollIntervalMs * 3L);
    private static Task<bool> OperatorAsync(ConfigDbContext db, int id, CancellationToken ct) => db.Users.AnyAsync(x => x.Id == id && x.Enabled && (x.Role == UserRole.Admin || x.Role == UserRole.Operator), ct);
    private void Expire(GatewayControlCommand command, AcquisitionGateway gateway)
    {
        if (command.ResultJson is not null) return;
        if (command.ExpiresTs > Now && gateway.Enabled && gateway.LeaseUntilTs > Now && gateway.SessionId == command.SessionId && gateway.ActiveStreamId == command.StreamId) return;
        Finish(command, new(command.State == "Authorized" ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent,
            command.State == "Authorized" ? "执行授权已发出但未收到回执，结果未知；不会自动重发" : "命令已过期或原会话已结束，未下发"));
    }
    private void Finish(GatewayControlCommand command, GatewayCommandResult result)
    { command.State = result.Outcome == GatewayCommandOutcome.Unknown ? "Unknown" : "Completed"; command.ResultJson = JsonSerializer.Serialize(result, GatewayProtocol.Json); command.CompletedTs = Now; }
    private static GatewayCommand Wire(GatewayControlCommand command) => new(command.Id, command.DeviceId, command.TagId, command.Value, command.Revision, command.CreatedTs, command.ExpiresTs);
    private static GatewayCommandStatus Status(GatewayControlCommand command) => new(command.Id, command.GatewayId, command.DeviceId, command.TagId, command.Value, command.State,
        command.CreatedTs, command.ExpiresTs, command.ResultJson is null ? null : JsonSerializer.Deserialize<GatewayCommandResult>(command.ResultJson, GatewayProtocol.Json), command.CompletedTs);
}
