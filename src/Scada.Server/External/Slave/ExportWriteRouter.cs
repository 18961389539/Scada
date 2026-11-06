using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scada.Engine.Control;

namespace Scada.Server.External.Slave;

public sealed record ExportWriteResult(bool Success, string Message, double? ReadBack, bool Pending)
{
    public static ExportWriteResult Refused(string message) => new(false, message, null, false);
}

/// <summary>
/// 从站写回的唯一出口。外部主站（Modbus 主站 / 104 调度）的写入与站内操作走同一条控制闭环：
/// 就地/远程、挂牌、限幅、变化率、许可互锁、许可授权、回读确认和审计都由 ControlCommandService 统一执行。
/// 这样"外部写"不会成为绕开安全约束的旁路——这是从站功能能否上现场的前提。
/// </summary>
public sealed class ExportWriteRouter(IServiceScopeFactory scopes, ILogger<ExportWriteRouter> logger)
{
    public async Task<ExportWriteResult> WriteAsync(int tagId, double value, string source, string remote, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var commands = scope.ServiceProvider.GetRequiredService<ControlCommandService>();
            // 从站写入属于自动化指令，不弹确认框；需要双人复核的点仍会被控制层拒绝。
            var result = await commands.SubmitAsync(new ControlSubmission(
                tagId, value, new ControlActor(null, $"external:{source}:{remote}"),
                ControlOrigin.Operator, source, Confirmed: true), ct);
            // 拒绝原因必须落日志：Modbus 只能回一个异常码，104 只能回否定确认，
            // 现场排查"为什么写不进去"全靠这一行。
            if (!result.Success)
                logger.LogInformation("对外从站写入被拒绝：测点 {TagId}，来源 {Source}/{Remote}，{Message}", tagId, source, remote, result.Message);
            return new ExportWriteResult(result.Success, result.Message, result.ReadBack, result.Pending);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "对外从站写入测点 {TagId} 失败（来源 {Source}）", tagId, source);
            return ExportWriteResult.Refused("控制链路异常：" + ex.Message);
        }
    }
}
