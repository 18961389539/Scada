using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;

namespace Scada.Server.Services;

/// <summary>
/// 控制挂牌的唯一落地点。REST 与 MCP 共用目标存在性、原因长度、落库与审计。
/// 摘牌与检修牌策略通过 <see cref="ControlLockoutPolicy"/> 注入，不把网页代摘规则与 MCP 自摘规则合并。
/// 区域权限由各入口负责：REST 在调用前检查（不存在的目标对非全局用户仍是 403）；
/// MCP 在存在性检查之后、落库之前由本服务调用入口传入的授权回调（先报 NotFound）。
///
/// 原因规则（挂牌与摘牌相同）：去空白后须为 1～200 个字符，超长拒绝而不是截断。
/// 此前 REST 摘牌会把超长说明截到 200 字，MCP 则拒绝；现已统一为拒绝，以免审计记录被静默截断。
/// 落库只写 <see cref="RuntimeDbContext"/>，不再对无变更的配置库调用 SaveChanges。
/// </summary>
public enum ControlLockoutCommandError
{
    None,
    InvalidTarget,
    InvalidKind,
    InvalidReason,
    TargetNotFound,
    LockoutNotFound,
    AlreadyRemoved,
    MaintenanceNotAllowed,
    Forbidden,
    PolicyDenied
}

public sealed class ControlLockoutCommandResult
{
    public bool Success { get; init; }
    public ControlLockoutCommandError Error { get; init; }
    public string? Message { get; init; }
    public ControlLockout? Lockout { get; init; }

    public static ControlLockoutCommandResult Failed(ControlLockoutCommandError error, string? message, ControlLockout? lockout = null) =>
        new() { Error = error, Message = message, Lockout = lockout };

    public static ControlLockoutCommandResult Ok(ControlLockout lockout) =>
        new() { Success = true, Lockout = lockout };
}

public sealed record ControlLockoutActor(int? UserId, string Name);

public enum ControlLockoutRemoveRule
{
    /// <summary>网页：挂牌人（需控制权）或该区域配置管理员可摘。</summary>
    OwnerOrAreaAdmin,
    /// <summary>MCP：只能摘本入口挂的禁止操作牌（PlacedBy 与 UserId 匹配，且种类为 NoOperate）。</summary>
    OwnProhibitOnly
}

/// <summary>
/// 入口策略。REST 与 MCP 的产品差异留在这里，服务内部只有一套落库与审计路径。
/// </summary>
public sealed class ControlLockoutPolicy
{
    public bool AllowMaintenance { get; init; }
    public ControlLockoutRemoveRule RemoveRule { get; init; }

    /// <summary>
    /// 存在性检查之后、落库之前的区域授权。返回 false 时给出 <see cref="ControlLockoutCommandError.Forbidden"/>，
    /// 不写拒绝审计（由入口决定是否记录）。REST 通常已在调用前检查，可省略。
    /// </summary>
    public Func<int?, int?, CancellationToken, Task<bool>>? AuthorizeAfterExistenceAsync { get; set; }

    public Func<int?, int?, bool>? CanControl { get; set; }
    public Func<int?, int?, bool>? CanConfigure { get; set; }

    public static ControlLockoutPolicy Rest() => new()
    {
        AllowMaintenance = true,
        RemoveRule = ControlLockoutRemoveRule.OwnerOrAreaAdmin
    };

    public static ControlLockoutPolicy Mcp() => new()
    {
        AllowMaintenance = false,
        RemoveRule = ControlLockoutRemoveRule.OwnProhibitOnly
    };
}

public sealed class ControlLockoutCommandService
{
    public const int ReasonMaxLength = 200;
    public const string InvalidTargetMessage = "请指定设备或测点之一";
    public const string PlaceReasonMessage = "请填写挂牌原因（最多 200 字）";
    public const string RemoveReasonRequiredMessage = "请填写摘牌说明";
    public const string RemoveReasonLengthMessage = "请填写摘牌说明（最多 200 字）";
    public const string AlreadyRemovedMessage = "已摘牌";
    public const string OwnerOrAdminMessage = "只有挂牌人或该区域的配置管理员可以摘牌";
    public const string OwnProhibitOnlyMessage = "只能摘除 MCP 自己挂的禁止操作牌。";
    public const string MaintenanceNotAllowedMessage = "Maintenance lockouts require a logged-in placer and cannot be placed through MCP.";

