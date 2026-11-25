# 配置参考

> 由 `python tools/config-doc/generate.py` 从 `src/Scada.Core/Options/ScadaSettings.cs` 生成，请勿手改。
> CI 用 `--check` 校验文档与代码一致。

## 覆盖方式

优先级从低到高：

1. `src/Scada.Server/appsettings.json` 的 `Scada` 段；
2. 站点配置文件（`data/appsettings.Site.json`，由 `StartupPaths.ResolveSiteSettings` 定位）；
3. 环境变量，双下划线表示层级：`Scada__HistoryQueryMaxConcurrent=8`；
4. 命令行：`--Scada:HistoryQueryMaxConcurrent 8`。

**appsettings 的改动默认需要重启进程**；例外是 `Scada:DesktopClient`（桌面客户端版本门禁与心跳），它用 `IOptionsMonitor` 读取，保存后一个心跳周期内生效。`ConfigRefreshService` 刷新的是数据库里的业务配置，不是这个文件。

启动日志会打印「非默认配置 N 项」，用于对齐不同机器之间的差异（敏感项不落日志）。

## TLS 与反向代理

默认只监听明文 HTTP（`http://0.0.0.0:5080`）。局域网内可直接嗅探到 8 小时有效期的 JWT，
生产环境必须二选一：

1. **反向代理终结 TLS**：SCADA 放在 Nginx / IIS ARR 之后，保持 `Kestrel:Endpoints:Http` 不变；

   代理终结时必须同时启用 `Scada:ReverseProxy` 并列出可信代理。否则服务端只看得到代理地址：
   内部限流按「身份 + 终端地址」分区，几十台终端会被合并成一个配额桶；IP 白名单、审计与日志也都会记成代理 IP。

   ```json
   "Scada": { "ReverseProxy": { "Enabled": true, "KnownProxies": ["10.0.0.5"] } }
   ```

   只信任这里列出的地址（同机代理填 `127.0.0.1`），伪造的 `X-Forwarded-For` 不会改变限流分区。客户端直连（没有代理）时保持 `Enabled: false`；
   请求带 `X-Forwarded-For` 却未启用时，服务端会每 5 分钟在日志里提示一次。
2. **Kestrel 直接终结 TLS**：

```json
"Kestrel": {
  "Endpoints": {
    "Http":  { "Url": "http://0.0.0.0:5080" },
    "Https": { "Url": "https://0.0.0.0:5443" }
  },
  "Certificates": {
    "Default": { "Path": "C:/certs/scada.pfx", "Password": "***" }
  }
}
```

或用环境变量 `ASPNETCORE_Kestrel__Certificates__Default__Path` / `__Password` 指定 PFX。
两种方式下 HTTPS 请求都会带 HSTS 头（`Middleware/SecurityHeaders.cs`）；
未检测到 https 端点时，非 Development 环境启动会记录一条告警。

## 配置平面

配置分成三层，不要把页面可改且会影响按日切分或历史分桶的项放进站点文件：

1. **部署平面**：站点配置文件（`data/appsettings.Site.json`）与进程启动参数。存放路径、JWT、监听地址、`Ha:SharedKey` 等。还原时保留，不进配置快照。
2. **厂级运营组态**：配置库（随完整备份和主备配置复制）。工厂日历（班次时区）在此层。写入后历史、报表、OEE、计算任务和通知会重读。
3. **运行时**：运行库与历史库。历史按测点编号存储，删除组态时不做在线大清理。

详见 [configuration-planes.md](configuration-planes.md)。

## 标量配置

「配置文件中」列表示该键是否显式出现在 `appsettings.json` 里；
未出现不代表无效，只是使用下面的默认值。标 **敏感** 的项不会出现在启动日志中。

