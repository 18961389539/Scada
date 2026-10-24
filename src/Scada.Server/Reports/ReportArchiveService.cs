using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Engine.Notifications;
using Scada.Engine.Reports;

namespace Scada.Server.Reports;

// 报表生成与归档：计算 → 渲染 Excel/PDF → 写入归档目录（只读属性 + SHA256）→ 追加 ReportRun（同周期版本号递增）→ 可选邮件入发件箱。
public sealed class ReportArchiveService(
    IDbContextFactory<ConfigDbContext> factory,
    ReportBuilder builder,
    AlarmNotificationDispatcher dispatcher,
    IOptions<ScadaSettings> options,
    ILogger<ReportArchiveService> logger)
{
    public string Root => Path.GetFullPath(options.Value.ReportsRoot);
    public TimeZoneInfo Zone => CalcJobEngine.ResolveZone(options.Value.ShiftTimeZone);

    public async Task<ReportRun> GenerateAsync(ReportTemplate template, long at, string trigger, int? userId, string userName, CancellationToken ct)
    {
        var (key, from, to) = ReportPeriods.Range(template.Period, template.ShiftStarts, at, Zone);
        var table = await builder.BuildAsync(template, key, from, to, Zone, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            var version = (await db.ReportRuns.Where(x => x.TemplateId == template.Id && x.PeriodKey == key).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
            var label = $"v{version} · 模板版本 {template.Revision} · {(trigger == "schedule" ? "定时生成" : "手动生成：" + userName)}";
            // 文件名带随机后缀：库被还原或换库后目录中可能残留同名旧文件，绝不覆盖也不因此失败。
            var stem = $"{template.Id}_{Sanitize(key)}_v{version}_{Guid.NewGuid().ToString("N")[..8]}";
            var directory = Path.Combine(Root, template.Id.ToString()); Directory.CreateDirectory(directory);
            var xlsx = Path.Combine(directory, stem + ".xlsx"); var pdf = Path.Combine(directory, stem + ".pdf");
            if (File.Exists(xlsx) || File.Exists(pdf)) throw new IOException("归档文件已存在，拒绝覆盖");
            var xlsxHash = Write(xlsx, ReportExcel.Render(table, label, Zone));
            var pdfHash = Write(pdf, ReportPdf.Render(table, label, Zone, options.Value.ReportFontPath));
            var run = new ReportRun
            {
                TemplateId = template.Id, TemplateRevision = template.Revision, TemplateName = template.Name, AreaId = template.AreaId,
                PeriodKey = key, FromTs = from, ToTs = to, Version = version, Trigger = trigger, GeneratedBy = userName, GeneratedTs = ScadaTime.NowMs(),
                TemplateJson = JsonSerializer.Serialize(template, ReportTemplates.Json), DataJson = JsonSerializer.Serialize(table, ReportTemplates.Json),
                XlsxPath = xlsx, XlsxSha256 = xlsxHash, PdfPath = pdf, PdfSha256 = pdfHash
            };
            db.ReportRuns.Add(run);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = userId, UserName = userName, Ts = run.GeneratedTs, Action = $"生成报表：{template.Name} {key} v{version}", Result = "成功",
                TargetType = "ReportRun", Details = JsonSerializer.Serialize(new { templateId = template.Id, templateRevision = template.Revision, periodKey = key, version, trigger, xlsxSha256 = xlsxHash, pdfSha256 = pdfHash })
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) when (attempt < 5)
            {
                // 并发生成撞上 (模板, 周期, 版本) 唯一约束：删掉本次文件，换下一个版本号重试。
                db.ChangeTracker.Clear(); TryDelete(xlsx); TryDelete(pdf); continue;
            }
            if (template.EmailChannelId is int channelId && ReportTemplates.NotificationRecipients(template.EmailRecipients).Count > 0)
            {
                await dispatcher.EnqueueReportAsync(run.Id, channelId, template.EmailRecipients, ct);
                _ = Task.Run(async () => { try { await dispatcher.DrainAsync(CancellationToken.None); } catch (Exception ex) { logger.LogWarning(ex, "报表邮件投递失败"); } });
            }
            logger.LogInformation("已生成报表 {Template} {Period} v{Version}", template.Name, key, version);
            return run;
        }
    }

    // 读取归档文件并校验 SHA256；不一致说明被篡改或损坏，拒绝提供。
    public async Task<byte[]?> ReadVerifiedAsync(ReportRun run, string format, CancellationToken ct)
    {
        var (path, hash) = format == "pdf" ? (run.PdfPath, run.PdfSha256) : (run.XlsxPath, run.XlsxSha256);
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase) ? bytes : null;
    }

    private static string Write(string path, byte[] bytes)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); } catch (Exception) { /* 非关键 */ }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch (Exception) { }
    }

    private static string Sanitize(string key) => new(key.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
}

// 定时生成：每分钟检查启用定时的模板，为“上一个已结束且超过延迟的周期”生成一次（不回溯更早周期）。
public sealed class ReportScheduler(IServiceProvider services, IDbContextFactory<ConfigDbContext> factory, ILogger<ReportScheduler> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(long now, CancellationToken ct)
    {
        var archive = services.GetRequiredService<ReportArchiveService>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var templates = await db.ReportTemplates.AsNoTracking().Where(x => x.ScheduleEnabled).ToListAsync(ct);
        var generated = 0;
        foreach (var template in templates)
        {
            try
            {
                var delay = template.ScheduleDelayMin * 60_000L;
                var current = ReportPeriods.Range(template.Period, template.ShiftStarts, now - delay, archive.Zone);
                var previous = ReportPeriods.Range(template.Period, template.ShiftStarts, current.From - 1, archive.Zone);
                if (now < previous.To + delay) continue;
                if (await db.ReportRuns.AnyAsync(x => x.TemplateId == template.Id && x.PeriodKey == previous.Key && x.Trigger == "schedule", ct)) continue;
                await archive.GenerateAsync(template, previous.From, "schedule", null, "系统定时", ct);
                generated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "定时生成报表 {Template} 失败", template.Name); }
        }
        return generated;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try { await RunOnceAsync(ScadaTime.NowMs(), stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "报表定时任务异常"); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
