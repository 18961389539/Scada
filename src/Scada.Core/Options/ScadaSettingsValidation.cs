using System.Net;
using Scada.Core.Versioning;

namespace Scada.Core.Options;

/// <summary>
/// 站点配置的启动期校验。此前 102 个配置项没有任何范围约束：0 超时、负保留天数、
/// 备份目标缺路径这类错误只会在第一次用到时炸，且往往表现为业务异常而不是"配置写错了"。
///
/// 这里只做纯函数校验（不依赖 Microsoft.Extensions.Options，Core 保持零 NuGet 依赖），
/// 由宿主在启动时调用一次：有错直接失败，并把全部问题一次列出来。
/// </summary>
public static class ScadaSettingsValidation
{
    public static IReadOnlyList<Models.ValidationIssue> Validate(ScadaSettings settings)
    {
        var issues = new List<Models.ValidationIssue>();

        void Fail(string code, string field, string message) => issues.Add(new(code, field, message));
        void Int(string field, int value, int min, int max, string message)
        {
            if (value < min || value > max) Fail($"settings.{field}", field, message);
        }
        void Long(string field, long value, long min, long max, string message)
        {
            if (value < min || value > max) Fail($"settings.{field}", field, message);
        }
        void Double(string field, double value, double min, double max, string message)
        {
            if (!double.IsFinite(value) || value < min || value > max) Fail($"settings.{field}", field, message);
        }

        if (string.IsNullOrWhiteSpace(settings.SiteName) || settings.SiteName.Length > 100)
            Fail("settings.siteName", nameof(settings.SiteName), "站点名称须为 1～100 个字符");

        foreach (var (field, value) in new (string, string?)[]
                 {
                     (nameof(settings.ConfigDbPath), settings.ConfigDbPath),
                     (nameof(settings.HistoryRoot), settings.HistoryRoot),
                     (nameof(settings.BackupRoot), settings.BackupRoot),
                     (nameof(settings.DiagnosticsRoot), settings.DiagnosticsRoot),
                     (nameof(settings.ReportsRoot), settings.ReportsRoot)
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) Fail($"settings.{field}", field, "路径不能为空");
        }

        // 历史与聚合：0 并发或 0 超时会让查询永久排队/立即失败；保留天数为负会让清理任务删错区间。
        Int(nameof(settings.HistoryRetentionMonths), settings.HistoryRetentionMonths, 1, 1200, "原始历史保留月数须在 1～1200 之间");
        Int(nameof(settings.SpaceReclaimVacuumTimeoutSeconds), settings.SpaceReclaimVacuumTimeoutSeconds, 5, 600, "空间回收 VACUUM 超时须在 5～600 秒之间");
        Int(nameof(settings.HistoryQueryMaxConcurrent), settings.HistoryQueryMaxConcurrent, 1, 64, "历史查询并发须在 1～64 之间");
        Int(nameof(settings.HistoryQueryTimeoutSeconds), settings.HistoryQueryTimeoutSeconds, 1, 3600, "历史查询超时须在 1～3600 秒之间");
        Long(nameof(settings.HistoryQueryMaxReadPoints), settings.HistoryQueryMaxReadPoints, 1, long.MaxValue, "历史单次查询点数上限必须大于 0");
        Long(nameof(settings.HistoryAnalysisMaxPoints), settings.HistoryAnalysisMaxPoints, 1, settings.HistoryQueryMaxReadPoints, "分析点数上限须大于 0 且不超过单次查询上限");
        Int(nameof(settings.ArchiveFlushIntervalMs), settings.ArchiveFlushIntervalMs, 10, 600_000, "归档刷盘间隔须在 10～600000ms 之间");
        Int(nameof(settings.ArchiveBatchSize), settings.ArchiveBatchSize, 1, 1_000_000, "归档批量须在 1～1000000 之间");
        Long(nameof(settings.HistorySpoolSegmentBytes), settings.HistorySpoolSegmentBytes, 1, long.MaxValue, "暂存分段大小必须大于 0");
        if (settings.HistorySpoolMaxBytes < settings.HistorySpoolSegmentBytes)
            Fail("settings.HistorySpoolMaxBytes", nameof(settings.HistorySpoolMaxBytes), "暂存总容量不能小于单个分段大小");
        Long(nameof(settings.HistorySpoolMinFreeMb), settings.HistorySpoolMinFreeMb, 0, long.MaxValue, "暂存最小可用空间不能为负");
        Long(nameof(settings.HistorySpoolWarnBytes), settings.HistorySpoolWarnBytes, 0, long.MaxValue, "暂存告警阈值不能为负");
        Int(nameof(settings.Agg5MinRetentionDays), settings.Agg5MinRetentionDays, 0, 36_500, "5 分钟聚合保留天数须在 0～36500 之间");
        Int(nameof(settings.AggHourRetentionDays), settings.AggHourRetentionDays, 0, 36_500, "小时聚合保留天数须在 0～36500 之间");
        Double(nameof(settings.HistoryCapacityWarnDays), settings.HistoryCapacityWarnDays, 0, 3650, "容量预警天数须在 0～3650 之间");
        Double(nameof(settings.DiskFreeWarnGb), settings.DiskFreeWarnGb, 0, 1_000_000, "磁盘余量预警不能为负");
        Int(nameof(settings.BusBacklogWarn), settings.BusBacklogWarn, 1, int.MaxValue, "总线积压告警必须大于 0");

        // 备份：保留份数为 0 会在下一次定时备份后立刻删掉刚生成的包。
        Int(nameof(settings.BackupRetentionDays), settings.BackupRetentionDays, 0, 36_500, "备份保留天数不能为负");
        Int(nameof(settings.BackupKeepLatest), settings.BackupKeepLatest, 1, 1000, "备份至少保留 1 份");
        Int(nameof(settings.FullBackupIntervalHours), settings.FullBackupIntervalHours, 0, 87_600, "全量备份间隔须在 0～87600 小时之间");
        for (var i = 0; i < settings.BackupTargets.Count; i++)
        {
            var target = settings.BackupTargets[i];
            var field = $"{nameof(settings.BackupTargets)}[{i}]";
            if (string.IsNullOrWhiteSpace(target.Name)) Fail("settings.backup.name", field, "备份目标名称不能为空");
            if (target.KeepLatest < 1) Fail("settings.backup.keepLatest", field, "异地目标至少保留 1 份");
            if (string.Equals(target.Type, "Directory", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(target.Path)) Fail("settings.backup.path", field, "目录型备份目标必须填写 Path");
            }
            else if (string.Equals(target.Type, "S3", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(target.Endpoint) || string.IsNullOrWhiteSpace(target.Bucket))
                    Fail("settings.backup.s3", field, "S3 备份目标必须填写 Endpoint 与 Bucket");
            }
            else
            {
                Fail("settings.backup.type", field, $"备份目标类型不支持：{target.Type}（仅 Directory 或 S3）");
            }
        }

        // 报警与通知：队列/阈值取 0 会让洪泛治理与通知静默失效。
        Int(nameof(settings.AlarmEventRetentionDays), settings.AlarmEventRetentionDays, 0, 36_500, "报警记录保留天数不能为负");
        Int(nameof(settings.OperationLogRetentionDays), settings.OperationLogRetentionDays, 0, 36_500, "操作日志保留天数不能为负");
        Int(nameof(settings.AlarmWebhookCooldownSec), settings.AlarmWebhookCooldownSec, 0, 86_400, "外发冷却时间须在 0～86400 秒之间");
        Int(nameof(settings.AlarmWebhookTimeoutMs), settings.AlarmWebhookTimeoutMs, 100, 600_000, "外发超时须在 100～600000ms 之间");
        Int(nameof(settings.AlarmWebhookQueueSize), settings.AlarmWebhookQueueSize, 1, 1_000_000, "外发队列长度必须大于 0");
        Int(nameof(settings.AlarmShelveMaxMinutes), settings.AlarmShelveMaxMinutes, 1, 525_600, "暂挂上限须在 1～525600 分钟之间");
        Int(nameof(settings.AlarmChatterCount), settings.AlarmChatterCount, 1, 1000, "抖动判定次数必须大于 0");
        Int(nameof(settings.AlarmChatterWindowSec), settings.AlarmChatterWindowSec, 1, 86_400, "抖动窗口须在 1～86400 秒之间");
        Int(nameof(settings.AlarmFloodThreshold), settings.AlarmFloodThreshold, 1, 1_000_000, "洪水阈值必须大于 0");
        Int(nameof(settings.AlarmStandingHours), settings.AlarmStandingHours, 1, 8760, "长期驻留判定须在 1～8760 小时之间");
        Int(nameof(settings.AlarmEscalationScanSec), settings.AlarmEscalationScanSec, 1, 3600, "升级扫描周期须在 1～3600 秒之间");
        Int(nameof(settings.AlarmRollupIntervalSec), settings.AlarmRollupIntervalSec, 10, 86_400, "报警聚合周期须在 10～86400 秒之间");
        Int(nameof(settings.AlarmRollupBackfillDays), settings.AlarmRollupBackfillDays, 0, 3650, "报警聚合回补天数须在 0～3650 之间");
        Int(nameof(settings.AlarmRollupRecomputeHours), settings.AlarmRollupRecomputeHours, 0, 8760, "报警聚合重算窗口须在 0～8760 小时之间");
        Int(nameof(settings.AlarmAnalyticsRawMaxDays), settings.AlarmAnalyticsRawMaxDays, 0, 3650, "报警明细分析天数须在 0～3650 之间");
        Int(nameof(settings.AlarmAnalyticsMaxRows), settings.AlarmAnalyticsMaxRows, 1, 10_000_000, "报警分析行数上限必须大于 0");
        Int(nameof(settings.NotificationMaxAttempts), settings.NotificationMaxAttempts, 1, 20, "通知最大尝试次数须在 1～20 之间");
        Int(nameof(settings.NotificationRetryBaseSec), settings.NotificationRetryBaseSec, 1, 3600, "通知首次重试间隔须在 1～3600 秒之间");
        Int(nameof(settings.NotificationRetryMaxSec), settings.NotificationRetryMaxSec, settings.NotificationRetryBaseSec, 86_400, "通知重试上限不能小于首次间隔");
        Int(nameof(settings.NotificationOutboxRetentionDays), settings.NotificationOutboxRetentionDays, 0, 36_500, "发件箱保留天数不能为负");

        // OEE / 计算任务：采样周期 0 会让采集循环变成忙等。
        Int(nameof(settings.OeeSampleIntervalSec), settings.OeeSampleIntervalSec, 1, 3600, "OEE 采样周期须在 1～3600 秒之间");
        Int(nameof(settings.OeeRollupIntervalSec), settings.OeeRollupIntervalSec, 10, 86_400, "OEE 聚合周期须在 10～86400 秒之间");
        Int(nameof(settings.OeeRollupBackfillDays), settings.OeeRollupBackfillDays, 0, 3650, "OEE 聚合回补天数须在 0～3650 之间");
        Int(nameof(settings.OeeRollupRecomputeHours), settings.OeeRollupRecomputeHours, 0, 8760, "OEE 聚合重算窗口须在 0～8760 小时之间");
        Int(nameof(settings.OeeRollupMinuteRetentionDays), settings.OeeRollupMinuteRetentionDays, 0, 3650, "OEE 分钟聚合保留天数须在 0～3650 之间");
        Int(nameof(settings.OeeRollupHourlyRetentionDays), settings.OeeRollupHourlyRetentionDays, 0, 3650, "OEE 小时聚合保留天数须在 0～3650 之间");
        Int(nameof(settings.OeeRollupDailyRetentionDays), settings.OeeRollupDailyRetentionDays, 0, 3650, "OEE 日聚合保留天数须在 0～3650 之间");
        Int(nameof(settings.OeeAnalyticsRawMaxDays), settings.OeeAnalyticsRawMaxDays, 0, 3650, "OEE 明细分析天数须在 0～3650 之间");
        Int(nameof(settings.OeeAlarmDowntimeMinLevel), settings.OeeAlarmDowntimeMinLevel, 0, 2, "OEE 停机报警起始等级须在 0～2 之间");
        Int(nameof(settings.CalcStatePersistSec), settings.CalcStatePersistSec, 1, 3600, "计算状态落盘周期须在 1～3600 秒之间");
        Int(nameof(settings.CalcScriptTimeoutMs), settings.CalcScriptTimeoutMs, 1, 60_000, "受限脚本超时须在 1～60000ms 之间");
        Long(nameof(settings.CalcScriptMaxSteps), settings.CalcScriptMaxSteps, 1, 100_000_000, "受限脚本步数上限须在 1～1 亿之间");
        Int(nameof(settings.ReportArchiveRetentionDays), settings.ReportArchiveRetentionDays, 0, 36_500, "报表归档保留天数不能为负");
        Int(nameof(settings.ReportBackfillMaxPeriods), settings.ReportBackfillMaxPeriods, 0, 365, "报表回补周期数须在 0～365 之间");

        // 实时推送与配置刷新：0 间隔会打满 CPU；刷新为 0 则配置永远不生效。
        Int(nameof(settings.PushIntervalMs), settings.PushIntervalMs, 50, 60_000, "实时推送间隔须在 50～60000ms 之间");
        Int(nameof(settings.CommandTimeoutMs), settings.CommandTimeoutMs, 1, 600_000, "命令超时须在 1～600000ms 之间");
        Int(nameof(settings.ConfigRefreshSeconds), settings.ConfigRefreshSeconds, 1, 3600, "配置刷新周期须在 1～3600 秒之间");
        Int(nameof(settings.LogRetainedFiles), settings.LogRetainedFiles, 1, 10_000, "日志保留文件数须在 1～10000 之间");
        Int(nameof(settings.LogFileSizeLimitMb), settings.LogFileSizeLimitMb, 1, 10_240, "单日志文件上限须在 1～10240MB 之间");
        Int(nameof(settings.WatchdogStallSeconds), settings.WatchdogStallSeconds, 0, 86_400, "看门狗停滞阈值须在 0～86400 秒之间");
        Int(nameof(settings.ClockCheckIntervalMinutes), settings.ClockCheckIntervalMinutes, 5, 1_440, "时钟校验周期须在 5～1440 分钟之间");
        Int(nameof(settings.ClockSkewWarnMs), settings.ClockSkewWarnMs, 1_000, 3_600_000, "时钟偏差告警阈值须在 1000～3600000ms 之间");

        // 流量治理与传输：0 是合法的"关闭"，但负数会被下游 Math.Clamp 静默改写，
        // 配错的人看不出自己写错了；上传上限过小会让工程包导入直接 413。
        Int(nameof(settings.ApiRateLimitPerMinute), settings.ApiRateLimitPerMinute, 0, 1_000_000, "登录用户每分钟请求上限须在 0～1000000 之间（0 关闭）");
        Int(nameof(settings.AnonymousRateLimitPerMinute), settings.AnonymousRateLimitPerMinute, 0, 1_000_000, "匿名请求每分钟上限须在 0～1000000 之间（0 关闭）");
        Int(nameof(settings.ReadRequestTimeoutSeconds), settings.ReadRequestTimeoutSeconds, 0, 3600, "读请求超时须在 0～3600 秒之间（0 关闭）");
        Int(nameof(settings.WriteRequestTimeoutSeconds), settings.WriteRequestTimeoutSeconds, 0, 3600, "写请求超时须在 0～3600 秒之间（0 关闭）");
        Int(nameof(settings.MaxUploadMb), settings.MaxUploadMb, 1, 4096, "单请求体上限须在 1～4096MB 之间");

        if (!string.IsNullOrWhiteSpace(settings.ShiftTimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(settings.ShiftTimeZone, out _))
            Fail("settings.shiftTimeZone", nameof(settings.ShiftTimeZone), $"时区名称无法识别：{settings.ShiftTimeZone}");

        // 桌面客户端集中运维：心跳/离线阈值配错会被下游 clamp 静默改写；版本号写错会让门禁
        // 永远不生效（比较时 fail-open），必须在启动时暴露，而不是等到现场发现"配了拦不住"。
        var desktop = settings.DesktopClient;
        Int("DesktopClient.HeartbeatSeconds", desktop.HeartbeatSeconds, 10, 600, "桌面客户端心跳周期须在 10～600 秒之间");
        Int("DesktopClient.OnlineThresholdSeconds", desktop.OnlineThresholdSeconds, 30, 600, "桌面客户端离线阈值须在 30～600 秒之间");

        var minimumOk = AppVersionRule.TryParse(desktop.MinimumVersion, out var minimum);
        var latestOk = AppVersionRule.TryParse(desktop.LatestVersion, out var latest);
        if (!string.IsNullOrWhiteSpace(desktop.MinimumVersion) && !minimumOk)
            Fail("settings.desktopClient.minimumVersion", "DesktopClient.MinimumVersion", $"最低版本号无法识别：{desktop.MinimumVersion}");
        if (!string.IsNullOrWhiteSpace(desktop.LatestVersion) && !latestOk)
            Fail("settings.desktopClient.latestVersion", "DesktopClient.LatestVersion", $"最新版本号无法识别：{desktop.LatestVersion}");
        if (minimumOk && latestOk && latest < minimum)
            Fail("settings.desktopClient.versionOrder", "DesktopClient.LatestVersion", "最新版本不能低于最低版本");
        if (!string.IsNullOrWhiteSpace(desktop.DownloadUrl)
            && (!Uri.TryCreate(desktop.DownloadUrl.Trim(), UriKind.Absolute, out var download)
                || (download.Scheme != Uri.UriSchemeHttp && download.Scheme != Uri.UriSchemeHttps)))
            Fail("settings.desktopClient.downloadUrl", "DesktopClient.DownloadUrl", "下载地址须为 http/https 绝对地址");

        // 反向代理：开了却不列可信代理，ForwardedHeaders 中间件会忽略转发头，
        // 现场以为"配了"，实际限流仍按代理地址合并。
        if (settings.ReverseProxy.Enabled)
        {
            if (settings.ReverseProxy.KnownProxies.Length == 0)
                Fail("settings.reverseProxy.knownProxies", "ReverseProxy.KnownProxies",
                    "启用反向代理时至少要列出一个可信代理 IP");
            foreach (var proxy in settings.ReverseProxy.KnownProxies)
            {
                if (!IPAddress.TryParse(proxy?.Trim(), out _))
                    Fail("settings.reverseProxy.knownProxies", "ReverseProxy.KnownProxies", $"可信代理地址无法识别：{proxy}");
            }
        }

        return issues;
    }
}
