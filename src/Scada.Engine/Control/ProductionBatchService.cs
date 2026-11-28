using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Control;

// 批次编排层：开始批次 → 配方下发 →（可选）启动顺控。写值只走既有 RecipeService / ControlWorkflowService（均经 ControlCommandService）。
// 接管或重启后不自动续跑，把在途批次记为需人工确认。
public sealed class ProductionBatchService(
    IDbContextFactory<ConfigDbContext> factory,
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    RecipeService recipes,
    ControlWorkflowService workflow,
    ILogger<ProductionBatchService> logger,
    INodeRole? role = null) : BackgroundService
{
    public const string RestartInterruptMessage = "服务重启，批次结果未知，不会自动续跑；请核对现场后重新开批";
    public const string TakeoverUnknownMessage = "主备接管，批次结果未知，不会自动续跑；请核对现场后重新开批";

    private readonly INodeRole _role = role ?? new StandaloneNodeRole();
    private long _reconciledGeneration = long.MinValue;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (_role.IsActive && _reconciledGeneration != _role.Generation)
            {
                await ReconcileOrphanBatchesAsync(_role.Generation > 0, stoppingToken);
                _reconciledGeneration = _role.Generation;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var cts in _running.Values) cts.Cancel();
        try { await Task.WhenAll(_tasks.Values).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); } catch (Exception ex) { logger.LogWarning(ex, "等待批次编排结束超时"); }
        await base.StopAsync(cancellationToken);
    }

    public async Task ReconcileOrphanBatchesAsync(bool takeover, CancellationToken stoppingToken)
    {
        try
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(stoppingToken);
            var message = takeover ? TakeoverUnknownMessage : RestartInterruptMessage;
            var now = ScadaTime.NowMs();
            foreach (var batch in await runtime.ProductionBatches
                .Where(x => x.Status == ProductionBatchStatus.Starting || x.Status == ProductionBatchStatus.Running)
                .ToListAsync(stoppingToken))
            {
                if (_running.ContainsKey(batch.Id)) continue;
                batch.Status = ProductionBatchStatus.NeedsConfirmation;
                batch.EndedTs = now;
                batch.Message = message;
                runtime.OperationLogs.Add(Audit(batch.StartedByUserId, "system", $"批次中断 {batch.BatchNumber}", message, batch.Id));
            }
            await runtime.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "恢复生产批次状态失败"); }
    }

    public async Task<ProductionBatch> StartAsync(string batchNumber, int recipeId, int versionNo, int? sequenceId, ControlActor actor, CancellationToken ct)
    {
        var number = batchNumber.Trim();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var (template, recipe, version) = await RecipeService.LoadAsync(db, recipeId, versionNo, ct);
        ControlSequence? sequence = null;
        if (sequenceId is int sid)
        {
            sequence = await db.ControlSequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sid, ct)
                ?? throw new ControlWorkflowException("顺控不存在", 404);
        }

        if (await runtime.ProductionBatches.AnyAsync(x => x.BatchNumber == number, ct))
            throw new ControlWorkflowException("批号已存在", 400);

        var batch = new ProductionBatch
        {
            BatchNumber = number,
            AreaId = template.AreaId,
            RecipeId = recipe.Id,
            RecipeName = recipe.Name,
            RecipeVersion = version.Version,
            SequenceId = sequence?.Id,
            SequenceName = sequence?.Name,
            Status = ProductionBatchStatus.Starting,
            StartedTs = ScadaTime.NowMs(),
            StartedByUserId = actor.UserId,
            StartedBy = actor.UserName
        };
        runtime.ProductionBatches.Add(batch);
        await runtime.SaveChangesAsync(ct);
        runtime.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"开始批次 {number}",
            sequence is null ? $"下发配方 {recipe.Name} v{version.Version}" : $"下发配方 {recipe.Name} v{version.Version} 后执行顺控 {sequence.Name}", batch.Id));
        await runtime.SaveChangesAsync(ct);

        try
        {
            var download = await recipes.RequestAsync(recipe.Id, version.Version, actor, ct);
            batch.RecipeDownloadId = download.Id;
            if (download.Status is RecipeDownloadStatus.Rejected or RecipeDownloadStatus.Failed)
            {
                batch.Status = ProductionBatchStatus.Failed;
                batch.EndedTs = ScadaTime.NowMs();
                batch.Message = Trim("配方下发失败，整批失败，未启动顺控。" + (download.Message ?? ""));
                runtime.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"批次失败 {number}", batch.Message, batch.Id));
                await runtime.SaveChangesAsync(ct);
                return batch;
            }
            await runtime.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            batch.Status = ProductionBatchStatus.Failed;
            batch.EndedTs = ScadaTime.NowMs();
            batch.Message = Trim("配方下发失败，整批失败，未启动顺控。" + ex.Message);
            runtime.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"批次失败 {number}", batch.Message, batch.Id));
            await runtime.SaveChangesAsync(ct);
            if (ex is ControlWorkflowException) throw;
            throw new ControlWorkflowException(batch.Message, 400);
        }

        var cts = new CancellationTokenSource();
        _running[batch.Id] = cts;
        _tasks[batch.Id] = Task.Run(() => OrchestrateAsync(batch.Id, actor, cts.Token));
        await using var fresh = await runtimeFactory.CreateDbContextAsync(ct);
        return await fresh.ProductionBatches.AsNoTracking().SingleAsync(x => x.Id == batch.Id, ct);
    }

    public async Task<bool> AbortAsync(int batchId, ControlActor actor, CancellationToken ct)
    {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var batch = await runtime.ProductionBatches.SingleOrDefaultAsync(x => x.Id == batchId, ct);
        if (batch is null) return false;
        if (batch.Status is not (ProductionBatchStatus.Starting or ProductionBatchStatus.Running)) return false;

        if (batch.RecipeDownloadId is int downloadId)
        {
            var download = await runtime.RecipeDownloads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == downloadId, ct);
            if (download?.Status == RecipeDownloadStatus.Pending && download.ApprovalId is Guid approval)
            {
                try { await workflow.DecideAsync(approval, false, actor, "中止批次", ct); }
                catch (ControlWorkflowException) { /* 复核可能已结束 */ }
            }
            else
                await recipes.AbortAsync(downloadId, actor, ct);
        }
        if (batch.SequenceRequestId is Guid seqReq)
        {
            try { await workflow.DecideAsync(seqReq, false, actor, "中止批次", ct); }
            catch (ControlWorkflowException) { }
        }
        if (batch.ControlSequenceRunId is int runId)
            await workflow.AbortAsync(runId, actor, ct);

        if (_running.TryGetValue(batchId, out var cts)) cts.Cancel();

        batch.Status = ProductionBatchStatus.Aborted;
        batch.EndedTs = ScadaTime.NowMs();
        batch.Message = "已中止批次（同时中止在途的配方下发与顺控）";
        runtime.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"中止批次 {batch.BatchNumber}", batch.Message, batch.Id));
        await runtime.SaveChangesAsync(ct);
        return true;
    }

    public Task WaitAsync(int batchId) => _tasks.TryGetValue(batchId, out var task) ? task : Task.CompletedTask;

    private async Task OrchestrateAsync(int batchId, ControlActor actor, CancellationToken ct)
    {
        try
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var batch = await runtime.ProductionBatches.SingleAsync(x => x.Id == batchId, ct);
            if (batch.RecipeDownloadId is int downloadId)
                await WaitDownloadAsync(downloadId, ct);

            await runtime.Entry(batch).ReloadAsync(ct);
            if (batch.Status is ProductionBatchStatus.Aborted or ProductionBatchStatus.NeedsConfirmation) return;

            var download = batch.RecipeDownloadId is int did
                ? await runtime.RecipeDownloads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == did, ct)
                : null;
            if (download is null || IsUnknown(download.Message) || IsUnknown(batch.Message))
            {
                await FinishAsync(runtime, batch, ProductionBatchStatus.NeedsConfirmation, download?.Message ?? TakeoverUnknownMessage, actor, "system");
                return;
            }
            if (download.Status != RecipeDownloadStatus.Completed)
            {
                var failed = download.Status == RecipeDownloadStatus.Aborted ? ProductionBatchStatus.Aborted : ProductionBatchStatus.Failed;
                await FinishAsync(runtime, batch, failed, "配方下发失败，整批失败，未启动顺控。" + (download.Message ?? RecipeService.Label(download.Status)), actor, actor.UserName);
                return;
            }

            if (batch.SequenceId is not int sequenceId)
            {
                await FinishAsync(runtime, batch, ProductionBatchStatus.Completed, "配方下发完成", actor, actor.UserName);
                return;
            }

            if (ct.IsCancellationRequested) return;
            await using var db = await factory.CreateDbContextAsync(ct);
            var sequence = await db.ControlSequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sequenceId, ct);
            if (sequence is null)
            {
                await FinishAsync(runtime, batch, ProductionBatchStatus.Failed, "配方已下发，但顺控已不存在，未能启动", actor, actor.UserName);
                return;
            }

            batch.Status = ProductionBatchStatus.Running;
            batch.Message = $"配方已下发，正在执行顺控 {sequence.Name}";
            try
            {
                if (sequence.DualApproval)
                {
                    var pending = await workflow.RequestSequenceAsync(sequence, actor, ct);
                    batch.SequenceRequestId = pending.Id;
                    batch.Message = $"配方已下发，顺控 {sequence.Name} 等待复核";
                    runtime.OperationLogs.Add(Audit(actor.UserId, actor.UserName, $"批次 {batch.BatchNumber} 申请启动顺控", batch.Message, batch.Id));
                    await runtime.SaveChangesAsync(ct);
                    await WaitSequenceRequestAsync(batch.Id, pending.Id, ct);
                    await runtime.Entry(batch).ReloadAsync(ct);
                    if (batch.Status is ProductionBatchStatus.Aborted or ProductionBatchStatus.NeedsConfirmation) return;
                    if (batch.ControlSequenceRunId is not int approvedRun)
                    {
                        await FinishAsync(runtime, batch, ProductionBatchStatus.Failed, "顺控复核未通过或已过期，批次失败（配方已下发）", actor, actor.UserName);
                        return;
                    }
                    await workflow.WaitAsync(approvedRun);
                }
                else
                {
                    var run = await workflow.StartAsync(sequence, actor, null, ct);
                    batch.ControlSequenceRunId = run.Id;
                    await runtime.SaveChangesAsync(ct);
                    await workflow.WaitAsync(run.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FinishAsync(runtime, batch, ProductionBatchStatus.Failed, "配方已下发，但顺控启动失败：" + ex.Message, actor, actor.UserName);
                return;
            }

            await runtime.Entry(batch).ReloadAsync(ct);
            if (batch.Status is ProductionBatchStatus.Aborted or ProductionBatchStatus.NeedsConfirmation) return;
            if (batch.ControlSequenceRunId is not int runId)
            {
                await FinishAsync(runtime, batch, ProductionBatchStatus.Failed, "配方已下发，但未能取得顺控运行记录", actor, actor.UserName);
                return;
            }
            var runRow = await runtime.ControlSequenceRuns.AsNoTracking().SingleAsync(x => x.Id == runId, ct);
            if (IsUnknown(runRow.Message))
            {
                await FinishAsync(runtime, batch, ProductionBatchStatus.NeedsConfirmation, runRow.Message ?? TakeoverUnknownMessage, actor, "system");
                return;
            }
            var status = runRow.Status switch
            {
                ControlRunStatus.Completed => ProductionBatchStatus.Completed,
                ControlRunStatus.Aborted => ProductionBatchStatus.Aborted,
                _ => ProductionBatchStatus.Failed
            };
            var message = runRow.Status == ControlRunStatus.Completed
                ? "配方下发与顺控均已完成"
                : "顺控未完成：" + (runRow.Message ?? runRow.Status.ToString());
            await FinishAsync(runtime, batch, status, message, actor, actor.UserName);
        }
        catch (OperationCanceledException) { /* 中止或停止 */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "编排生产批次 {BatchId} 失败", batchId);
            try
            {
                await using var runtime = await runtimeFactory.CreateDbContextAsync(CancellationToken.None);
                var batch = await runtime.ProductionBatches.SingleOrDefaultAsync(x => x.Id == batchId);
                if (batch is not null && batch.Status is ProductionBatchStatus.Starting or ProductionBatchStatus.Running)
                    await FinishAsync(runtime, batch, ProductionBatchStatus.Failed, Trim("批次编排失败：" + ex.Message), actor, "system");
            }
            catch (Exception saveEx) { logger.LogError(saveEx, "记录批次失败状态时出错"); }
        }
        finally
        {
            _running.TryRemove(batchId, out _);
        }
    }

    private async Task WaitDownloadAsync(int downloadId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var download = await runtime.RecipeDownloads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == downloadId, ct);
            if (download is null) return;
            if (download.Status == RecipeDownloadStatus.Pending)
            {
                await Task.Delay(200, ct);
                continue;
            }
            if (download.Status == RecipeDownloadStatus.Running)
            {
                await recipes.WaitAsync(downloadId);
                return;
            }
            return;
        }
    }

    private async Task WaitSequenceRequestAsync(int batchId, Guid requestId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var request = await runtime.ControlRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct);
            var batch = await runtime.ProductionBatches.SingleOrDefaultAsync(x => x.Id == batchId, ct);
            if (batch is null || batch.Status is ProductionBatchStatus.Aborted or ProductionBatchStatus.NeedsConfirmation) return;
            if (request is null) return;
            if (request.Status == ControlRequestStatus.Pending || request.Status == ControlRequestStatus.Executing)
            {
                await Task.Delay(200, ct);
                continue;
            }
            if (request.RunId is int runId && batch.ControlSequenceRunId is null)
            {
                batch.ControlSequenceRunId = runId;
                await runtime.SaveChangesAsync(ct);
            }
            return;
        }
    }

    private static async Task FinishAsync(RuntimeDbContext runtime, ProductionBatch batch, ProductionBatchStatus status, string message, ControlActor actor, string userName)
    {
        if (batch.Status is ProductionBatchStatus.Aborted or ProductionBatchStatus.NeedsConfirmation && status != batch.Status)
            return;
        batch.Status = status;
        batch.EndedTs = ScadaTime.NowMs();
        batch.Message = Trim(message);
        var action = status switch
        {
            ProductionBatchStatus.Completed => $"批次完成 {batch.BatchNumber}",
            ProductionBatchStatus.Aborted => $"批次中止 {batch.BatchNumber}",
            ProductionBatchStatus.NeedsConfirmation => $"批次待确认 {batch.BatchNumber}",
            _ => $"批次失败 {batch.BatchNumber}"
        };
        runtime.OperationLogs.Add(Audit(actor.UserId, userName, action, batch.Message, batch.Id));
        await runtime.SaveChangesAsync();
    }

    public static bool IsUnknown(string? message) =>
        !string.IsNullOrWhiteSpace(message) && (message.Contains("结果未知", StringComparison.Ordinal) || message.Contains("不会自动续", StringComparison.Ordinal) || message.Contains("服务重启", StringComparison.Ordinal));

    public static string Label(ProductionBatchStatus s) => s switch
    {
        ProductionBatchStatus.Starting => "启动中",
        ProductionBatchStatus.Running => "运行中",
        ProductionBatchStatus.Completed => "完成",
        ProductionBatchStatus.Failed => "失败",
        ProductionBatchStatus.Aborted => "已中止",
        ProductionBatchStatus.NeedsConfirmation => "需确认",
        _ => s.ToString()
    };

    private static string Trim(string text) => text.Length > 1000 ? text[..1000] : text;

    private static OperationLog Audit(int? userId, string userName, string action, string result, int batchId) => new()
    {
        UserId = userId,
        UserName = userName,
        Ts = ScadaTime.NowMs(),
        Action = action.Length > 200 ? action[..200] : action,
        Result = Trim(result),
        TargetType = "ProductionBatch",
        TargetId = batchId.ToString(CultureInfo.InvariantCulture)
    };
}