### General

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `SiteName` | `"SCADA"` | ✓ | — |
| `DemoMode` | `（无）` | ✓ | Demo UI conveniences require an explicit opt-in, independently of data seeding. |
| `FilterPublishedScreensByArea` | `（无）` | — | 已发布画面目录是否只显示账号可见区域的画面。默认关闭，保持跨区域画面目录的既有行为。 |
| `RequirePasswordChangeOnFirstLogin` | `true` | ✓ | 首装管理员与管理员代建/重置的口令，必须由本人在首次登录后改掉。 批量交付、演示与自动化环境可以显式关闭（关闭后出厂口令会一直有效）。 |
| `OpenBrowserOnStart` | `（无）` | — | 交互式双击启动后自动打开默认浏览器；未显式配置时仅生产环境生效，开发与自动化不弹窗。 |
| `ConfigDbPath` | `"data/scada.db"` | ✓ | — |
| `DriverPluginRoot` | `（无）` | — | 外部驱动插件目录。为空时（默认）不加载任何外部程序集。 配置目录后仍须把 DLL 的 SHA-256 写入 DriverPluginAllowSha256；插件自带的托管/非托管依赖 也要逐一并入允许列表，未列入的程序集一律拒绝（只校验主文件等于给替换依赖留后门）。 覆盖已有内置协议还须写入 DriverPluginOverrideProtocols；IEC 61850 无内置实现，只需通过允许列表。 |
| `RuntimeDbPath` | `""` | — | 空字符串表示与配置库同目录的 runtime.db。报警、命令账本、网关收件箱和 OEE 采样写在这里。 |
| `OpcPkiRoot` | `（无）` | — | **敏感** — |
| `DiskFreeWarnGb` | `5` | ✓ | 运维告警阈值：低于/高于这些值时 /health 转为 degraded 并写日志。 |
| `FullBackupIntervalHours` | `168` | ✓ | 定时全量备份间隔（小时）；0 表示关闭，默认每周一次。 |
| `SpaceReclaimVacuumTimeoutSeconds` | `60` | — | 一次性 VACUUM 的命令超时（秒）。维护任务在历史查询空闲时对尚未 incremental 的老库执行；超时或锁冲突则跳过。 |
| `OperationLogRetentionDays` | `365` | — | 操作/审计日志保留天数。到期删除不可恢复；0 表示不自动清理。 |
| `ShiftTimeZone` | `（无）` | — | 工厂日历（班次时区）的启动回退值。页面与 API 读写配置库中的厂级组态；本键只用于首次种子和测试。 站点文件不要再写入此项。未配置时按服务器本地时区切日，历史小时桶仍按 UTC 整点（既有整点/半点回补规则见 HistoryDbFactory）。 |
| `PushIntervalMs` | `500` | ✓ | — |
| `TagImportMaxRows` | `50000` | — | 测点 Excel 导入的每张工作表最大数据行数，防止超大文件长时间占用内存与配置库写锁。 |
| `CollectorWorkerStopGraceMs` | `5000` | — | 停止一台设备时等待在途采集 I/O 退出的时长。超时后任务进入"已放弃"名单， 仍不退出则由 CollectorStuckIoGraceSeconds 强制关链路兜底。 |
| `CollectorStuckIoGraceSeconds` | `60` | — | 旧采集 I/O 被判定卡死的时长：超过后强制关闭驱动与共享链路、放弃该任务并允许重启采集。 0 表示永不放弃（保持旧行为，只提示不重启）。 |
| `CollectorBusDisposeGraceMs` | `5000` | — | 共享总线释放的最长等待：驱动卡在读操作里会一直占着总线，超时后强制拆链，避免停机被拖死。 |
| `CollectorMinWorkerThreads` | `（无）` | — | 采集线程池最小线程数下限（0 不调整）。阻塞式驱动事务按线程计价， 设备数量大时抬高该值可避免线程注入延迟造成的采集抖动。 |
| `ConfigRefreshSeconds` | `10` | ✓ | — |
| `AllowPublicCollectorTargets` | `（无）` | — | — |
| `LocalCollectionEnabled` | `true` | — | 本机进程内采集。默认开启，保持一体化部署。 设为 false 后 Server 不再连接现场设备；启用且未分配网关的设备会在保存、浏览与控制时被拒绝， 采集改走已登记的 Gateway.Host（可与中心同机回环）。这不是驱动插件进程隔离。 更改后需重启中心。回环 HTTP 还须同时打开 Gateway:AllowLoopbackHttp 与网关的 AllowLoopbackHttp。 |
| `SeedDemoData` | `（无）` | ✓ | — |
| `DesktopClient` | `new()` | ✓ | 桌面客户端集中运维：在线心跳阈值与版本门禁。 |
| `ReverseProxy` | `new()` | ✓ | 反向代理：默认关闭，只信任显式列出的代理地址。 |

