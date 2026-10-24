using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Server.Services;

namespace Scada.Server.Projects;

public sealed record ProjectApplyResult(ProjectPlan Plan, string BaseSha, IReadOnlyList<string> Errors, IReadOnlyList<string> Forbidden, IReadOnlyList<string> Warnings, int? VersionId);

// 工程管理：导出、导入（预览/应用）、版本快照、版本比较与回滚。
// 预览与应用走同一段代码：预览在事务内完整执行后回滚，因此预览通过即应用通过（除非期间配置被他人修改，用 baseSha 检测）。
public sealed class ProjectService(IDbContextFactory<ConfigDbContext> factory, AccessModelProvider models, CalcJobConfiguration jobs, ConfigReloader reloader, CalcJobEngine engine, ILogger<ProjectService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private volatile string? _snapshotFailure;

    // 自动快照失败（例如画面发布后）：发布本身已生效，但版本线缺一版，需在 /health 可见，直到下一次快照成功。
    public IReadOnlyList<string> Warnings() => _snapshotFailure is string message ? [message] : [];
    public void ReportSnapshotFailure(string what, Exception ex)
    {
        _snapshotFailure = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {what}后自动创建配置快照失败：{ex.Message}";
        logger.LogError(ex, "{What}后自动创建配置快照失败", what);
    }

    public async Task<ProjectPackage> ExportAsync(Func<string, int[], bool>? include, string exportedBy, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await ProjectExporter.ExportAsync(db, await models.GetAsync(ct), include, exportedBy, ct);
    }

    public async Task<ConfigVersion?> GetVersionAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ConfigVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<ProjectPackage> PackageOfAsync(string reference, CancellationToken ct)
    {
        if (reference == "current") return await ExportAsync(null, "system", ct);
        if (!int.TryParse(reference, out var id) || await GetVersionAsync(id, ct) is not ConfigVersion version) throw new ProjectException("版本不存在", 404);
        var package = ProjectFormat.Parse(version.PackageJson);
        await using var db = await factory.CreateDbContextAsync(ct);
        ProjectFormat.UpgradeLegacyRules(package, await ProjectExporter.LegacyRuleKeysAsync(db, ct));
        return package;
    }

    public async Task<ConfigVersion> SnapshotAsync(string kind, string note, int? userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var package = await ProjectExporter.ExportAsync(db, await models.GetAsync(ct), null, userName, ct);
        var version = NewVersion(kind, note, userId, userName, package);
        db.ConfigVersions.Add(version);
        await db.SaveChangesAsync(ct);
        db.OperationLogs.Add(new OperationLog { UserId = userId, UserName = userName, Ts = version.CreatedTs, Action = $"创建配置快照：{note}", Result = "成功", TargetType = "ConfigVersion", TargetId = version.Id.ToString(), Details = JsonSerializer.Serialize(new { kind, sha256 = version.ContentSha256, summary = ProjectFormat.Summary(package) }) });
        await db.SaveChangesAsync(ct);
        _snapshotFailure = null;
        return version;
    }

    private static ConfigVersion NewVersion(string kind, string note, int? userId, string userName, ProjectPackage package) => new()
    {
        Kind = kind, Note = note.Length > 500 ? note[..500] : note, CreatedTs = ScadaTime.NowMs(), CreatedByUserId = userId, CreatedBy = userName,
        PackageJson = JsonSerializer.Serialize(package, ProjectFormat.Json), ContentSha256 = ProjectFormat.ContentHash(package),
        SummaryJson = JsonSerializer.Serialize(ProjectFormat.Summary(package))
    };

    // 事务提交之后才允许触碰运行时：重载配置快照与采集（同时让授权模型失效、触发实时推送），并让计算引擎重载。
    // ProjectApplier 只接收 DbContext 与纯校验服务，预览路径（含失败回滚）不会到达这里，因此没有事务外副作用：
    // 不改内存缓存、不重载运行时、不推送实时消息、不下发网关配置（网关按已提交配置拉取）、不写审计（审计与快照都在事务内，随回滚撤销）。
    private async Task PublishRuntimeAsync()
    {
        await reloader.ReloadAsync(CancellationToken.None);
        engine.RequestReload();
    }

    // mirror=false：导入；mirror=true：回滚到目标包。dryRun=true 只预览。
    public async Task<ProjectApplyResult> RunAsync(ProjectPackage target, bool mirror, ConflictResolution resolution, IReadOnlyDictionary<string, ConflictResolution>? overrides,
        string? expectedBaseSha, bool dryRun, AccessContext access, int? userId, string userName, string label, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var model = await models.GetAsync(ct);
            var current = await ProjectExporter.ExportAsync(db, model, null, userName, ct);
            var baseSha = ProjectFormat.ContentHash(current);
            ProjectFormat.UpgradeLegacyRules(target, await ProjectExporter.LegacyRuleKeysAsync(db, ct));
            if (!dryRun && expectedBaseSha is not null && expectedBaseSha != baseSha) throw new ProjectException("预览之后工程配置已被修改，请重新预览", 409);
            var plan = ProjectPlanner.Build(current, target, mirror, resolution, overrides);
            var applier = new ProjectApplier(db, jobs, access, userId, userName);
            var errors = new List<string>(); var forbidden = new List<string>();
            if (plan.Effective.Any())
                try { await applier.ApplyAsync(plan, ct); }
                catch (ProjectApplyException ex) { errors.AddRange(ex.Errors); forbidden.AddRange(ex.Forbidden); }
            var warnings = plan.Warnings.Concat(applier.Warnings).Distinct().ToList();
            if (dryRun || errors.Count > 0 || forbidden.Count > 0 || !plan.Effective.Any())
            {
                await transaction.RollbackAsync(ct);
                return new(plan, baseSha, errors, forbidden, warnings, null);
            }
            // 发布：同一事务内记录“变更前”与“变更后”两个快照，并写差分审计。
            db.ChangeTracker.Clear();
            var after = await ProjectExporter.ExportAsync(db, model, null, userName, ct);
            var latest = await db.ConfigVersions.AsNoTracking().OrderByDescending(x => x.Id).Select(x => x.ContentSha256).FirstOrDefaultAsync(ct);
            if (latest != baseSha) db.ConfigVersions.Add(NewVersion(mirror ? "pre-rollback" : "pre-import", $"{label}之前", userId, userName, current));
            var version = NewVersion(mirror ? "rollback" : "import", label, userId, userName, after);
            db.ConfigVersions.Add(version);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = userId, UserName = userName, Ts = ScadaTime.NowMs(), Action = label, Result = "成功", TargetType = "Project",
                Details = JsonSerializer.Serialize(new
                {
                    baseSha, afterSha = version.ContentSha256,
                    changes = plan.Effective.Take(1000).Select(x => new { section = x.Section, key = x.FinalKey, action = x.Action, changes = x.Changes.ToDictionary(c => c.Field, c => new { old = c.Old, @new = c.New }) }),
                    truncated = plan.Effective.Count() > 1000
                }, ProjectFormat.Json)
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _snapshotFailure = null;
            logger.LogInformation("{Label}：{Count} 项变更", label, plan.Effective.Count());
            await PublishRuntimeAsync();
            return new(plan, baseSha, [], [], warnings, version.Id);
        }
        finally { Gate.Release(); }
    }
}
