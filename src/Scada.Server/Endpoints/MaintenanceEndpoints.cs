using Scada.Server.Auth;
using Scada.Engine.Maintenance;
using Microsoft.EntityFrameworkCore;
using Scada.Data;

namespace Scada.Server.Endpoints;

public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var maintenance = app.MapGroup("/api/maintenance").RequireAuthorization(AccessPolicies.GlobalSystem);
        var group = maintenance.MapGroup("/backups");

        maintenance.MapPost("/diagnostics", async (DiagnosticService diagnostics, CancellationToken cancellationToken) =>
        {
            var bundle = await diagnostics.CreateAsync(cancellationToken);
            return Results.File(bundle.Path, "application/zip", bundle.FileName);
        });

        group.MapGet("", (BackupService backups) => Results.Ok(backups.List()));

        group.MapPost("", async (BackupService backups, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken cancellationToken) =>
        {
            var summary = await backups.CreateAsync(cancellationToken);
            await LogAsync(factory, http, "创建完整备份", summary.FileName, $"{summary.Bytes} 字节");
            return Results.Ok(summary);
        });

        // 完整流水线：备份 → 恢复演练 → 异地拷贝 → 保留策略（与定时任务相同）。
        group.MapPost("/run", async (BackupScheduler scheduler, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken cancellationToken) =>
        {
            var run = await scheduler.RunAsync("手动", cancellationToken);
            await LogAsync(factory, http, "备份并演练", run.FileName ?? "-", run.Ok ? "成功" : "失败：" + (run.Error ?? ""));
            return Results.Ok(run);
        });
        group.MapGet("/runs", (BackupScheduler scheduler) => Results.Ok(scheduler.Runs()));
        group.MapGet("/targets", (BackupScheduler scheduler) => Results.Ok(scheduler.Targets().Select(x => new { name = x.Name, kind = x.Kind, keepLatest = x.KeepLatest })));
        group.MapPost("/{fileName}/drill", async (string fileName, BackupService backups, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken cancellationToken) =>
        {
            try { backups.Resolve(fileName); }
            catch (ArgumentException ex) { return EndpointHelpers.BadRequest(ex.Message); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { message = ex.Message }); }
            var drill = await backups.DrillAsync(fileName, cancellationToken);
            await LogAsync(factory, http, "备份恢复演练", fileName, drill.Ok ? "通过" : "失败：" + string.Join("；", drill.Problems));
            return Results.Ok(drill);
        });

        group.MapPost("/{fileName}/verify", async (string fileName, BackupService backups, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await backups.VerifyAsync(fileName, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return EndpointHelpers.BadRequest(ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        group.MapGet("/{fileName}", (string fileName, BackupService backups) =>
        {
            try
            {
                var path = backups.Resolve(fileName);
                return Results.File(path, "application/zip", fileName);
            }
            catch (ArgumentException ex)
            {
                return EndpointHelpers.BadRequest(ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });
    }

    private static async Task LogAsync(IDbContextFactory<ConfigDbContext> factory, HttpContext http, string action, string target, string result)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.OperationLogs.Add(new() { UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = Scada.Core.Models.ScadaTime.NowMs(), Action = action,
            Result = result.Length > 500 ? result[..500] : result, TargetType = "Backup", TargetId = target });
        await db.SaveChangesAsync();
    }
}