### History

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `HistoryRoot` | `"data/history"` | ✓ | — |
| `HistoryRetentionMonths` | `6` | ✓ | **敏感** 原始月库保留月数。按 UTC 月删除截止月之前的完整月库；运行时小于 1 时按 1 处理。 |
| `HistorySynchronous` | `HistorySyncMode.Normal` | — | 历史写入的 SQLite synchronous 级别。默认 NORMAL：进程崩溃最多丢最后一个未提交事务， 主机掉电还可能回滚已提交但未 fsync 的事务；需要更强保证时改 Full。 配置库/运行库固定 FULL（见 SqlitePragmaInterceptor），不受此设置影响。 |
| `Agg5MinRetentionDays` | `365` | ✓ | — |
| `AggHourRetentionDays` | `（无）` | ✓ | — |
| `HistorySizeWarnGb` | `（无）` | ✓ | — |
| `HistoryCapacityWarnDays` | `7` | ✓ | — |
| `ArchiveFlushIntervalMs` | `1000` | ✓ | — |
| `ArchiveBatchSize` | `2000` | ✓ | — |
| `HistoryQueryMaxConcurrent` | `4` | ✓ | — |
| `HistoryQueryTimeoutSeconds` | `30` | ✓ | — |
| `HistoryQueryMaxReadPoints` | `2_000_000` | ✓ | — |
| `HistoryAnalysisMaxPoints` | `200_000` | ✓ | — |
| `HistorySpoolWarnBytes` | `64L * 1024 * 1024` | — | — |
| `HistorySpoolMaxBytes` | `256L * 1024 * 1024` | — | 历史暂存总容量与单段大小；满盘默认拒收，DropOldestOnFull=true 时删最旧完整段再写。 |
| `HistorySpoolSegmentBytes` | `16L * 1024 * 1024` | — | — |
| `HistorySpoolDropOldestOnFull` | `（无）` | — | — |
| `HistorySpoolMinFreeMb` | `512` | — | 暂存拒收门槛（MB）：可用空间低于此值才拒绝落盘；DiskFreeWarnGb 只负责告警。0 表示不检查。 |
| `AggregationEnabled` | `true` | ✓ | — |

### Alarm

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `AlarmWebhookUrl` | `（无）` | — | 报警外发：留空则只走站内推送。同测点同等级在冷却期内只发一次，恢复事件不受冷却限制。 |
| `AlarmWebhookMinLevel` | `AlarmLevel.Warning` | — | — |
| `AlarmWebhookCooldownSec` | `60` | — | — |
| `AlarmWebhookTimeoutMs` | `5000` | — | — |
| `AlarmWebhookQueueSize` | `500` | — | — |
| `AlarmShelveMaxMinutes` | `480` | — | 报警管理（ISA-18.2）：暂挂上限、抖动判定、洪水阈值（每 10 分钟）、长期驻留、升级扫描周期、班次时区。 |
| `AlarmChatterCount` | `3` | — | — |
| `AlarmChatterWindowSec` | `60` | — | — |
| `AlarmFloodThreshold` | `10` | — | — |
| `AlarmFloodAutoSuppress` | `true` | — | 洪水治理：进入洪水后自动抑制不高于该等级的报警（Critical 始终发声），洪水退出后自动释放； 首出标记含触发进入洪水的那条与窗口内最早一条（同一片段可能两条），便于定位根因。 |
| `AlarmFloodSuppressMaxLevel` | `AlarmLevel.Warning` | — | — |
| `AlarmFloodFirstOut` | `true` | — | — |
| `AlarmStandingHours` | `24` | — | — |
| `AlarmEscalationScanSec` | `15` | — | — |
| `AlarmEventRetentionDays` | `365` | ✓ | 已结束报警记录的保留天数：由每日维护任务清理；0 表示永久保留。统计预聚合不受影响。 |
| `AlarmRollupEnabled` | `true` | — | 报警统计分析：预聚合（小时/日）后台任务周期、首次回补天数、每轮重算最近小时数（吸收迟到的确认/恢复）、保留期； 原始明细分析（抖动/洪水/关联等）最长覆盖天数，超过时只对区间最后这么多天做明细分析，计数类指标用预聚合。 |
| `AlarmRollupIntervalSec` | `300` | — | — |
| `AlarmRollupBackfillDays` | `400` | — | — |
| `AlarmRollupRecomputeHours` | `48` | — | — |
| `AlarmRollupHourlyRetentionDays` | `90` | — | — |
| `AlarmRollupDailyRetentionDays` | `1095` | — | — |
| `AlarmAnalyticsRawMaxDays` | `31` | — | — |
| `AlarmAnalyticsMaxRows` | `500_000` | — | — |

### Notification

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `NotificationOutboxRetentionDays` | `30` | — | 发件箱中已发送/最终失败记录的保留天数。 |
| `NotificationMaxAttempts` | `5` | — | 通知发件箱：最多尝试次数、首次重试间隔（之后指数退避）与上限。 |
| `NotificationRetryBaseSec` | `30` | — | — |
| `NotificationRetryMaxSec` | `1800` | — | — |

