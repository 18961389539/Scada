using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.Health;

namespace Scada.Engine.Maintenance;

public sealed record BackupTargetResult(string Target, string Kind, bool Ok, string? Error, IReadOnlyList<string> Pruned);
public sealed record BackupRun(string Trigger, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string? FileName, long Bytes, bool Ok,
    string? Error, BackupDrill? Drill, IReadOnlyList<BackupTargetResult> Targets, IReadOnlyList<string> Pruned);

// 定时备份流水线：完整备份 → 恢复演练 → 异地拷贝（各目标独立，失败互不影响）→ 本地与异地保留策略。结果记入 backup-runs.json。
public sealed class BackupScheduler(BackupService backups, IOptions<ScadaSettings> options, ILogger<BackupScheduler> logger, INodeRole? role = null,
    Func<BackupTargetOptions, IBackupTarget>? targetFactory = null) : IHealthContributor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ScadaSettings _settings = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string RunsPath => Path.Combine(backups.Root, "backup-runs.json");

    public IReadOnlyList<BackupRun> Runs()
    {
        try { return File.Exists(RunsPath) ? JsonSerializer.Deserialize<List<BackupRun>>(File.ReadAllText(RunsPath), Json) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException) { logger.LogWarning(ex, "读取备份记录失败"); return []; }
    }

    public IReadOnlyList<SystemWarning> Warnings()
    {
        var last = Runs().FirstOrDefault();
        if (last is null) return [];
        var list = new List<SystemWarning>();
        if (last.FileName is null) list.Add(new("backup_failed", $"最近一次备份失败：{last.Error}"));
        else if (last.Drill is { Ok: false } drill) list.Add(new("backup_drill_failed", $"备份 {last.FileName} 恢复演练未通过：{string.Join("；", drill.Problems)}"));
        foreach (var t in last.Targets.Where(x => !x.Ok)) list.Add(new("backup_offsite_failed", $"异地备份 {t.Target} 失败：{t.Error}"));
        return list;
    }

    // 健康聚合通过接口收集，不再由聚合方按具体类型反查。
    IEnumerable<SystemWarning> IHealthContributor.HealthWarnings() => Warnings();

    public IReadOnlyList<(string Name, string Kind, int KeepLatest)> Targets() => _settings.BackupTargets.Select(o => (o.Name, o.Type, o.KeepLatest)).ToList();

    public async Task<BackupRun> RunAsync(string trigger, CancellationToken ct) => (await RunCoreAsync(trigger, false, ct))!;

    public Task<BackupRun?> RunScheduledAsync(CancellationToken ct) => RunCoreAsync("定时", true, ct);

    private async Task<BackupRun?> RunCoreAsync(string trigger, bool scheduled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var started = DateTimeOffset.UtcNow;
        BackupRun run;
        try
        {
            if (scheduled)
            {
                if (_settings.FullBackupIntervalHours <= 0 || role is { IsActive: false }) return null;
                var latest = backups.List().FirstOrDefault();
                if (latest is not null && DateTimeOffset.UtcNow - latest.CreatedAt < TimeSpan.FromHours(_settings.FullBackupIntervalHours)) return null;
            }
            BackupSummary summary;
            try { summary = await backups.CreateAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "完整备份失败");
                run = new(trigger, started, DateTimeOffset.UtcNow, null, 0, false, ex.Message, null, [], []);
                Save(run); return run;
            }
            var drill = _settings.BackupDrillEnabled ? await backups.DrillAsync(summary.FileName, ct) : null;
            var targets = new List<BackupTargetResult>();
            foreach (var o in _settings.BackupTargets)
            {
                var name = string.IsNullOrWhiteSpace(o.Name) ? o.Type : o.Name;
                if (drill is { Ok: false }) { targets.Add(new(name, o.Type, false, "恢复演练未通过，未上传此备份", [])); continue; }
                IBackupTarget? target = null;
                try
                {
                    // Fail before constructing a target or making ANY remote call (including retention).
                    BackupSecurity.RequireEncryptedOffsite(summary.Path);
                    target = (targetFactory ?? (x => BackupTargetFactory.Create(x)))(o);
                    await target.UploadAsync(summary.Path, summary.FileName, ct);
                    var remote = await target.ListAsync(ct);
                    if (!remote.Any(x => x.FileName == summary.FileName && x.Bytes == summary.Bytes)) throw new IOException("上传后在目标中未找到大小一致的副本");
                    var pruned = new List<string>();
                    foreach (var old in remote.OrderByDescending(x => x.FileName, StringComparer.Ordinal).Skip(target.KeepLatest))
                    {
                        await target.DeleteAsync(old.FileName, ct); pruned.Add(old.FileName);
                    }
                    targets.Add(new(target.Name, target.Kind, true, null, pruned));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "异地备份到 {Target} 失败", name);
                    targets.Add(new(name, o.Type, false, ex.Message, []));
                }
                finally { (target as IDisposable)?.Dispose(); }
            }
            var prunedLocal = backups.Prune();
            var ok = drill?.Ok != false && targets.All(x => x.Ok);
            run = new(trigger, started, DateTimeOffset.UtcNow, summary.FileName, summary.Bytes, ok, ok ? null : "演练或异地拷贝未全部成功", drill, targets, prunedLocal);
            Save(run);
            return run;
        }
        finally { _gate.Release(); }
    }

    private void Save(BackupRun run)
    {
        try
        {
            Directory.CreateDirectory(backups.Root);
            var runs = Runs().Prepend(run).Take(100).ToList();
            File.WriteAllText(RunsPath + ".tmp", JsonSerializer.Serialize(runs, Json));
            File.Move(RunsPath + ".tmp", RunsPath, true);
        }
        catch (IOException ex) { logger.LogWarning(ex, "写入备份记录失败"); }
    }
}