    public async Task<ControlLockoutCommandResult> PlaceAsync(
        ConfigDbContext db, RuntimeDbContext runtime,
        int? deviceId, int? tagId, ControlLockoutKind kind, string? reason,
        ControlLockoutActor actor, ControlLockoutPolicy policy,
        CancellationToken cancellationToken)
    {
        if ((deviceId is null) == (tagId is null))
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.InvalidTarget, InvalidTargetMessage);
        if (!Enum.IsDefined(kind))
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.InvalidKind, InvalidKindMessage(kind));
        if (kind == ControlLockoutKind.Maintenance && !policy.AllowMaintenance)
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.MaintenanceNotAllowed, MaintenanceNotAllowedMessage);
        var text = NormalizeReason(reason, placing: true, out var invalid);
        if (invalid is not null) return invalid;

        if (deviceId is int device && !await db.Devices.AnyAsync(x => x.Id == device, cancellationToken))
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.TargetNotFound, "Device does not exist.");
        if (tagId is int tag && !await db.Tags.AnyAsync(x => x.Id == tag, cancellationToken))
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.TargetNotFound, "Tag does not exist.");

        if (policy.AuthorizeAfterExistenceAsync is { } authorize && !await authorize(deviceId, tagId, cancellationToken))
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.Forbidden, null);

        var now = ScadaTime.NowMs();
        var lockout = new ControlLockout
        {
            DeviceId = deviceId, TagId = tagId, Kind = kind, Reason = text!,
            PlacedByUserId = actor.UserId, PlacedBy = actor.Name, PlacedTs = now
        };
        runtime.ControlLockouts.Add(lockout);
        await runtime.SaveChangesAsync(cancellationToken);
        AuditPlace(runtime, lockout, actor, now);
        await runtime.SaveChangesAsync(cancellationToken);
        return ControlLockoutCommandResult.Ok(lockout);
    }

    public async Task<ControlLockoutCommandResult> RemoveAsync(
        RuntimeDbContext runtime, int lockoutId, string? reason,
        ControlLockoutActor actor, ControlLockoutPolicy policy,
        CancellationToken cancellationToken)
    {
        var text = NormalizeReason(reason, placing: false, out var invalid);
        if (invalid is not null) return invalid;

        var lockout = await runtime.ControlLockouts.SingleOrDefaultAsync(x => x.Id == lockoutId, cancellationToken);
        if (lockout is null)
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.LockoutNotFound, "Lockout does not exist.");
        if (lockout.RemovedTs is not null)
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.AlreadyRemoved, AlreadyRemovedMessage, lockout);

        if (!CanRemove(lockout, actor, policy, out var denied))
            return denied!;
        if (policy.AuthorizeAfterExistenceAsync is { } authorize && !await authorize(lockout.DeviceId, lockout.TagId, cancellationToken))
            return ControlLockoutCommandResult.Failed(ControlLockoutCommandError.Forbidden, null, lockout);

        var before = new { lockout.RemovedTs, lockout.RemovedBy };
        var now = ScadaTime.NowMs();
        lockout.RemovedTs = now;
        lockout.RemovedBy = actor.Name;
        lockout.RemoveReason = text;
        AuditRemove(runtime, lockout, actor, before, text!, now);
        await runtime.SaveChangesAsync(cancellationToken);
        return ControlLockoutCommandResult.Ok(lockout);
    }

    public static string InvalidKindMessage(ControlLockoutKind kind) =>
        $"挂牌类型无效：{kind}（可选 {string.Join("/", Enum.GetNames<ControlLockoutKind>())}）";

    private static string? NormalizeReason(string? reason, bool placing, out ControlLockoutCommandResult? error)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = ControlLockoutCommandResult.Failed(ControlLockoutCommandError.InvalidReason,
                placing ? PlaceReasonMessage : RemoveReasonRequiredMessage);
            return null;
        }
        if (text.Length > ReasonMaxLength)
        {
            error = ControlLockoutCommandResult.Failed(ControlLockoutCommandError.InvalidReason,
                placing ? PlaceReasonMessage : RemoveReasonLengthMessage);
            return null;
        }
        error = null;
        return text;
    }

    private static bool CanRemove(
        ControlLockout lockout, ControlLockoutActor actor, ControlLockoutPolicy policy,
        out ControlLockoutCommandResult? denied)
    {
        switch (policy.RemoveRule)
        {
            case ControlLockoutRemoveRule.OwnProhibitOnly:
                if (lockout.PlacedBy != actor.Name || lockout.PlacedByUserId != actor.UserId || lockout.Kind != ControlLockoutKind.NoOperate)
                {
                    denied = ControlLockoutCommandResult.Failed(ControlLockoutCommandError.PolicyDenied, OwnProhibitOnlyMessage, lockout);
                    return false;
                }
                denied = null;
                return true;
            case ControlLockoutRemoveRule.OwnerOrAreaAdmin:
                var control = policy.CanControl?.Invoke(lockout.DeviceId, lockout.TagId) ?? false;
                var configure = policy.CanConfigure?.Invoke(lockout.DeviceId, lockout.TagId) ?? false;
                if (!(control && lockout.PlacedByUserId == actor.UserId) && !configure)
                {
                    denied = ControlLockoutCommandResult.Failed(ControlLockoutCommandError.Forbidden, OwnerOrAdminMessage, lockout);
                    return false;
                }
                denied = null;
                return true;
            default:
                denied = ControlLockoutCommandResult.Failed(ControlLockoutCommandError.PolicyDenied, OwnerOrAdminMessage, lockout);
                return false;
        }
    }

    private static void AuditPlace(RuntimeDbContext runtime, ControlLockout lockout, ControlLockoutActor actor, long ts)
    {
        var target = lockout.DeviceId is int device ? $"设备 #{device}" : $"测点 #{lockout.TagId}";
        var kindName = lockout.Kind == ControlLockoutKind.NoOperate ? "禁止操作" : "检修中";
        runtime.OperationLogs.Add(new OperationLog
        {
            UserId = actor.UserId,
            UserName = actor.Name,
            Ts = ts,
            Action = $"挂牌（{kindName}）：{target}",
            Result = "成功",
            TargetType = "ControlLockout",
            TargetId = lockout.Id.ToString(),
            Details = AuditDiff.Compute(null, lockout),
            NewValue = $"{lockout.Kind}：{lockout.Reason}"
        });
    }

    private static void AuditRemove(
        RuntimeDbContext runtime, ControlLockout lockout, ControlLockoutActor actor,
        object before, string reason, long ts)
    {
        runtime.OperationLogs.Add(new OperationLog
        {
            UserId = actor.UserId,
            UserName = actor.Name,
            Ts = ts,
            Action = $"摘牌 #{lockout.Id}（挂牌人 {lockout.PlacedBy}）",
            Result = "成功",
            TargetType = "ControlLockout",
            TargetId = lockout.Id.ToString(),
            Details = AuditDiff.Compute(before, new { lockout.RemovedTs, lockout.RemovedBy, lockout.RemoveReason }),
            NewValue = $"摘牌：{reason}"
        });
    }
}
