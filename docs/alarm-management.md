# 报警管理（ISA-18.2）

本文说明报警状态模型、暂挂/抑制/停用、处理时间线、通知分发与升级、抖动与洪水、KPI，以及各接口的权限。所有接口都套用区域授权（见 `authorization.md`），配置变更写字段级差分审计。

## 状态模型

| 状态 | 含义 | 实时视图 |
|---|---|---|
| `Unack` 未确认 | 报警活动、未确认 | 显示，发声 |
| `Acked` 已确认 | 报警活动、已确认 | 显示 |
| `RtnUnack` 已恢复未确认 | 已恢复但没人确认 | 显示，直到确认（7 天内，见下方限制） |
| `Normal` | 已恢复且已确认 | 只在历史中 |

叠加三个标记（报警照常入库，只是不发声、不外发）：

- **暂挂（Shelved）**：操作员按报警规则临时屏蔽。必须写原因，时长 1～`AlarmShelveMaxMinutes`（默认 480）分钟，到期自动解除；解除时仍活动且未确认的报警会重新报警（声光、外发）。暂挂状态持久化在 `AlarmShelve` 表，重启后保留。
- **抑制（Suppressed，suppressed by design）**：规则可引用一个条件测点，`WhenOff` = 条件测点为 0 时抑制（典型：设备运行信号=0，即停机），`WhenOn` = 为 1 时抑制。条件测点质量不好时**不抑制**（宁可多报）。抑制状态随条件变化，活动报警会记 `suppress/unsuppress` 并在解除时重新报警。
- **抖动（Chattering）**：同一规则在 `AlarmChatterWindowSec`（默认 60 秒）内触发达到 `AlarmChatterCount`（默认 3）次，后续触发标记为抖动，只记录不报警，提示工程上调整死区或延时。

**停用（Out of service）** 是规则级配置：停用期间不评估，活动报警以“停用：原因”结束；必须写原因，需配置权限，走差分审计。

### 抖动抑制参数

- `DelaySec` 触发延时（已有）、`RecoverDeadband` 恢复死区（已有）；
- 新增 `OffDelaySec` 恢复延时：恢复条件需持续该秒数才恢复，期间回到报警区重新计时。

## 处理记录（时间线）

`AlarmActivity` 按报警实例保存：触发（含暂挂/抑制/抖动标记）、确认（可带备注）、备注、暂挂/解除、抑制/解除、通知、升级、恢复。已确认的报警再次“确认”只追加备注，不改首次确认人。

## 通知分发

- **通道（NotificationChannel）**：`Email`（SMTP）、`Webhook`（通用 JSON POST）、`SmsHttp`（通用短信 HTTP 网关：`{"phones":[...],"message":"..."}`）。发送器实现 `INotificationSender`，新增厂商只需再实现一个并注册。
  - 敏感值（SMTP 口令、鉴权头的值）用 DataProtection 加密（purpose `Scada.NotificationSecrets.v1`，与设备口令共用持久化密钥环）。接口只返回 `secret: "***"` 与 `hasSecret`；更新时传 `***` 或不传表示保持，传空串表示清除。审计差分里凭据只显示 `***`。URL 中不允许写账号口令。
- **班次（DutyShift）**：起止分钟（本地时区 `ShiftTimeZone`，默认服务器时区）、星期掩码；结束早于开始表示跨零点，星期按班次开始那天判断。
- **路由（NotificationRoute）**：通道 + 区域（空 = 全部区域）+ 最低等级 + 班次（空 = 不限）+ 收件人 + 触发时机（报警/恢复）。
  - 区域匹配：测点报警按授权模型推导的区域（未指定区域的计算测点可能跨多个区域，任一匹配即发）；设备离线报警按设备区域；网关级报警只走“全部区域”路由。**这就是 webhook 按区域分流**。
  - 暂挂/抑制/抖动的报警不外发；同一报警对同一路由的“报警”通知只成功发送一次。
- **升级**：设置了 `EscalateAfterMinutes` 的路由是升级路由，只在报警持续未确认（且未暂挂/抑制）达到该分钟数时发送一次；后台每 `AlarmEscalationScanSec`（默认 15 秒）扫描。
- **持久化发件箱**：所有通知（首次与升级）先写入 `NotificationOutbox`，去重键 `动作:报警Id:路由Id` 唯一，重复触发或升级扫描与手动触发并发时只入队一次；投递前用条件更新抢占（Pending→Sending，带 5 分钟租约），多个实例/线程并发也只有一个发送。失败按 `NotificationRetryBaseSec`（默认 30 秒）指数退避，上限 `NotificationRetryMaxSec`（默认 1800 秒），最多 `NotificationMaxAttempts`（默认 5）次；进程重启后从发件箱继续（租约过期的 Sending 也会被接管）。最终失败写入时间线（`notify-failed`），诊断包 `notifications.json` 可见；`/health` 的 `alarmNotifications.finalFailed` 与告警 `alarm_notification_failed` 只统计**最近 24 小时内、未“已知悉”**的最终失败。报警通知页“投递失败”页签可逐条或全部“已知悉”（`POST /api/notification-outbox/acknowledge`，需报警所在区域的 Configure，记审计）。
- **超时与抢占期限**：抢占期限 5 分钟；各通道超时上限分别为邮件 120 秒、webhook/短信 30 秒，保存通道时校验，分发器另以“超时+1 秒”强制取消，保证一次发送不会超过租约导致他实例重复接管。
- **保留期清理**：已发送/最终失败记录在进入终态 `NotificationOutboxRetentionDays`（默认 30）天后每小时自动清理；待发/发送中的记录不清理。路由或通道被删除/停用视为永久失败；升级时报警已确认或已结束则不再发送。
- 每次投递写 `AlarmNotificationLog`，并在报警时间线留下“通知/升级通知”记录；`/health` 的 `alarmNotifications` 给出发送/失败计数与最后错误。
- 原有全局 `Scada:AlarmWebhookUrl` 保留，语义为“全部区域”；如需分区域，请改用通道 + 路由。

