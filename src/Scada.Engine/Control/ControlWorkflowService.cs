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

    // 读取并规范化步骤（旧的线性步骤自动补齐编号与类型，按原顺序执行，行为不变）。
    public static IReadOnlyList<ControlSequenceStep> Steps(ControlSequence sequence) => SequenceGraph.Normalize(JsonSerializer.Deserialize<List<ControlSequenceStep>>(sequence.StepsJson, Json) ?? []);
    public static List<ControlStepResult> InitialResults(IReadOnlyList<ControlSequenceStep> steps) =>
        steps.Select((x, i) => new ControlStepResult { Index = i + 1, StepId = x.Id, Type = x.Type, Name = x.Name, TagId = x.TagId, Value = x.Value }).ToList();

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
        // 标记已执行并落库。注意：无条件更新且无并发令牌，并发复核时两人都能通过上面的 Pending 检查。
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
            StepsJson = JsonSerializer.Serialize(InitialResults(steps), Json)
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

    private sealed class RunContext
    {
        public required int RunId; public required ControlSequence Sequence; public required IReadOnlyList<ControlSequenceStep> Steps; public required Dictionary<string, int> Index;
        public required List<ControlStepResult> Results; public required ControlActor Actor; public string? ApprovedBy; public CancellationToken Abort;
        public readonly ConcurrentDictionary<string, int> Jumps = new();
        public readonly List<string> Continued = []; public string? Diverted; public int Executed;
        public readonly SemaphoreSlim SaveGate = new(1, 1);
    }
    private enum PathOutcome { Ended, Joined, Failed, Aborted }
    private sealed record PathResult(PathOutcome Outcome, string? Message = null);
    public const int MaxExecutedSteps = 10_000;

    private async Task RunAsync(int runId, ControlSequence sequence, IReadOnlyList<ControlSequenceStep> steps, ControlActor actor, string? approvedBy, CancellationToken ct)
    {
        var ctx = new RunContext
        {
            RunId = runId, Sequence = sequence, Steps = steps, Index = steps.Select((x, i) => (x.Id!, i)).ToDictionary(x => x.Item1, x => x.i),
            Results = InitialResults(steps), Actor = actor, ApprovedBy = approvedBy, Abort = ct
        };
        var status = ControlRunStatus.Completed; string? message = null;
        try
        {
            var result = await RunPathAsync(ctx, steps[0].Id!, null, CancellationToken.None);
            (status, message) = result.Outcome switch
            {
                PathOutcome.Aborted => (ControlRunStatus.Aborted, result.Message),
                PathOutcome.Failed => (ControlRunStatus.Failed, result.Message),
                _ when ctx.Diverted is not null => (ControlRunStatus.Failed, $"{ctx.Diverted}；失败处理步骤已执行完毕，请核对现场"),
                _ when ctx.Continued.Count > 0 => (ControlRunStatus.Completed, $"完成，但有 {ctx.Continued.Count} 处步骤失败已按配置继续：{string.Join("；", ctx.Continued.Take(5))}"),
                _ => (ControlRunStatus.Completed, null)
            };
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            status = ControlRunStatus.Failed; message = "服务停止，顺控中断；请核对现场状态后重新执行";
        }
        catch (Exception ex) { status = ControlRunStatus.Failed; message = Trim("顺控执行异常：" + ex.Message); logger.LogError(ex, "顺控 {Name} 执行异常", sequence.Name); }
        lock (ctx.Results)
        {
            foreach (var r in ctx.Results.Where(x => x.Status == "running")) { r.Status = "failed"; r.Message = _shutdown.IsCancellationRequested ? "服务停止，中断" : r.Message ?? "未完成"; r.EndedTs = ScadaTime.NowMs(); }
            foreach (var r in ctx.Results.Where(x => x.Status is "pending")) r.Status = "skipped";
        }
        try
        {
            await SaveAsync(ctx, status, message);
            await using var db = await factory.CreateDbContextAsync();
            db.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"顺控结束 {sequence.Name}", status switch
            {
                ControlRunStatus.Completed => message is null ? "完成" : message, ControlRunStatus.Aborted => "中止：" + message, _ => (message?.Contains("中断") == true ? "中断：" : "失败：") + message
            }, runId));
            await db.SaveChangesAsync();
        }
        finally { _running.TryRemove(runId, out var cts); cts?.Dispose(); }
    }

    private static string? Fallthrough(RunContext ctx, int i) => ctx.Steps[i].Next ?? (i + 1 < ctx.Steps.Count ? ctx.Steps[i + 1].Id : null);

    // 沿步骤图执行一条路径，直到结束、到达 stopAt（并行分支的汇合步骤）、失败或中止。
    private async Task<PathResult> RunPathAsync(RunContext ctx, string? id, string? stopAt, CancellationToken branch)
    {
        while (true)
        {
            if (id is null) return new(PathOutcome.Ended);
            if (id == stopAt) return new(PathOutcome.Joined);
            var i = ctx.Index[id]; var step = ctx.Steps[i]; var r = ctx.Results[i];
            if (_shutdown.IsCancellationRequested) return new(PathOutcome.Failed, $"服务停止，顺控在步骤“{step.Name}”之前中断；请核对现场状态后重新执行");
            if (ctx.Abort.IsCancellationRequested) return new(PathOutcome.Aborted, $"在步骤“{step.Name}”之前中止");
            if (branch.IsCancellationRequested) return new(PathOutcome.Aborted, "分支已停止（其他分支已满足汇合条件或已失败）");
            if (Interlocked.Increment(ref ctx.Executed) > MaxExecutedSteps) return new(PathOutcome.Failed, $"执行步数超过 {MaxExecutedSteps}，已停止");
            lock (ctx.Results) { r.Status = "running"; r.StartedTs = ScadaTime.NowMs(); r.EndedTs = null; r.Executions++; r.Message = null; }
            await SaveAsync(ctx, null, null);
            if (step.Type == "end") { Finish(ctx, r, "done", "结束"); return new(PathOutcome.Ended); }
            var (ok, text, next) = await ExecuteStepAsync(ctx, i, step, branch);
            if (ok) { Finish(ctx, r, "done", text); id = next; continue; }
            Finish(ctx, r, "failed", text);
            var failure = $"第 {i + 1} 步“{step.Name}”失败：{text}";
            // 中止与服务停止不走失败处理（不能因此转入其他步骤继续下发）。
            if (ctx.Abort.IsCancellationRequested) return new(PathOutcome.Aborted, $"在步骤“{step.Name}”中止");
            if (_shutdown.IsCancellationRequested) return new(PathOutcome.Failed, $"服务停止，顺控在步骤“{step.Name}”中断；请核对现场状态后重新执行");
            if (branch.IsCancellationRequested) return new(PathOutcome.Aborted, "分支已停止（其他分支已满足汇合条件或已失败）");
            switch (step.OnFailure)
            {
                case "continue": lock (ctx.Continued) ctx.Continued.Add(failure); id = Fallthrough(ctx, i); continue;
                case "goto":
                    lock (ctx.Continued) ctx.Diverted ??= $"{failure}，已转入失败处理步骤 {step.OnFailureGoto}";
                    id = step.OnFailureGoto; continue;
                default: return new(PathOutcome.Failed, failure);
            }
        }
    }

    private void Finish(RunContext ctx, ControlStepResult r, string status, string? message)
    {
        lock (ctx.Results) { r.Status = status; r.Message = message is null ? null : Trim(message); r.EndedTs = ScadaTime.NowMs(); }
    }

    private async Task<(bool Ok, string Message, string? Next)> ExecuteStepAsync(RunContext ctx, int i, ControlSequenceStep step, CancellationToken branch)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(step.TimeoutMs, 100, 600_000));
        // 等待可被服务停止、中止或并行分支停止打断（写入本身不会被打断）。
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, branch, ctx.Abort);
        try { return await ExecuteCoreAsync(ctx, i, step, branch, timeout, waitCts.Token); }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested && (branch.IsCancellationRequested || ctx.Abort.IsCancellationRequested))
        {
            return (false, ctx.Abort.IsCancellationRequested ? "已中止" : "分支已停止", null);
        }
    }

    private async Task<(bool Ok, string Message, string? Next)> ExecuteCoreAsync(RunContext ctx, int i, ControlSequenceStep step, CancellationToken branch, TimeSpan timeout, CancellationToken wait)
    {
        switch (step.Type)
        {
            case "write":
            {
                var result = await commands.SubmitAsync(new ControlSubmission(step.TagId!.Value, step.Value, ctx.Actor, ControlOrigin.Sequence, $"顺控 {ctx.Sequence.Name} 步骤 {step.Id} {step.Name}",
                    Confirmed: true, PreApproved: ctx.ApprovedBy is not null, ApprovedBy: ctx.ApprovedBy), CancellationToken.None);
                if (!result.Success) return (false, result.Message, null);
                if (step.DelayMs > 0) await Task.Delay(Math.Min(step.DelayMs, 600_000), wait);
                if (!string.IsNullOrWhiteSpace(step.ConfirmExpression))
                {
                    var (ok, why) = await commands.WaitAsync(step.ConfirmExpression, timeout, wait);
                    if (!ok) return (false, $"完成条件未满足：{why}", null);
                }
                return (true, result.Message, Fallthrough(ctx, i));
            }
            case "wait":
            {
                var (ok, why) = await commands.WaitAsync(step.Expression!, timeout, wait);
                return ok ? (true, "条件成立", Fallthrough(ctx, i)) : (false, $"等待条件超时：{why}", null);
            }
            case "condition":
            {
                var (value, why) = commands.TryEvaluate(step.Expression!);
                if (value is null) return (false, $"条件无法判定：{why}", null);
                return value.Value ? (true, $"条件成立 → {step.Then}", step.Then) : (true, $"条件不成立 → {step.Else ?? Fallthrough(ctx, i) ?? "结束"}", step.Else ?? Fallthrough(ctx, i));
            }
            case "jump":
            {
                var count = ctx.Jumps.AddOrUpdate(step.Id!, 1, (_, n) => n + 1);
                if (count > step.MaxIterations) return (false, $"循环超过上限 {step.MaxIterations} 次", null);
                return (true, $"第 {count} 次跳转 → {step.Target}", step.Target);
            }
            case "parallel":
            {
                var join = ctx.Index[step.Join!]; var joinStep = ctx.Steps[join];
                using var siblings = CancellationTokenSource.CreateLinkedTokenSource(branch);
                var tasks = step.Branches!.Select(b => Task.Run(() => RunPathAsync(ctx, b, step.Join, siblings.Token))).ToList();
                var pending = tasks.ToList(); PathResult? winner = null; var failures = new List<string>();
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending); pending.Remove(done);
                    PathResult outcome;
                    try { outcome = await done; }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        // 服务停止：等其余分支也退出后再向上抛出，避免运行结束后仍有分支在下发。
                        siblings.Cancel();
                        try { await Task.WhenAll(pending); } catch { /* 同样是停止导致的取消 */ }
                        throw;
                    }
                    catch (Exception ex) { outcome = new(PathOutcome.Failed, Trim("分支执行异常：" + ex.Message)); logger.LogError(ex, "顺控 {Name} 分支异常", ctx.Sequence.Name); }
                    if (outcome.Outcome == PathOutcome.Joined)
                    {
                        if (joinStep.JoinMode == "any" && winner is null) { winner = outcome; siblings.Cancel(); }
                    }
                    else if (outcome.Outcome == PathOutcome.Failed || outcome.Outcome == PathOutcome.Aborted && !siblings.IsCancellationRequested)
                    {
                        failures.Add(outcome.Message ?? "分支失败");
                        if (joinStep.JoinMode != "any") siblings.Cancel(); // all：任一分支失败，其余分支在当前步骤结束后停止
                    }
                }
                var r = ctx.Results[join];
                lock (ctx.Results) { r.Executions++; r.StartedTs ??= ScadaTime.NowMs(); }
                if (_shutdown.IsCancellationRequested) { Finish(ctx, r, "failed", "服务停止，中断"); return (false, "服务停止，中断", null); }
                if (ctx.Abort.IsCancellationRequested) { Finish(ctx, r, "failed", "已中止"); return (false, "已中止", null); }
                var success = joinStep.JoinMode == "any" ? winner is not null : failures.Count == 0;
                if (!success)
                {
                    var why = joinStep.JoinMode == "any" ? $"所有分支均未成功：{string.Join("；", failures)}" : $"分支失败：{string.Join("；", failures)}";
                    Finish(ctx, r, "failed", why); return (false, why, null);
                }
                Finish(ctx, r, "done", joinStep.JoinMode == "any" ? "任一分支完成，已汇合" : "全部分支完成，已汇合");
                return (true, $"并行完成（{(joinStep.JoinMode == "any" ? "任一" : "全部")}）", Fallthrough(ctx, join));
            }
            default: return (false, $"步骤类型 {step.Type} 不能直接执行", null);
        }
    }

    private async Task SaveAsync(RunContext ctx, ControlRunStatus? status, string? message)
    {
        await ctx.SaveGate.WaitAsync();
        try
        {
            string json; lock (ctx.Results) json = JsonSerializer.Serialize(ctx.Results, Json);
            await using var db = await factory.CreateDbContextAsync();
            var run = await db.ControlSequenceRuns.SingleAsync(x => x.Id == ctx.RunId);
            run.StepsJson = json;
            if (status is ControlRunStatus s) { run.Status = s; run.EndedTs = ScadaTime.NowMs(); run.Message = message is null ? null : Trim(message); }
            await db.SaveChangesAsync();
        }
        finally { ctx.SaveGate.Release(); }
    }

    private static string Trim(string text) => text.Length > 500 ? text[..500] : text;

    private static OperationLog Audit(int? userId, string userName, string action, string result, int? runId, int? tagId = null) => new()
    {
        UserId = userId, UserName = userName, Ts = ScadaTime.NowMs(), Action = action, Result = Trim(result), TagId = tagId,
        TargetType = runId is null ? "Control" : "ControlSequenceRun", TargetId = runId?.ToString()
    };
}
