using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Services;

/// <summary>
/// 本机控制的持久命令号。未下发可以再次执行；已经送出或结果未知的编号不会再写。
/// </summary>
public sealed class LocalCommandLedger(IDbContextFactory<RuntimeDbContext> contexts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CommandExecutionResult> ExecuteAsync(CollectorEngine local, int deviceId, Tag tag, double value, CollectorCommandContext context, CancellationToken ct)
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
        if (Stored(row) is { } stored && stored.Outcome != GatewayCommandOutcome.NotSent)
            return (ToResult(stored, id), 0);
        var attempt = row.Attempt;
        row.Attempt = attempt + 1;
        row.State = "Running";
        row.ResultJson = null;
        db.Entry(row).Property(entry => entry.Attempt).OriginalValue = attempt;
        try
        {
            await db.SaveChangesAsync(ct);
            return (null, row.Attempt);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (new(false, "命令已受理且尚未结束，不会再次下发", Outcome: GatewayCommandOutcome.Unknown, CommandId: id), 0);
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
