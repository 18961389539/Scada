using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Control;

public sealed class ControlWorkflowException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }

// 双人复核与顺控执行。顺控在后台逐步执行：每步走完整控制检查与下发，再在超时内等待本步完成条件；任一步失败即停止，其余步骤记为跳过。
public sealed class ControlWorkflowService(IDbContextFactory<ConfigDbContext> factory, ControlCommandService commands, RecipeService recipes, ILogger<ControlWorkflowService> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private readonly CancellationTokenSource _shutdown = new();

    // 宿主正常停止：打断正在等待的步骤，把运行记为中断后再退出（不留下 Running 状态）。
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        try { await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); } catch (Exception ex) { logger.LogWarning(ex, "等待顺控结束超时"); }
        await base.StopAsync(cancellationToken);
    }

    // 启动时把上次进程退出时仍在运行的顺控标记为中断（不自动续跑，避免重启后对现场重复动作）。
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(stoppingToken);
            var now = ScadaTime.NowMs();
            foreach (var run in await db.ControlSequenceRuns.Where(x => x.Status == ControlRunStatus.Running).ToListAsync(stoppingToken))
            {
                if (_running.ContainsKey(run.Id)) continue;
                run.Status = ControlRunStatus.Failed; run.EndedTs = now; run.Message = "服务重启，顺控中断；请核对现场状态后重新执行";
                db.OperationLogs.Add(Audit(run.StartedByUserId, "system", $"顺控中断 {run.SequenceName}", run.Message, run.Id));
            }
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "恢复顺控运行状态失败"); }
    }

    public static IReadOnlyList<ControlSequenceStep> Steps(ControlSequence sequence) => JsonSerializer.Deserialize<List<ControlSequenceStep>>(sequence.StepsJson, Json) ?? [];

    public async Task ExpireAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = ScadaTime.NowMs();
        var expired = await db.ControlRequests.Where(x => x.Status == ControlRequestStatus.Pending && x.ExpiresTs < now).ToListAsync(ct);
        foreach (var r in expired) { r.Status = ControlRequestStatus.Expired; r.DecidedTs = now; db.OperationLogs.Add(Audit(r.RequestedByUserId, r.RequestedBy, "控制复核超时", $"请求 {r.Id} 超时未复核", null, r.TagId)); }
        await db.SaveChangesAsync(ct);
        foreach (var r in expired.Where(x => x.RecipeDownloadId is not null))
            await recipes.DecidedAsync(r.RecipeDownloadId!.Value, false, new(r.RequestedByUserId, r.RequestedBy), null, "复核超时", ct);
    }

    public async Task<ControlRequest> RequestSequenceAsync(ControlSequence sequence, ControlActor actor, CancellationToken ct)
    {
        if (actor.UserId is not int user) throw new ControlWorkflowException("需要登录用户发起");
        await using var db = await factory.CreateDbContextAsync(ct);
        var request = new ControlRequest
        {
            Id = Guid.NewGuid(), Kind = "sequence", SequenceId = sequence.Id, Source = $"顺控 {sequence.Name}", RequestedByUserId = user, RequestedBy = actor.UserName,
            RequestedTs = ScadaTime.NowMs(), ExpiresTs = ScadaTime.NowMs() + 300_000, Status = ControlRequestStatus.Pending
        };
        db.ControlRequests.Add(request);
        db.OperationLogs.Add(Audit(user, actor.UserName, $"申请执行顺控 {sequence.Name}（待双人复核）", $"待复核，编号 {request.Id}", null));
        await db.SaveChangesAsync(ct);
        return request;
    }

    // 复核：批准人必须不同于申请人（权限由调用方按区域检查）；批准时重新做全部控制检查。
    public async Task<(ControlRequest Request, ControlSubmissionResult? Result, ControlSequenceRun? Run)> DecideAsync(Guid id, bool approve, ControlActor approver, string? comment, CancellationToken ct)
    {
        await ExpireAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var request = await db.ControlRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ControlWorkflowException("复核请求不存在", 404);
        if (request.Status != ControlRequestStatus.Pending) throw new ControlWorkflowException($"请求已处理（{request.Status}）", 409);
        var self = approver.UserId == request.RequestedByUserId;
        if (approve && self) throw new ControlWorkflowException("双人复核须由另一位用户批准", 403);
        request.DecidedByUserId = approver.UserId; request.DecidedBy = approver.UserName; request.DecidedTs = ScadaTime.NowMs(); request.Comment = comment?.Trim();
        if (!approve)
        {
            request.Status = self ? ControlRequestStatus.Cancelled : ControlRequestStatus.Rejected;
            db.OperationLogs.Add(Audit(approver.UserId, approver.UserName, self ? "撤回控制申请" : "驳回控制申请", $"请求 {request.Id}（申请人 {request.RequestedBy}）{comment}", null, request.TagId));
            await db.SaveChangesAsync(ct);
            if (request.RecipeDownloadId is int rejected)
                await recipes.DecidedAsync(rejected, false, new(request.RequestedByUserId, request.RequestedBy), null, self ? "申请人撤回" : $"复核人 {approver.UserName} 驳回", ct);
            return (request, null, null);
        }
        // 先占住状态，避免两人同时批准导致重复执行。
        request.Status = ControlRequestStatus.Executed;
        db.OperationLogs.Add(Audit(approver.UserId, approver.UserName, "批准控制申请", $"请求 {request.Id}（申请人 {request.RequestedBy}）{comment}", null, request.TagId));
        await db.SaveChangesAsync(ct);
        if (request.Kind == "recipe" && request.RecipeDownloadId is int downloadId)
        {
            try
            {
                await recipes.DecidedAsync(downloadId, true, new(request.RequestedByUserId, request.RequestedBy), approver.UserName, "", ct);
                request.ResultMessage = $"已开始下发 #{downloadId}";
            }
            catch (Exception ex) { request.Status = ControlRequestStatus.Failed; request.ResultMessage = Trim(ex.Message); }
            await db.SaveChangesAsync(CancellationToken.None);
            return (request, null, null);
        }
        if (request.Kind == "sequence")
        {
            var sequence = await db.ControlSequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SequenceId, ct);
            if (sequence is null) { request.Status = ControlRequestStatus.Failed; request.ResultMessage = "顺控已删除"; await db.SaveChangesAsync(ct); return (request, null, null); }
            var run = await StartAsync(sequence, new(request.RequestedByUserId, request.RequestedBy), approver.UserName, ct);
            request.RunId = run.Id; request.ResultMessage = $"已启动运行 #{run.Id}"; await db.SaveChangesAsync(ct);
            return (request, null, run);
        }
        var result = await commands.SubmitAsync(new ControlSubmission(request.TagId!.Value, request.Value!.Value, new(request.RequestedByUserId, request.RequestedBy), ControlOrigin.Operator,
            request.Source, Confirmed: true, RequestId: request.Id, PreApproved: true, ApprovedBy: approver.UserName), ct);
        request.Status = result.Success ? ControlRequestStatus.Executed : ControlRequestStatus.Failed; request.ResultMessage = Trim(result.Message);
        await db.SaveChangesAsync(CancellationToken.None);
        return (request, result, null);
    }

    public async Task<ControlSequenceRun> StartAsync(ControlSequence sequence, ControlActor actor, string? approvedBy, CancellationToken ct)
    {
        var steps = Steps(sequence);
        if (steps.Count == 0) throw new ControlWorkflowException("顺控没有步骤");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.ControlSequenceRuns.AnyAsync(x => x.SequenceId == sequence.Id && x.Status == ControlRunStatus.Running, ct)) throw new ControlWorkflowException("该顺控正在运行", 409);
        var run = new ControlSequenceRun
        {
            SequenceId = sequence.Id, SequenceName = sequence.Name, AreaId = sequence.AreaId, Status = ControlRunStatus.Running, StartedTs = ScadaTime.NowMs(),
            StartedByUserId = actor.UserId, StartedBy = actor.UserName, ApprovedBy = approvedBy,
            StepsJson = JsonSerializer.Serialize(steps.Select((x, i) => new ControlStepResult { Index = i + 1, Name = x.Name, TagId = x.TagId, Value = x.Value }).ToList(), Json)
        };
        db.ControlSequenceRuns.Add(run);
        await db.SaveChangesAsync(ct);
        db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"启动顺控 {sequence.Name}", approvedBy is null ? $"运行 #{run.Id}" : $"运行 #{run.Id}，复核人 {approvedBy}", run.Id));
        await db.SaveChangesAsync(ct);
        var cts = new CancellationTokenSource(); _running[run.Id] = cts;
        _tasks[run.Id] = Task.Run(() => RunAsync(run.Id, sequence, steps, actor, approvedBy, cts.Token));
        return run;
    }

    public async Task<bool> AbortAsync(int runId, ControlActor actor, CancellationToken ct)
    {
        if (!_running.TryGetValue(runId, out var cts)) return false;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"中止顺控运行 #{runId}", "已请求中止（当前步骤结束后停止）", runId));
            await db.SaveChangesAsync(ct);
        }
        cts.Cancel();
        return true;
    }

    // 测试用：等待某次运行结束。
    public Task WaitAsync(int runId) => _tasks.TryGetValue(runId, out var task) ? task : Task.CompletedTask;

    private async Task RunAsync(int runId, ControlSequence sequence, IReadOnlyList<ControlSequenceStep> steps, ControlActor actor, string? approvedBy, CancellationToken ct)
    {
        var results = steps.Select((x, i) => new ControlStepResult { Index = i + 1, Name = x.Name, TagId = x.TagId, Value = x.Value }).ToList();
        var status = ControlRunStatus.Completed; string? message = null;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (_shutdown.IsCancellationRequested) { status = ControlRunStatus.Failed; message = $"服务停止，顺控在第 {i + 1} 步之前中断；请核对现场状态后重新执行"; break; }
                var step = steps[i]; var r = results[i];
                if (ct.IsCancellationRequested) { status = ControlRunStatus.Aborted; message = $"在第 {i + 1} 步之前中止"; break; }
                r.Status = "running"; r.StartedTs = ScadaTime.NowMs(); await SaveAsync(runId, results, null, null);
                var result = await commands.SubmitAsync(new ControlSubmission(step.TagId, step.Value, actor, ControlOrigin.Sequence, $"顺控 {sequence.Name} 第 {i + 1} 步 {step.Name}",
                    Confirmed: true, PreApproved: approvedBy is not null, ApprovedBy: approvedBy), CancellationToken.None);
                if (!result.Success) { r.Status = "failed"; r.Message = Trim(result.Message); status = ControlRunStatus.Failed; message = $"第 {i + 1} 步“{step.Name}”失败：{result.Message}"; }
                else
                {
                    if (step.DelayMs > 0) await Task.Delay(Math.Min(step.DelayMs, 600_000), _shutdown.Token);
                    if (!string.IsNullOrWhiteSpace(step.ConfirmExpression))
                    {
                        var (ok, why) = await commands.WaitAsync(step.ConfirmExpression, TimeSpan.FromMilliseconds(Math.Clamp(step.TimeoutMs, 100, 600_000)), _shutdown.Token);
                        if (!ok) { r.Status = "failed"; r.Message = Trim($"完成条件未满足：{why}"); status = ControlRunStatus.Failed; message = $"第 {i + 1} 步“{step.Name}”完成条件未满足：{why}"; }
                    }
                    if (r.Status != "failed") { r.Status = "done"; r.Message = result.Message; }
                }
                r.EndedTs = ScadaTime.NowMs();
                if (status != ControlRunStatus.Completed) break;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            status = ControlRunStatus.Failed; message = "服务停止，顺控中断；请核对现场状态后重新执行";
            foreach (var r in results.Where(x => x.Status == "running")) { r.Status = "failed"; r.Message = "服务停止，中断"; r.EndedTs = ScadaTime.NowMs(); }
        }
        catch (Exception ex) { status = ControlRunStatus.Failed; message = Trim("顺控执行异常：" + ex.Message); logger.LogError(ex, "顺控 {Name} 执行异常", sequence.Name); }
        foreach (var r in results.Where(x => x.Status is "pending")) r.Status = "skipped";
        try
        {
            await SaveAsync(runId, results, status, message);
            await using var db = await factory.CreateDbContextAsync();
            db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"顺控结束 {sequence.Name}", status switch
            {
                ControlRunStatus.Completed => "完成", ControlRunStatus.Aborted => "中止：" + message, _ => (message?.Contains("中断") == true ? "中断：" : "失败：") + message
            }, runId));
            await db.SaveChangesAsync();
        }
        finally { _running.TryRemove(runId, out var cts); cts?.Dispose(); }
    }

    private async Task SaveAsync(int runId, List<ControlStepResult> results, ControlRunStatus? status, string? message)
    {
        await using var db = await factory.CreateDbContextAsync();
        var run = await db.ControlSequenceRuns.SingleAsync(x => x.Id == runId);
        run.StepsJson = JsonSerializer.Serialize(results, Json);
        if (status is ControlRunStatus s) { run.Status = s; run.EndedTs = ScadaTime.NowMs(); run.Message = message; }
        await db.SaveChangesAsync();
    }

    private static string Trim(string text) => text.Length > 500 ? text[..500] : text;

    private static OperationLog Audit(int? userId, string userName, string action, string result, int? runId, int? tagId = null) => new()
    {
        UserId = userId, UserName = userName, Ts = ScadaTime.NowMs(), Action = action, Result = Trim(result), TagId = tagId,
        TargetType = runId is null ? "Control" : "ControlSequenceRun", TargetId = runId?.ToString()
    };
}
