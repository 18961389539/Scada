using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.Control;
using static Scada.Server.Mcp.McpActors;
using static Scada.Server.Mcp.McpAuditLog;
using static Scada.Server.Mcp.McpText;

namespace Scada.Server.Mcp;

// 写操作以固定的 mcp:lan 身份执行：没有登录用户、没有区域限制，全部动作写入操作审计。
// 测点写入仍完整经过现有控制闭环；双人复核类对象（需要登录用户发起）一律拒绝。
[McpServerToolType]
public sealed class McpControlTools(
    IDbContextFactory<ConfigDbContext> factory,
    AlarmService alarms,
    AlarmShelfService alarmShelf,
    IOptions<ScadaSettings> settings,
    ControlCommandService commands,
    ControlWorkflowService workflow,
    RecipeService recipes,
    ICollectorDiagnostics diagnostics,
    McpIdempotency idempotency)
{
    [McpServerTool(Name = "acknowledge_alarm", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Acknowledge one alarm as mcp:lan, optionally appending a note (max 1000 characters). Acknowledging twice only appends the note and keeps the first acknowledgement. Does not clear, shelve or suppress the alarm. Returns success=false when the alarm does not exist or the note is invalid.")]
    public async Task<McpAlarmAck> AcknowledgeAlarm(int alarmId, CancellationToken cancellationToken,
        [Description("Optional note, at most 1000 characters.")] string? note = null)
    {
        if (alarmId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "alarmId must be positive.");
        note = OptionalText(note, 1000, "note");
        var alarm = await alarms.AckAsync(alarmId, McpActorName, note, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Alarm does not exist.");
        return new(alarm.Id, true, "Acknowledged.", alarm.AckUser, alarm.AckTs, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "annotate_alarm", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Append one note (1-1000 characters) to an alarm timeline as mcp:lan without changing its acknowledgement or alarm state.")]
    public async Task<McpAlarmNote> AnnotateAlarm(int alarmId, string note, CancellationToken cancellationToken)
    {
        if (alarmId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "alarmId must be positive.");
        var text = RequiredText(note, 1000, "note");
        var activity = await alarms.AddNoteAsync(alarmId, McpActorName, text, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Alarm does not exist.");
        return new(alarmId, activity.Id, activity.Ts, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "shelve_alarm_rule", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Temporarily shelve one alarm rule as mcp:lan for 1 to AlarmShelveMaxMinutes minutes with a required reason. Shelved alarms are still recorded but not announced or forwarded; the shelf expires automatically. Not available for rules that do not exist.")]
    public async Task<McpAlarmShelf> ShelveAlarmRule(int ruleId, int minutes, string reason, CancellationToken cancellationToken)
    {
        if (ruleId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "ruleId must be positive.");
        var text = RequiredText(reason, 500, "reason");
        var max = Math.Max(settings.Value.AlarmShelveMaxMinutes, 1);
        if (minutes < 1 || minutes > max)
            throw new McpToolException(McpErrorCodes.InvalidArgument, $"minutes must be between 1 and {max}.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.AlarmRules.AnyAsync(x => x.Id == ruleId, cancellationToken))
            throw new McpToolException(McpErrorCodes.NotFound, "Alarm rule does not exist.");
        var result = await alarmShelf.ShelveAsync(ruleId, minutes, text, McpActorName, max, cancellationToken);
        if (!result.Success || result.Shelve is null)
            return new(ruleId, false, result.Error ?? "Shelving failed.", null, null, null, ScadaTime.NowMs());
        return new(ruleId, true, "Shelved.", result.Shelve.Id, result.Shelve.StartTs, result.Shelve.UntilTs, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "unshelve_alarm_rule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Remove an active shelf from one alarm rule as mcp:lan. Active unacknowledged alarms that were shelved are re-announced. Returns success=false when the rule does not exist or is not shelved.")]
    public async Task<McpAlarmShelf> UnshelveAlarmRule(int ruleId, CancellationToken cancellationToken,
        [Description("Optional note recorded on affected alarms, at most 500 characters.")] string? reason = null)
    {
        if (ruleId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "ruleId must be positive.");
        var note = OptionalText(reason, 500, "reason") ?? "MCP 手动解除暂挂";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.AlarmRules.AnyAsync(x => x.Id == ruleId, cancellationToken))
            throw new McpToolException(McpErrorCodes.NotFound, "Alarm rule does not exist.");
        var ok = await alarmShelf.UnshelveAsync(ruleId, McpActorName, note, cancellationToken);
        return new(ruleId, ok, ok ? "Unshelved." : "Rule is not shelved.", null, null, null, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "check_tag_write", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Dry-run the control pipeline for one tag value as mcp:lan: local/remote mode, lockouts, writability, limits, max step, permit expression and dual approval. Returns refusals without writing anything. Call this before write_tag_value.")]
    public async Task<McpControlCheckView> CheckTagWrite(int tagId, double value, CancellationToken cancellationToken)
    {
        if (tagId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "tagId must be positive.");
        if (!double.IsFinite(value)) throw new McpToolException(McpErrorCodes.InvalidArgument, "value must be finite.");
        var check = await commands.CheckAsync(tagId, value, McpActor, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
        return new(check.TagId, check.TagName, check.Current, check.Refusals, check.RequireConfirm, check.DualApproval,
            check.LimitMin, check.LimitMax, check.MaxStep, check.Mode.ToString(), check.Lockouts, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "write_tag_value", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Write one physical, writable tag value through the existing control pipeline as mcp:lan, with read-back and audit. Local mode, lockouts, limits, max step, permit conditions and device state are enforced. Dual-approval tags are refused because MCP has no login user. success=false means no confirmed success; outcome Unknown means the command may have reached the device and must not be retried blindly.")]
    public async Task<McpControlWrite> WriteTagValue(int tagId, double value, CancellationToken cancellationToken,
        [Description("Optional idempotency ID; an empty GUID is rejected.")] Guid? requestId = null)
    {
        if (tagId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "tagId must be positive.");
        if (!double.IsFinite(value)) throw new McpToolException(McpErrorCodes.InvalidArgument, "value must be finite.");
        if (requestId == Guid.Empty) throw new McpToolException(McpErrorCodes.InvalidArgument, "requestId must not be an empty GUID.");
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            if (!await db.Tags.AnyAsync(x => x.Id == tagId, cancellationToken))
                throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
        var result = await commands.SubmitAsync(new ControlSubmission(tagId, value, McpActor, ControlOrigin.Operator,
            "MCP", Confirmed: true, RequestId: requestId), cancellationToken);
        return new(tagId, value, result.Success, result.Message, result.ReadBack, result.Outcome?.ToString(),
            result.CommandId, result.Pending, result.ApprovalId, result.Refusals ?? [], ScadaTime.NowMs());
    }

    [McpServerTool(Name = "start_control_sequence", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Start one existing control sequence as mcp:lan. Each step still runs the full control checks; dual-approval sequences are refused because MCP has no login user. Returns the run ID, which can be aborted with abort_control_sequence and inspected with the existing REST run endpoints.")]
    public Task<McpSequenceStart> StartControlSequence(int sequenceId, CancellationToken cancellationToken,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("start_control_sequence", requestId, async () =>
        {
            if (sequenceId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "sequenceId must be positive.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var sequence = await db.ControlSequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sequenceId, cancellationToken)
                ?? throw new McpToolException(McpErrorCodes.NotFound, "Control sequence does not exist.");
            if (sequence.DualApproval)
                return new McpSequenceStart(sequenceId, false, "Dual-approval sequences require a logged-in user and cannot be started through MCP.", null, ScadaTime.NowMs());
            try
            {
                var run = await workflow.StartAsync(sequence, McpActor, null, cancellationToken);
                return new McpSequenceStart(sequenceId, true, "Sequence started.", run.Id, ScadaTime.NowMs());
            }
            catch (ControlWorkflowException ex)
            {
                return new McpSequenceStart(sequenceId, false, ex.Message, null, ScadaTime.NowMs());
            }
        });

    [McpServerTool(Name = "abort_control_sequence", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Request an abort of one running control sequence as mcp:lan. The current step finishes before it stops. Returns success=false when the run is not executing.")]
    public async Task<McpSequenceAbort> AbortControlSequence(int runId, CancellationToken cancellationToken)
    {
        if (runId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "runId must be positive.");
        var ok = await workflow.AbortAsync(runId, McpActor, cancellationToken);
        return new(runId, ok, ok ? "Abort requested; the current step finishes before it stops." : "Run is not executing.", ScadaTime.NowMs());
    }

    [McpServerTool(Name = "check_recipe_download", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Dry-run a recipe download as mcp:lan: every parameter is checked against limits, lockouts, local/remote mode and its control policy. Returns refusals plus whether the recipe requires dual approval, without writing anything.")]
    public async Task<McpRecipeCheck> CheckRecipeDownload(int recipeId, CancellationToken cancellationToken,
        [Description("Recipe version; 0 uses the current version.")] int version = 0)
    {
        if (recipeId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "recipeId must be positive.");
        if (version < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "version must not be negative.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var (template, recipe, selected) = await LoadRecipeAsync(db, recipeId, version, cancellationToken);
        var check = await recipes.CheckAsync(template, recipe, selected, McpActor, cancellationToken);
        return new(check.RecipeId, check.Version, check.Ok, check.DualApproval,
            check.Parameters.Select(x => new McpRecipeParameter(x.Key, x.Name, x.TagId, x.TagName, x.Target, x.Current, x.DualApproval, x.Refusals)).ToArray(),
            check.Refusals.ToArray(), ScadaTime.NowMs());
    }

    [McpServerTool(Name = "apply_recipe", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Download one recipe version as mcp:lan through the existing control pipeline. All parameters are pre-checked; any refusal rejects the whole batch. Dual-approval recipes are refused because MCP has no login user. Returns the download ID and status; writing continues in the background.")]
    public Task<McpRecipeDownload> ApplyRecipe(int recipeId, CancellationToken cancellationToken,
        [Description("Recipe version; 0 uses the current version.")] int version = 0,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("apply_recipe", requestId, async () =>
        {
            if (recipeId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "recipeId must be positive.");
            if (version < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "version must not be negative.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var (template, recipe, selected) = await LoadRecipeAsync(db, recipeId, version, cancellationToken);
            var check = await recipes.CheckAsync(template, recipe, selected, McpActor, cancellationToken);
            if (!check.Ok)
                return new McpRecipeDownload(recipe.Id, selected.Version, false, string.Join("；", check.Refusals), null, "Rejected", null, 0, ScadaTime.NowMs());
            if (check.DualApproval)
                return new McpRecipeDownload(recipe.Id, selected.Version, false, "Dual-approval recipes require a logged-in user and cannot be downloaded through MCP.", null, null, null, 0, ScadaTime.NowMs());
            try
            {
                var download = await recipes.RequestAsync(recipeId, selected.Version, McpActor, cancellationToken);
                var ok = download.Status != RecipeDownloadStatus.Rejected;
                return new McpRecipeDownload(download.RecipeId, download.Version, ok, download.Message ?? (ok ? "Download started." : "Download rejected."),
                    download.Id, download.Status.ToString(), download.ApprovalId, download.Written, ScadaTime.NowMs());
            }
            catch (ControlWorkflowException ex)
            {
                return new McpRecipeDownload(recipe.Id, selected.Version, false, ex.Message, null, null, null, 0, ScadaTime.NowMs());
            }
        });

    [McpServerTool(Name = "set_device_control_mode", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Switch one device between Remote and Local control as mcp:lan with a required reason (max 200 characters). Local rejects all central writes, including MCP, until switched back. The change and previous mode are audited.")]
    public async Task<McpDeviceModeResult> SetDeviceControlMode(int deviceId, DeviceControlMode mode, string reason, CancellationToken cancellationToken)
    {
        if (deviceId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "deviceId must be positive.");
        if (!Enum.IsDefined(mode)) throw new McpToolException(McpErrorCodes.InvalidArgument, "mode is invalid.");
        var text = RequiredText(reason, 200, "reason");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
        var state = await db.DeviceControlStates.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        var before = state?.Mode ?? DeviceControlMode.Remote;
        var now = ScadaTime.NowMs();
        if (state is null) { state = new DeviceControlState { DeviceId = deviceId }; db.DeviceControlStates.Add(state); }
        state.Mode = mode; state.Reason = text; state.ChangedTs = now; state.ChangedBy = McpActorName;
        db.OperationLogs.Add(Audit($"切换控制权：{device.Name} → {(mode == DeviceControlMode.Local ? "就地" : "远程")}",
            "Device", deviceId, "成功", before.ToString(), $"{mode}：{text}", now));
        await db.SaveChangesAsync(cancellationToken);
        return new(deviceId, true, "Control mode changed.", mode.ToString(), before.ToString(), now, now);
    }

    [McpServerTool(Name = "place_control_lockout", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Place a NoOperate lockout, which blocks all central writes including MCP, on one device or tag as mcp:lan, with a required reason (max 200 characters). Maintenance lockouts are refused because they require a logged-in placer. Returns the new lockout ID.")]
    public Task<McpLockoutResult> PlaceControlLockout(ControlLockoutKind kind, CancellationToken cancellationToken,
        [Description("Exactly one of deviceId or tagId must be supplied.")] int? deviceId = null,
        [Description("Exactly one of deviceId or tagId must be supplied.")] int? tagId = null,
        [Description("Required reason, at most 200 characters.")] string? reason = null,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("place_control_lockout", requestId, async () =>
        {
            if ((deviceId is null) == (tagId is null))
                throw new McpToolException(McpErrorCodes.InvalidArgument, "Supply exactly one of deviceId or tagId.");
            if (deviceId is int d && d <= 0 || tagId is int t && t <= 0)
                throw new McpToolException(McpErrorCodes.InvalidArgument, "Target ID must be positive.");
            if (!Enum.IsDefined(kind)) throw new McpToolException(McpErrorCodes.InvalidArgument, "kind is invalid.");
            if (kind != ControlLockoutKind.NoOperate)
                return new McpLockoutResult(0, false, "Maintenance lockouts require a logged-in placer and cannot be placed through MCP.", kind.ToString(), deviceId, tagId, null, null, ScadaTime.NowMs());
            var text = RequiredText(reason, 200, "reason");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (deviceId is int device && !await db.Devices.AnyAsync(x => x.Id == device, cancellationToken))
                throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
            if (tagId is int tag && !await db.Tags.AnyAsync(x => x.Id == tag, cancellationToken))
                throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
            var now = ScadaTime.NowMs();
            var lockout = new ControlLockout
            {
                DeviceId = deviceId, TagId = tagId, Kind = kind, Reason = text,
                PlacedByUserId = null, PlacedBy = McpActorName, PlacedTs = now
            };
            db.ControlLockouts.Add(lockout);
            await db.SaveChangesAsync(cancellationToken);
            var target = deviceId is int dd ? $"设备 #{dd}" : $"测点 #{tagId}";
            db.OperationLogs.Add(Audit($"挂牌（禁止操作）：{target}",
                "ControlLockout", lockout.Id, "成功", null, $"NoOperate：{text}", now));
            await db.SaveChangesAsync(cancellationToken);
            return new McpLockoutResult(lockout.Id, true, "Lockout placed.", kind.ToString(), deviceId, tagId, now, null, now);
        });

    [McpServerTool(Name = "remove_control_lockout", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Remove one active lockout as mcp:lan with a required removal reason (max 200 characters). Removal is recorded; success=false when the lockout does not exist or was already removed.")]
    public async Task<McpLockoutResult> RemoveControlLockout(int lockoutId, string reason, CancellationToken cancellationToken)
    {
        if (lockoutId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "lockoutId must be positive.");
        var text = RequiredText(reason, 200, "reason");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var lockout = await db.ControlLockouts.SingleOrDefaultAsync(x => x.Id == lockoutId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Lockout does not exist.");
        var now = ScadaTime.NowMs();
        if (lockout.RemovedTs is not null)
            return new(lockout.Id, false, "Lockout was already removed.", lockout.Kind.ToString(), lockout.DeviceId, lockout.TagId, lockout.PlacedTs, lockout.RemovedTs, now);
        lockout.RemovedTs = now; lockout.RemovedBy = McpActorName; lockout.RemoveReason = text;
        db.OperationLogs.Add(Audit($"摘牌 #{lockout.Id}（挂牌人 {lockout.PlacedBy}）", "ControlLockout", lockout.Id, "成功",
            null, $"摘牌：{text}", now));
        await db.SaveChangesAsync(cancellationToken);
        return new(lockout.Id, true, "Lockout removed.", lockout.Kind.ToString(), lockout.DeviceId, lockout.TagId, lockout.PlacedTs, now, now);
    }

    [McpServerTool(Name = "reconnect_device", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Request a collector reconnect for one locally collected device as mcp:lan. The reconnect runs after the current poll and queued commands. Remote-gateway devices return unsupported because MCP does not issue gateway engineering commands. Does not change device configuration.")]
    public async Task<McpReconnectResult> ReconnectDevice(int deviceId, CancellationToken cancellationToken)
    {
        if (deviceId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "deviceId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
        if (device.GatewayId is not null)
            throw new McpToolException(McpErrorCodes.Unsupported,
                "Remote gateway devices require a logged-in operator; MCP does not issue gateway engineering commands.");
        var snapshot = diagnostics.GetDiagnostics(deviceId)
            ?? throw new McpToolException(McpErrorCodes.DataUnavailable, "No collector snapshot is available for this device.", true);
        var now = ScadaTime.NowMs();
        if (!snapshot.Enabled || snapshot.PointCount == 0)
            return new(deviceId, false, "Device is disabled or has no points to collect.", now);
        var accepted = diagnostics.RequestReconnect(deviceId);
        db.OperationLogs.Add(Audit($"请求采集重连：设备 #{deviceId} {device.Name}", "Device", deviceId,
            accepted ? "MCP 请求，等待采集引擎执行" : "MCP 请求被拒绝：设备配置已变化", null, null, now));
        await db.SaveChangesAsync(cancellationToken);
        return accepted
            ? new(deviceId, true, "Reconnect requested; it runs after the current poll and queued commands.", now)
            : new(deviceId, false, "Device configuration changed; refresh and retry.", now);
    }

    private static async Task<(RecipeTemplate Template, Recipe Recipe, RecipeVersion Version)> LoadRecipeAsync(
        ConfigDbContext db, int recipeId, int version, CancellationToken cancellationToken)
    {
        try
        {
            return await RecipeService.LoadAsync(db, recipeId, version, cancellationToken);
        }
        catch (ControlWorkflowException ex) when (ex.Status == 404)
        {
            throw new McpToolException(McpErrorCodes.NotFound, ex.Message);
        }
    }

    public sealed record McpAlarmAck(int AlarmId, bool Success, string Message, string? AckUser, long? AckTs, long DataAsOf);
    public sealed record McpAlarmNote(int AlarmId, long ActivityId, long Ts, long DataAsOf);
    public sealed record McpAlarmShelf(int RuleId, bool Success, string Message, int? ShelveId, long? StartTs, long? UntilTs, long DataAsOf);
    public sealed record McpControlCheckView(int TagId, string TagName, double? Current, IReadOnlyList<string> Refusals,
        bool RequireConfirm, bool DualApproval, double? LimitMin, double? LimitMax, double? MaxStep, string Mode,
        IReadOnlyList<string> Lockouts, long DataAsOf);
    public sealed record McpControlWrite(int TagId, double Value, bool Success, string Message, double? ReadBack, string? Outcome,
        Guid? CommandId, bool Pending, Guid? ApprovalId, IReadOnlyList<string> Refusals, long DataAsOf);
    public sealed record McpSequenceStart(int SequenceId, bool Success, string Message, int? RunId, long DataAsOf);
    public sealed record McpSequenceAbort(int RunId, bool Success, string Message, long DataAsOf);
    public sealed record McpRecipeParameter(string Key, string Name, int TagId, string TagName, double Target, double? Current,
        bool DualApproval, IReadOnlyList<string> Refusals);
    public sealed record McpRecipeCheck(int RecipeId, int Version, bool Success, bool DualApproval,
        IReadOnlyList<McpRecipeParameter> Parameters, IReadOnlyList<string> Refusals, long DataAsOf);
    public sealed record McpRecipeDownload(int RecipeId, int Version, bool Success, string Message, int? DownloadId, string? Status,
        Guid? ApprovalId, int Written, long DataAsOf);
    public sealed record McpDeviceModeResult(int DeviceId, bool Success, string Message, string Mode, string? PreviousMode,
        long ChangedTs, long DataAsOf);
    public sealed record McpLockoutResult(int LockoutId, bool Success, string Message, string Kind, int? DeviceId, int? TagId,
        long? PlacedTs, long? RemovedTs, long DataAsOf);
    public sealed record McpReconnectResult(int DeviceId, bool Success, string Message, long DataAsOf);
}
