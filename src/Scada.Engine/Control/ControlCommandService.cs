using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Control;

public enum ControlOrigin { Operator, Screen, Sequence, Script }

public sealed record ControlActor(int? UserId, string UserName);

// 一次控制提交。Confirmed：操作人已在界面确认；PreApproved：已通过双人复核（复核批准或顺控启动已复核）。
public sealed record ControlSubmission(int TagId, double Value, ControlActor Actor, ControlOrigin Origin, string Source,
    bool Confirmed = false, Guid? RequestId = null, bool PreApproved = false, string? ApprovedBy = null);

public sealed record ControlSubmissionResult(bool Success, string Message, double? ReadBack = null, GatewayCommandOutcome? Outcome = null, Guid? CommandId = null,
    bool Pending = false, Guid? ApprovalId = null, IReadOnlyList<string>? Refusals = null)
{
    public static ControlSubmissionResult From(CommandExecutionResult r) => new(r.Success, r.Message, r.ReadBack, r.Outcome, r.CommandId);
    public CommandExecutionResult ToCommand() => new(Success, Message, ReadBack, Outcome, CommandId);
}

// 控制前检查结果（供界面确认框展示，也是下发前的强制检查）。
public sealed record ControlCheck(int TagId, string TagName, double? Current, IReadOnlyList<string> Refusals, bool RequireConfirm, bool DualApproval,
    double? LimitMin, double? LimitMax, double? MaxStep, string? Feedback, DeviceControlMode Mode, IReadOnlyList<string> Lockouts);

// 现场控制闭环：检查（就地/远程、挂牌、限幅、变化率、许可互锁）→ 确认 → 双人复核 → 下发及同点回读 → 独立反馈等待 → 审计。
// 操作员接口、运行画面、顺控和计算脚本的写入都走这里。
public sealed class ControlCommandService
{
    private readonly IConfigSnapshot _config;
    private readonly ICollectorControl _collector;
    private readonly IRealtimeCache _cache;
    private readonly IDbContextFactory<ConfigDbContext> _contextFactory;
    private readonly ILogger<ControlCommandService> _logger;
    public static int FeedbackPollMs { get; set; } = 100;
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly bool _confirmByDefault;
    private readonly INodeRole _role;

    public ControlCommandService(IConfigSnapshot config, ICollectorControl collector, IRealtimeCache cache, IDbContextFactory<ConfigDbContext> contextFactory, ILogger<ControlCommandService> logger, IOptions<ScadaSettings>? options = null, INodeRole? role = null)
    {
        _role = role ?? new StandaloneNodeRole();
        _confirmByDefault = options?.Value.ControlRequireConfirmByDefault ?? false;
        _config = config; _collector = collector; _cache = cache; _contextFactory = contextFactory; _logger = logger;
    }

    // 计算脚本等自动化写入：不需要界面确认，但同样受就地/远程、挂牌、限幅、互锁约束；需双人复核的测点拒绝自动写入。
    public async Task<CommandExecutionResult> ExecuteAsync(int tagId, double value, int? userId, string userName, CancellationToken cancellationToken = default, Guid? requestId = null) =>
        (await SubmitAsync(new ControlSubmission(tagId, value, new(userId, userName), ControlOrigin.Script, "计算脚本", true, requestId), cancellationToken)).ToCommand();

