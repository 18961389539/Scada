using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Interfaces;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Services;

/// <summary>
/// 本机控制的持久命令号。未下发可以再次执行；已经送出或结果未知的编号不会再写。
/// 启动或超时后把滞留的 Running 标为 Unknown，同一命令号只回放该结果。
/// </summary>
public sealed class LocalCommandLedger(IDbContextFactory<RuntimeDbContext> contexts)
{
    public const long StaleRunningMs = 300_000;
    public const string UnknownMessage = "服务在命令执行期间重启或超时，执行结果未知；不会自动重发，请核对现场状态后再用新命令号下发";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CommandExecutionResult> ExecuteAsync(ICollectorControl local, int deviceId, Tag tag, double value, CollectorCommandContext context, CancellationToken ct)
    {
        var id = context.RequestId is { } request && request != Guid.Empty ? request : Guid.NewGuid();
        var claim = await TryClaimAsync(id, deviceId, tag.Id, value, context, ct);
        if (claim.Result is { } existing) return existing with { CommandId = id };
        CommandExecutionResult result;
        try
        {
            result = Normalize(await local.WriteAsync(deviceId, tag, value, ct), id);
        }
        catch (OperationCanceledException)
        {
            result = new(false, "命令超时或请求已取消，执行结果未知，请检查设备反馈；不会自动重发", Outcome: GatewayCommandOutcome.Unknown, CommandId: id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = Normalize(new(false, ex.Message), id);
        }
        await CompleteAsync(id, claim.Attempt, result, CancellationToken.None);
        return result;
    }

    /// <summary>
    /// 把滞留的 Running 且尚无结果的命令标为 Unknown。maxAge 为 0 时收口全部滞留（启动）；否则只收口超过该时长的记录。
    /// </summary>
    public async Task<int> ReconcileStaleAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = ScadaTime.NowMs();
        var cutoff = maxAge <= TimeSpan.Zero ? now + 1 : now - (long)maxAge.TotalMilliseconds;
        var rows = await db.LocalControlCommands
            .Where(x => x.State == "Running" && (x.ResultJson == null || x.ResultJson == "") && x.CreatedTs <= cutoff)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        var json = JsonSerializer.Serialize(new GatewayCommandResult(GatewayCommandOutcome.Unknown, UnknownMessage, null), Json);
        foreach (var row in rows)
        {
            row.ResultJson = json;
            row.State = "Unknown";
            row.CompletedTs = now;
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<(CommandExecutionResult? Result, int Attempt)> TryClaimAsync(Guid id, int deviceId, int tagId, double value, CollectorCommandContext context, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.LocalControlCommands.FindAsync([id], ct);
        if (row is null)
        {
            row = new LocalControlCommand
            {
                Id = id, DeviceId = deviceId, TagId = tagId, Value = value, UserId = context.UserId, UserName = context.UserName ?? "",
                State = "Running", Attempt = 1, CreatedTs = ScadaTime.NowMs()
            };
            db.LocalControlCommands.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return (null, row.Attempt);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                row = await db.LocalControlCommands.FindAsync([id], ct) ?? throw new InvalidOperationException("命令记录在冲突后消失");
            }
        }
        if (row.UserId != context.UserId || row.DeviceId != deviceId || row.TagId != tagId || row.Value != value)
            return (new(false, "相同命令 ID 的账户或内容不一致，未新增下发", Outcome: GatewayCommandOutcome.NotSent, CommandId: id), 0);

        if (row.State == "Running" && string.IsNullOrWhiteSpace(row.ResultJson) && IsStale(row, ScadaTime.NowMs()))
            await MarkUnknownAsync(db, row, ct);

        if (Stored(row) is { } stored && stored.Outcome != GatewayCommandOutcome.NotSent)
            return (ToResult(stored, id), 0);
        if (row.State == "Unknown")
            return (new(false, UnknownMessage, Outcome: GatewayCommandOutcome.Unknown, CommandId: id), 0);
        if (row.State == "Running")
            return (new(false, "命令已受理且尚未结束，不会再次下发", Outcome: GatewayCommandOutcome.Unknown, CommandId: id), 0);

        return (new(false, UnknownMessage, Outcome: GatewayCommandOutcome.Unknown, CommandId: id), 0);
    }

    private static bool IsStale(LocalControlCommand row, long now) =>
        row.CreatedTs <= now - StaleRunningMs;

    private static async Task MarkUnknownAsync(RuntimeDbContext db, LocalControlCommand row, CancellationToken ct)
    {
        row.ResultJson = JsonSerializer.Serialize(new GatewayCommandResult(GatewayCommandOutcome.Unknown, UnknownMessage, null), Json);
        row.State = "Unknown";
        row.CompletedTs = ScadaTime.NowMs();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(row).ReloadAsync(ct);
        }
    }

    private async Task CompleteAsync(Guid id, int attempt, CommandExecutionResult result, CancellationToken ct)
    {
        if (attempt <= 0) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.LocalControlCommands.FindAsync([id], ct);
        if (row is null || row.Attempt != attempt) return;
        var outcome = result.Outcome ?? GatewayCommandOutcome.Failed;
        row.ResultJson = JsonSerializer.Serialize(new GatewayCommandResult(outcome, result.Message, result.ReadBack), Json);
        row.State = outcome == GatewayCommandOutcome.Unknown ? "Unknown" : "Completed";
        row.CompletedTs = ScadaTime.NowMs();
        await db.SaveChangesAsync(ct);
    }

    private static GatewayCommandResult? Stored(LocalControlCommand row) =>
        string.IsNullOrWhiteSpace(row.ResultJson) ? null : JsonSerializer.Deserialize<GatewayCommandResult>(row.ResultJson, Json);

    private static CommandExecutionResult ToResult(GatewayCommandResult stored, Guid id) =>
        new(stored.Outcome == GatewayCommandOutcome.Confirmed, stored.Message, stored.ReadBack, stored.Outcome, id);

    internal static CommandExecutionResult Normalize(CommandExecutionResult result, Guid id) =>
        CommandResults.Normalize(result, id);
}

/// <summary>
/// 启动时收口上次进程遗留的 Running 命令，并定期把超时仍无结果的记录标为 Unknown。
/// </summary>
public sealed class LocalCommandLedgerReconciler(LocalCommandLedger ledger, ILogger<LocalCommandLedgerReconciler> logger, INodeRole? role = null) : BackgroundService
{
    private readonly INodeRole _role = role ?? new StandaloneNodeRole();
    private long _reconciledGeneration = long.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastStale = Environment.TickCount64;
        do
        {
            if (!_role.IsActive) continue;
            try
            {
                if (_reconciledGeneration != _role.Generation)
                {
                    var recovered = await ledger.ReconcileStaleAsync(TimeSpan.Zero, stoppingToken);
                    if (recovered > 0)
                        logger.LogWarning("已将 {Count} 条滞留本机控制命令标为结果未知，不会自动重发", recovered);
                    _reconciledGeneration = _role.Generation;
                    lastStale = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - lastStale >= 30_000)
                {
                    var stale = await ledger.ReconcileStaleAsync(TimeSpan.FromMilliseconds(LocalCommandLedger.StaleRunningMs), stoppingToken);
                    if (stale > 0)
                        logger.LogWarning("已将 {Count} 条超时仍无结果的本机控制命令标为结果未知，不会自动重发", stale);
                    lastStale = Environment.TickCount64;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "收口滞留本机控制命令失败");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
