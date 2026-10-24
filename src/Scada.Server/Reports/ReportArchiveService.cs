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
    ReportSigner signer,
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
            var xlsxBytes = ReportExcel.Render(table, label, Zone); var pdfBytes = ReportPdf.Render(table, label, Zone, options.Value.ReportFontPath);
            var xlsxHash = Write(xlsx, xlsxBytes);
            var pdfHash = Write(pdf, pdfBytes);
            // 数字签名与归档同一次写入；签名失败（证书不可用）不阻止归档，但记录原因并写审计，验证时显示“未签名”。
            string? xlsxSig = null, pdfSig = null, thumbprint = null, subject = null, signError = null; long? signedTs = null;
            if (signer.Enabled)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    (xlsxSig, thumbprint, subject) = signer.Sign(xlsxBytes, now);
                    (pdfSig, _, _) = signer.Sign(pdfBytes, now);
                    signedTs = now.ToUnixTimeMilliseconds();
                }
                catch (Exception ex) { signError = ("签名失败：" + ex.Message)[..Math.Min(500, ("签名失败：" + ex.Message).Length)]; logger.LogError(ex, "报表签名失败"); }
            }
            var run = new ReportRun
            {
                TemplateId = template.Id, TemplateRevision = template.Revision, TemplateName = template.Name, AreaId = template.AreaId,
                PeriodKey = key, FromTs = from, ToTs = to, Version = version, Trigger = trigger, GeneratedBy = userName, GeneratedTs = ScadaTime.NowMs(),
                TemplateJson = JsonSerializer.Serialize(template, ReportTemplates.Json), DataJson = JsonSerializer.Serialize(table, ReportTemplates.Json),
                XlsxPath = xlsx, XlsxSha256 = xlsxHash, PdfPath = pdf, PdfSha256 = pdfHash,
                XlsxSignature = xlsxSig, PdfSignature = pdfSig, SignerThumbprint = thumbprint, SignerSubject = subject, SignedTs = signedTs, SignatureError = signError
            };
            db.ReportRuns.Add(run);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = userId, UserName = userName, Ts = run.GeneratedTs, Action = $"生成报表：{template.Name} {key} v{version}", Result = signError is null ? (signedTs is null ? "成功（未启用签名）" : "成功，已签名") : $"成功，但{signError}",
                TargetType = "ReportRun", Details = JsonSerializer.Serialize(new { templateId = template.Id, templateRevision = template.Revision, periodKey = key, version, trigger, xlsxSha256 = xlsxHash, pdfSha256 = pdfHash, signerThumbprint = thumbprint })
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
    public async Task<byte[]?> ReadVerifiedAsync(ReportRun run, string format, CancellationToken ct) => (await ReadCheckedAsync(run, format, ct)).Bytes;

    // 读取并同时校验哈希与数字签名。Bytes 仅在哈希一致且（已签名时）签名有效时返回。
    public async Task<(byte[]? Bytes, bool Exists, bool HashOk, SignatureCheck Signature)> ReadCheckedAsync(ReportRun run, string format, CancellationToken ct)
    {
        var (path, hash, signature) = format == "pdf" ? (run.PdfPath, run.PdfSha256, run.PdfSignature) : (run.XlsxPath, run.XlsxSha256, run.XlsxSignature);
        if (run.PurgedTs is not null || !File.Exists(path)) return (null, false, false, new(signature is not null, false, "归档文件不存在", null, null, null, false, false, false));
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var hashOk = string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase);
        var check = signer.Verify(bytes, signature, run.SignerThumbprint);
        var ok = hashOk && (!check.Present || check.Valid);
        return (ok ? bytes : null, true, hashOk, check);
    }

    // 保留期清理：只删除超过保留天数的归档文件，记录保留并标记 PurgedTs；每批写一条系统审计。默认不删除。
    public async Task<int> PurgeExpiredAsync(long now, CancellationToken ct)
    {
        var days = options.Value.ReportArchiveRetentionDays;
        if (days <= 0) return 0;
        var cutoff = now - days * 86_400_000L;
        await using var db = await factory.CreateDbContextAsync(ct);
        var expired = await db.ReportRuns.Where(x => x.PurgedTs == null && x.GeneratedTs < cutoff).OrderBy(x => x.Id).Take(500).ToListAsync(ct);
        if (expired.Count == 0) return 0;
        foreach (var run in expired) { TryDelete(run.XlsxPath); TryDelete(run.PdfPath); run.PurgedTs = now; }
        db.OperationLogs.Add(new OperationLog
        {
            UserName = "system", Ts = now, Action = $"报表归档保留期清理：删除 {expired.Count} 份报表文件", Result = "成功", TargetType = "ReportRun",
            Details = JsonSerializer.Serialize(new { retentionDays = days, runs = expired.Select(x => new { id = x.Id, templateName = x.TemplateName, periodKey = x.PeriodKey, version = x.Version, xlsxSha256 = x.XlsxSha256, pdfSha256 = x.PdfSha256 }) })
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("报表归档保留期清理：{Count} 份", expired.Count);
        return expired.Count;
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

// 定时生成：启动时及每分钟检查启用定时的模板，补齐最近 N 个已结束且超过延迟的周期（停机回补），并执行归档保留期清理。
public sealed class ReportScheduler(IServiceProvider services, IDbContextFactory<ConfigDbContext> factory, ILogger<ReportScheduler> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(long now, CancellationToken ct)
    {
        var archive = services.GetRequiredService<ReportArchiveService>();
        var maxPeriods = Math.Clamp(services.GetRequiredService<IOptions<ScadaSettings>>().Value.ReportBackfillMaxPeriods, 1, 366);
        await using var db = await factory.CreateDbContextAsync(ct);
        var templates = await db.ReportTemplates.AsNoTracking().Where(x => x.ScheduleEnabled).ToListAsync(ct);
        var generated = 0;
        foreach (var template in templates)
        {
            try
            {
                // 找出最近 maxPeriods 个已结束（且过了延迟）的周期，从旧到新补齐缺失的定时版本。
                // 停机期间错过的周期在启动后自动回补；早于“开启定时”时刻结束的周期不回补。
                var delay = template.ScheduleDelayMin * 60_000L;
                var since = template.ScheduleSinceTs ?? template.UpdatedTs;
                var due = new List<(string Key, long From, long To)>();
                var cursor = ReportPeriods.Range(template.Period, template.ShiftStarts, now - delay, archive.Zone);
                while (due.Count < maxPeriods)
                {
                    cursor = ReportPeriods.Range(template.Period, template.ShiftStarts, cursor.From - 1, archive.Zone);
                    if (cursor.To <= since) break;
                    if (now >= cursor.To + delay) due.Add(cursor);
                }
                due.Reverse();
                for (var i = 0; i < due.Count; i++)
                {
                    var period = due[i];
                    if (await db.ReportRuns.AnyAsync(x => x.TemplateId == template.Id && x.PeriodKey == period.Key && (x.Trigger == "schedule" || x.Trigger == "backfill"), ct)) continue;
                    await archive.GenerateAsync(template, period.From, i == due.Count - 1 ? "schedule" : "backfill", null, "系统定时", ct);
                    generated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "定时生成报表 {Template} 失败", template.Name); }
        }
        try { await archive.PurgeExpiredAsync(now, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "报表归档保留期清理失败"); }
        return generated;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!services.GetRequiredService<IOptions<ScadaSettings>>().Value.ReportSchedulerEnabled) return;
        // 启动后先跑一次：回补停机期间错过的周期。
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ContinueWith(_ => { });
        if (!stoppingToken.IsCancellationRequested)
            try { await RunOnceAsync(ScadaTime.NowMs(), stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "报表定时任务异常"); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                if (services.GetService<Scada.Core.Interfaces.INodeRole>() is { IsActive: false }) continue; // 备机不生成、不投递报表
                else try { await RunOnceAsync(ScadaTime.NowMs(), stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "报表定时任务异常"); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