    public async Task<ControlCheck?> CheckAsync(int tagId, double value, ControlActor actor, CancellationToken ct)
    {
        if (!_config.TryGetTag(tagId, out var tag)) return null;
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var point = await db.ControlPoints.AsNoTracking().SingleOrDefaultAsync(x => x.TagId == tagId, ct);
        var refusals = new List<string>();
        if (!_role.IsActive) refusals.Add(_role.StandbyMessage);
        var mode = DeviceControlMode.Remote; var lockoutTexts = new List<string>();
        var current = _cache.Get(tag.Id) is { Quality: TagQuality.Good } v ? v.Value : (double?)null;
        if (tag.Kind != TagKind.Physical) refusals.Add("计算测点只读，不能下发现场控制");
        else if (tag.DeviceId is not int deviceId || !_config.TryGetDevice(deviceId, out var device) || !device.Enabled) refusals.Add("设备不存在或未启用");
        else
        {
            if (!tag.Writable) refusals.Add("测点不可写");
            mode = (await db.DeviceControlStates.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId, ct))?.Mode ?? DeviceControlMode.Remote;
            if (mode == DeviceControlMode.Local) refusals.Add($"设备 {device.Name} 处于就地控制，中心不能下发");
            var lockouts = await db.ControlLockouts.AsNoTracking().Where(x => x.RemovedTs == null && (x.DeviceId == deviceId || x.TagId == tag.Id)).ToListAsync(ct);
            foreach (var l in lockouts)
            {
                var label = $"{(l.Kind == ControlLockoutKind.NoOperate ? "禁止操作" : "检修中")}（{l.PlacedBy}：{l.Reason}）";
                lockoutTexts.Add(label);
                if (l.Kind == ControlLockoutKind.NoOperate) refusals.Add($"已挂牌{label}");
                else if (actor.UserId is null || l.PlacedByUserId != actor.UserId) refusals.Add($"已挂牌{label}，仅挂牌人可操作");
            }
        }
        if (!CommandValuePolicy.TryGetRawValue(tag, value, out _, out var error)) refusals.Add(error);
        if (point is not null)
        {
            if (point.LimitMin is double min && value < min || point.LimitMax is double max && value > max)
                refusals.Add($"超出操作限幅 [{Fmt(point.LimitMin)}, {Fmt(point.LimitMax)}]");
            if (point.MaxStep is double step)
            {
                if (current is not double now) refusals.Add("当前值无效，无法校验变化率");
                else if (Math.Abs(value - now) > step + 1e-9) refusals.Add($"单次变化 {Fmt(Math.Abs(value - now))} 超过限制 {Fmt(step)}（当前 {Fmt(now)}）");
            }
            if (!string.IsNullOrWhiteSpace(point.PermitExpression) && Evaluate(point.PermitExpression) is var (ok, why) && !ok)
                refusals.Add(string.IsNullOrWhiteSpace(point.PermitMessage) ? $"许可条件不满足：{why}" : $"{point.PermitMessage}（{why}）");
        }
        return new ControlCheck(tag.Id, tag.Name, current, refusals, point?.RequireConfirm ?? _confirmByDefault, point?.DualApproval ?? false,
            point?.LimitMin, point?.LimitMax, point?.MaxStep, point?.FeedbackExpression, mode, lockoutTexts);
    }

    public async Task<ControlSubmissionResult> SubmitAsync(ControlSubmission s, CancellationToken ct)
    {
        if (!_config.TryGetTag(s.TagId, out var tag)) return new(false, "测点不存在", Outcome: GatewayCommandOutcome.NotSent);
        var check = (await CheckAsync(s.TagId, s.Value, s.Actor, ct))!;
        var refusals = check.Refusals.ToList();
        var interactive = s.Origin is ControlOrigin.Operator or ControlOrigin.Screen;
        if (refusals.Count == 0 && check.RequireConfirm && interactive && !s.Confirmed) refusals.Add("该测点要求操作确认，请在确认框中确认后再下发");
        if (refusals.Count == 0 && check.DualApproval && !s.PreApproved && !interactive) refusals.Add("该测点要求双人复核，不能由自动化或未复核的顺控直接写入");
        if (refusals.Count > 0)
        {
            var message = string.Join("；", refusals);
            await AuditAsync(tag, s, "控制被拒绝", "拒绝：" + message, null, ct);
            return new(false, message, Outcome: GatewayCommandOutcome.NotSent, Refusals: refusals);
        }
        if (check.DualApproval && !s.PreApproved)
        {
            if (s.Actor.UserId is not int requester) return new(false, "双人复核需要登录用户发起", Outcome: GatewayCommandOutcome.NotSent);
            await using var db = await _contextFactory.CreateDbContextAsync(ct);
            var timeout = (await db.ControlPoints.AsNoTracking().SingleAsync(x => x.TagId == tag.Id, ct)).ApprovalTimeoutSec;
            var request = new ControlRequest
            {
                Id = s.RequestId ?? Guid.NewGuid(), Kind = "command", TagId = tag.Id, Value = s.Value, Source = s.Source, RequestedByUserId = requester, RequestedBy = s.Actor.UserName,
                RequestedTs = ScadaTime.NowMs(), ExpiresTs = ScadaTime.NowMs() + Math.Clamp(timeout, 30, 3600) * 1000L, Status = ControlRequestStatus.Pending
            };
            if (await db.ControlRequests.AnyAsync(x => x.Id == request.Id, ct)) return new(false, "该命令编号已提交复核", Outcome: GatewayCommandOutcome.NotSent, Pending: true, ApprovalId: request.Id);
            db.ControlRequests.Add(request);
            db.OperationLogs.Add(Log(tag, s, "发起控制（待双人复核）", $"待复核，编号 {request.Id}", null));
            await db.SaveChangesAsync(ct);
            return new(false, "已提交，等待另一位有权限的用户复核", Outcome: GatewayCommandOutcome.NotSent, Pending: true, ApprovalId: request.Id);
        }

        var device = _config.TryGetDevice(tag.DeviceId!.Value, out var d) ? d : throw new InvalidOperationException();
        var oldValue = _cache.Get(tag.Id)?.Value;
        var result = ControlSubmissionResult.From(await _collector.WriteIdentifiedAsync(device.Id, tag, s.Value, new(s.RequestId, s.Actor.UserId, s.Actor.UserName), ct));
        if (result.Success && !string.IsNullOrWhiteSpace(check.Feedback))
        {
            var (ok, why) = await WaitAsync(check.Feedback!, TimeSpan.FromMilliseconds(await FeedbackTimeoutAsync(tag.Id, ct)), CancellationToken.None);
            if (!ok) result = result with { Success = false, Outcome = GatewayCommandOutcome.Unknown, Message = $"写入及回读成功，但独立反馈超时未到位：{why}" };
            else result = result with { Message = result.Message + "，反馈到位" };
        }
        var text = result.Outcome == GatewayCommandOutcome.Unknown ? $"结果未知：{result.Message}"
            : result.Outcome == GatewayCommandOutcome.NotSent ? $"未下发：{result.Message}"
            : result.Success ? $"成功，回读 {result.ReadBack:0.###}{(check.Feedback is null ? "" : "，反馈到位")}" : $"失败：{result.Message}";
        await AuditAsync(tag, s, "控制写入", text, oldValue, CancellationToken.None);
        return result;
    }

    private async Task<int> FeedbackTimeoutAsync(int tagId, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        return Math.Clamp((await db.ControlPoints.AsNoTracking().SingleAsync(x => x.TagId == tagId, ct)).FeedbackTimeoutMs, 100, 600_000);
    }

    // 等待条件为真，返回是否满足及最后一次不满足的原因。
    public async Task<(bool Ok, string Why)> WaitAsync(string expression, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var (ok, why) = Evaluate(expression);
            if (ok) return (true, "");
            if (DateTime.UtcNow >= deadline) return (false, $"{why}，等待 {timeout.TotalMilliseconds:0} 毫秒超时");
            await Task.Delay(FeedbackPollMs, ct);
        }
    }

    // 用实时值计算条件表达式；引用测点无有效值视为不满足。
    public (bool Ok, string Why) Evaluate(string expression)
    {
        string? missing = null;
        try
        {
            var compiled = TagExpression.Compile(expression);
            var value = compiled.Evaluate(id =>
            {
                if (_cache.Get(id) is { Quality: TagQuality.Good } v) return v.Value;
                missing ??= $"条件引用的测点 #{id} 无有效值"; return double.NaN;
            });
            return value != 0 ? (true, "") : (false, $"{expression} 不成立");
        }
        catch (TagExpressionException ex) { return (false, missing ?? ex.Message); }
    }

    public static string? ValidateExpression(string? expression, Func<int, bool> tagExists)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        try
        {
            var compiled = TagExpression.Compile(expression);
            var missing = compiled.Dependencies.FirstOrDefault(x => !tagExists(x));
            return missing != 0 ? $"条件引用的测点 #{missing} 不存在" : null;
        }
        catch (TagExpressionException ex) { return ex.Message; }
    }

    private static string Fmt(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-";

    private OperationLog Log(Tag tag, ControlSubmission s, string action, string result, double? oldValue) => new()
    {
        UserId = s.Actor.UserId, UserName = s.Actor.UserName, Ts = ScadaTime.NowMs(), Action = $"{action} {tag.Name}", TagId = tag.Id,
        OldValue = oldValue?.ToString("0.###", CultureInfo.InvariantCulture), NewValue = s.Value.ToString("0.###", CultureInfo.InvariantCulture),
        Result = result.Length > 1000 ? result[..1000] : result, TargetType = "Control", TargetId = tag.Id.ToString(CultureInfo.InvariantCulture),
        Details = JsonSerializer.Serialize(new { origin = s.Origin.ToString(), source = s.Source, confirmed = s.Confirmed, approvedBy = s.ApprovedBy, commandId = s.RequestId }, AuditJson)
    };

    private async Task AuditAsync(Tag tag, ControlSubmission s, string action, string result, double? oldValue, CancellationToken ct)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(Log(tag, s, action, result, oldValue));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "写操作日志失败"); }
    }
}