### Oee

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `OeeEnabled` | `true` | — | OEE / 生产统计：采集周期、预聚合回补与保留、明细分析最长天数。 |
| `OeeSampleIntervalSec` | `15` | — | — |
| `OeeRollupEnabled` | `true` | — | — |
| `OeeRollupIntervalSec` | `300` | — | — |
| `OeeRollupBackfillDays` | `400` | — | — |
| `OeeRollupRecomputeHours` | `48` | — | — |
| `OeeRollupMinuteRetentionDays` | `14` | — | — |
| `OeeRollupHourlyRetentionDays` | `90` | — | — |
| `OeeRollupDailyRetentionDays` | `1095` | — | — |
| `OeeAnalyticsRawMaxDays` | `31` | — | — |
| `OeeAlarmDowntimeMinLevel` | `1` | — | 报警转停机候选：匹配的报警等级下限；0=提示起全部。 |

### Calc

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `CalcStatePersistSec` | `10` | — | 计算任务：运行状态落盘周期（断电最多丢失该时长的累计量）与脚本执行上限。 |
| `CalcScriptTimeoutMs` | `50` | — | — |
| `CalcScriptMaxSteps` | `100_000` | — | — |

### Report

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `ReportsRoot` | `"data/reports"` | — | 已生成报表归档目录（只追加，文件带 SHA256 校验）。 |
| `ReportFontPath` | `（无）` | — | PDF 中文字体文件（.ttf/.otf/.ttc）；为空时依次尝试常见系统字体。 |
| `ReportLocale` | `"zh-CN"` | — | 归档报表（班报/日报/月报）固定表头的语言；归档文件生成后不可修改，因此按系统配置而非查看人语言。 |
| `ReportArchiveRetentionDays` | `（无）` | — | 归档文件保留天数；0 表示永久保留（默认）。到期只删文件，记录保留，并写审计。 |
| `ReportBackfillMaxPeriods` | `3` | — | 服务停机后启动时，每个定时模板最多自动回补的已结束周期数（含最近一个）；1 表示不回补更早周期。 |
| `ReportSchedulerEnabled` | `true` | — | 关闭后台定时（测试或只读副本用）；手动生成不受影响。 |
| `ReportSigningEnabled` | `true` | — | 报表归档数字签名（PKCS#7/CMS 分离签名）。证书为 PFX；为空时使用 keys/report-signing.pfx，不存在则自动生成自签名证书（仅用于防篡改， 对外具有法律效力的签名请配置 CA 颁发的证书）。 |
| `ReportSigningCertPath` | `（无）` | — | — |
| `ReportSigningCertPassword` | `（无）` | — | **敏感** — |

### Backup

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `BackupRoot` | `"data/backups"` | ✓ | — |
| `BackupRetentionDays` | `30` | ✓ | — |
| `BackupKeepLatest` | `7` | ✓ | 完整备份保留：至少保留最近 N 份；超过 BackupRetentionDays 天且不在最近 N 份内的删除。异地目标按各自的 KeepLatest 保留。 |
| `BackupDrillEnabled` | `true` | ✓ | 每次定时/手动“备份并演练”后，把备份还原到临时目录并做完整性与数量核对。 |
| `BackupPassword` | `（无）` | — | **敏感** 备份包加密口令。配置后新备份写成 full_*.zip.enc（PBKDF2-SHA256 + AES-256-CBC + HMAC-SHA256）， 校验、演练与还原都要同一口令；留空则沿用明文 ZIP。包里含 Data Protection 密钥（可解出设备口令与 通知密钥）和全部用户口令哈希，凡需要把包拷贝出站点（发给厂商排查、进共享盘、随工单流转）都应设置。 旧明文包在设置口令后仍可校验与还原，不会被拒。 |
| `BackupPasswordPrevious` | `（无）` | — | **敏感** / <summary>口令轮换后仅用于打开旧加密包，不作新备份。</summary> |

### Maintenance

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `DiagnosticsRoot` | `"data/diagnostics"` | — | — |

### Realtime

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `BusBacklogWarn` | `10_000` | — | — |
| `RealtimeBusHistoryWaitMs` | `（无）` | — | 历史队列满时发布方（采集/计算线程）的等待上限（毫秒）。0 = 非阻塞，满即拒收并计入 HistoryRejected。 配成正数可换取"宁可延迟也不丢点"，但磁盘抖动会直接传导成采集延迟。 |

### Control

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `CommandTimeoutMs` | `5000` | ✓ | — |
| `ControlRequireConfirmByDefault` | `true` | ✓ | 未配置控制策略的测点是否也要求操作确认。默认要求；“确认”由服务端一次性凭据证明，不再信任客户端布尔。 |