## 洪水与 KPI

- `GET /api/alarms/summary`：可见范围内各状态数量、暂挂/抑制/抖动、停用规则数，最近 10 分钟报警数与洪水判定（> `AlarmFloodThreshold`，默认 10），以及运行期洪水治理状态 `floodGuard`（策略 + 按区域明细）。报警页顶部在洪水时显示横幅。
- **运行期洪水治理**（`AlarmFloodGuard`）：按**区域**统计滚动 10 分钟报警率，超过 `AlarmFloodThreshold` 进入洪水，回落到结束阈值（起始阈值的一半，至少 1）以下才退出，滞回避免在阈值附近反复进出。
  - 进入洪水时把该片段内最早的一条报警标为**首出**（`AlarmEvent.FirstOut`，时间线记 `firstout`），用于快速定位根因；报警页在该行显示“首出”标记。
  - `AlarmFloodAutoSuppress`（默认开）时，自动抑制该区域内等级不高于 `AlarmFloodSuppressMaxLevel`（默认 Warning）的报警，抑制原因写“报警洪水：…”，时间线记 `suppress`；**Critical 始终发声**，已暂挂/已抑制/抖动的报警不重复处理。
  - 洪水退出（计数回落或窗口滑过）时逐条释放，重新按规则的条件抑制判定，时间线记 `unsuppress`；被洪水抑制的报警若期间恢复，抑制标记一并清除，正常进入“已恢复未确认”。
  - 区域之间互不影响：一个区域的洪水不会抑制其他区域的报警；无区域（未分区）报警单独归为一组。
  - 治理状态只存内存，进程重启后从新报警重新累积；重启时仍带洪水抑制原因的活动报警会被解除（条件抑制仍有效的保留）。
- `GET /api/alarms/kpi?from&to&top&standingHours`（区间 ≤ 92 天）：
  - 报警数（不含暂挂/抑制/抖动）、每小时、每 10 分钟均值与峰值；
  - 洪水区间（10 分钟桶）数量与时间占比；
  - 每操作员每小时报警数 = 报警数 / 小时 / 统计期内确认过报警的人数（至少 1）；
  - 最频繁报警 Top N（含抖动次数）、长期驻留报警（仍活动且超过 `AlarmStandingHours`，默认 24 小时）、操作员确认数/平均响应时间/备注数、等级分布。
- 前端“报警分析”页已升级为完整的报警统计分析仪表盘（EEMUA 191 KPI、坏分子、洪水首发、合理化建议、预聚合、下钻与导出），见 `alarm-analytics.md`；`/api/alarms/kpi` 保留兼容。

## 接口与权限

| 接口 | 权限 |
|---|---|
| `GET /api/alarms`（`live=true` 为实时视图）、`/summary`、`/kpi`、`/{id}/timeline` | 查看；按报警区域过滤，无权的详情 404 |
| `POST /api/alarms/{id}/ack`（可带 `{note}`）、`/{id}/notes` | 报警所在区域的确认报警权限 |
| `POST /api/alarm-rules/{id}/shelve`、`/unshelve`，`GET /api/alarm-shelves` | 规则测点区域的确认报警权限；列表按查看权限过滤 |
| `POST /api/alarm-rules/{id}/out-of-service`、规则的恢复延时/条件抑制字段 | 规则测点区域的配置权限；条件测点至少要能查看 |
| `GET /api/notification-channels`、`/api/duty-shifts` | 任一区域配置权限（凭据已脱敏） |
| 通道、班次增删改，`POST /api/notification-channels/{id}/test` | 全局配置权限 |
| `/api/notification-routes` 增删改查 | 路由所在区域的配置权限；全区域路由需全局配置权限；改区域需新旧区域都有权限 |

暂挂/解除、停用/恢复、通道/班次/路由增删改都写操作日志并带差分；被拒绝和失败的写操作同样记录。

## 升级

迁移 `AddAlarmManagement` 只追加列和新表：旧规则 `OffDelaySec=0`、不抑制、未停用；旧报警无标记。没有配置路由时不会产生任何新的外发。

## 已知限制

- 实时视图只保留最近 7 天的“已恢复未确认”，以免旧库遗留的未确认记录淹没界面；更早的仍可在历史中查到并确认。状态变化类事件（开始即结束）不算待确认报警。
- 暂挂粒度是报警规则；网关/设备离线这类无规则的报警不能暂挂。
- 抖动只做标记与静默，不自动调整死区/延时。
- 短信只做了通用 HTTP 适配；厂商专有签名需另写发送器。邮件使用 `System.Net.Mail.SmtpClient`（支持 STARTTLS；不支持 OAuth2）。
- 投递语义是“至少一次”：若发送成功但写回状态前进程崩溃，租约过期后可能重发一次。
- 洪水治理状态在内存中，重启后需要重新累积到阈值才会再次进入洪水；已抑制的报警在重启时统一释放。
- 洪水自动抑制只作用于规则报警，且不会抑制 Critical；网关/设备离线这类无规则报警不参与洪水抑制。