### Log

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `LogRoot` | `（无）` | — | 日志：目录（默认程序目录下 logs）、保留文件数、单文件上限（MB，超过即滚动）。 |
| `LogRetainedFiles` | `30` | ✓ | — |
| `LogFileSizeLimitMb` | `50` | ✓ | — |

### Watchdog

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `WatchdogStallSeconds` | `180` | ✓ | 看门狗：关键循环停滞超过该秒数即记录并退出进程，由服务管理器（SCM/systemd）重启。0 关闭。 |
| `WatchdogCheckIntervalMs` | `15_000` | — | 看门狗巡检节拍（毫秒）。默认 15 秒；调小只让"发现停滞"更及时，不改变停滞判定阈值。 |

### Clock

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `ClockCheckEnabled` | `true` | — | 系统时钟偏差检测（SNTP）：只检测并写入 /health 告警，不修改系统时间，对时交给 W32Time / chrony。 无外网或 UDP 123 被策略拦截时静默跳过，不会产生健康告警；内网 NTP 请把 Server 换成实际地址。 |
| `ClockCheckServer` | `"time.windows.com"` | — | — |
| `ClockCheckIntervalMinutes` | `360` | — | — |
| `ClockSkewWarnMs` | `5_000` | — | — |

### Security

| 配置键 | 默认值 | 配置文件 | 说明 |
|---|---|---|---|
| `MetricsToken` | `（无）` | ✓ | **敏感** Prometheus 抓取令牌（Authorization: Bearer）。未配置时 /metrics 只接受有系统权限的登录令牌。 |
| `HealthAnonymous` | `（无）` | ✓ | 健康检查的匿名访问。/health/live 恒匿名且只回 {"status":"ok"}；/health 与 /api/health 默认要求登录。 完整快照含设备清单、测点数量、磁盘路径与 HA 节点，不该给未登录者看；但它同时被前端主布局、 总览、实时监控、系统页读取，所以门槛只抬到"已登录"，不额外要求系统管理权限。 需要接外部监控（Zabbix/Prometheus blackbox）匿名读取时显式开启。 |
| `HealthRequireSystem` | `（无）` | ✓ | 更严格的现场：完整快照只给"全区域系统管理"权限。开启后普通操作员的健康卡片会拿不到数据， 仅适合用外部监控 + /health/live 取代前端健康展示的部署方式。 |
| `SecurityHeaders` | `true` | ✓ | 安全响应头（CSP / nosniff / Referrer-Policy / X-Frame-Options）。默认开启； 前置反向代理已统一注入时可关闭，避免重复下发。 |
| `ApiRateLimitPerMinute` | `1200` | ✓ | 全局限流：单个登录用户每分钟的 API 请求上限（令牌桶）。0 关闭。 与 MCP、对外 REST 各自的限额相互独立，不共享配额。 |
| `AnonymousRateLimitPerMinute` | `120` | ✓ | 未登录请求按来源 IP 的每分钟上限，主要挡住登录爆破与路径扫描。0 关闭。 |
| `ReadRequestTimeoutSeconds` | `30` | ✓ | 请求超时（秒）：读请求与写请求分开，0 表示不启用（沿用 Kestrel 无限等待）。 超时返回 504 并计入日志，防止慢客户端长期占用连接。 |
| `WriteRequestTimeoutSeconds` | `120` | ✓ | — |
| `MaxUploadMb` | `200` | ✓ | 单个请求体上限（MB）。Kestrel 默认 30MB，而工程包导入、屏幕背景图与报警规则 Excel 都可能超过； 上限过高会让一次误上传占满内存，200MB 是"够用且不至于失控"的折中。 |

## 结构化配置

以下项是数组或嵌套对象，改法与上面的标量不同，单列说明。

| 配置键 | 类型 | 说明 |
|---|---|---|
| `DriverPluginAllowSha256` | `string[]` | — |
| `DriverPluginOverrideProtocols` | `string[]` | — |
| `BackupTargets` | `List<BackupTargetOptions>` | — |
| `CollectorTargetAllowlist` | `List<string>` | 采集侧出站目标策略：默认只允许环回与内网地址，避免把公网 IP 误配成设备后对外发起连接。 白名单支持精确 IP、CIDR（如 10.20.0.0/16）与 *.域 后缀；配置白名单后主机名也必须命中。 |

## 需要单独展开的段落

- `Scada:Telemetry`（`TelemetryOptions`）：OTLP 端点、采样比例、EF Core 埋点开关。
- `Scada:BackupTargets`（数组）：异地备份目标，`Type` 为 `Directory` 或 `S3`。
- `Scada:Ai`、`Mcp`、`External:*`、`Ha`、`Jwt`：各有独立段落与校验，见 `appsettings.json`。
