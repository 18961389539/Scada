# 历史数据与容量管理

## 查询保护

趋势接口 `/api/history` 与 `/api/v1/history` 按查询区间选择分辨率：不超过 6 小时读取原始数据，
不超过 7 天读取 5 分钟聚合，更长区间读取小时聚合。每次最多 50 个测点、366 天、总计 20,000 个返回点。
外部接口新增 `downsampled` 和 `pointLimit` 字段，原有 `resolution`、`series` 保持不变。

SQLite 趋势读取采用流式处理，不先把完整结果装入内存。按时间桶保留首尾、极小值和极大值；
预算允许时每桶额外保留一条非 Good 质量采样。这是趋势预览，不能代替完整质量区间分析。
保留点数按所请求测点平均分配，稀疏测点未用完的预算不再转移到其他测点。

`/api/history/analysis` 保持原始采样精确分析，最多 20 测点、24 小时；超过读取预算时整次失败，
不将截断数据当作完整统计。报表与聚合后台任务仍使用原有精确存储接口，不套用趋势抽样。

趋势与精确分析共用并发槽位，默认不排队，客户端取消时释放数据库读取资源。配置位于 `Scada`：

| 配置 | 默认值 | 含义 |
| --- | --- | --- |
| `HistoryQueryMaxConcurrent` | 4 | 单进程查询并发上限，有效范围 1–64 |
| `HistoryQueryTimeoutSeconds` | 30 | 单次查询时限，有效范围 1–300 秒 |
| `HistoryQueryMaxReadPoints` | 2000000 | 单次趋势最多扫描的源采样点数，与返回点数不同 |
| `HistoryAnalysisMaxPoints` | 200000 | 单次精确分析最多读取的原始采样点数 |

超限返回 `application/problem+json`，含稳定 `code` 和可显示的 `message`：

| HTTP | code | 处理建议 |
| --- | --- | --- |
| 503 | `history_query_busy` | 遵循 `Retry-After: 5`，避免立即并发重试 |
| 504 | `history_query_timeout` | 减少测点或调整区间，不要循环重试大查询 |
| 422 | `history_query_read_limit` | 减少测点、缩小原始分析范围，或使用较粗趋势分辨率 |

这些保护作用于当前进程的历史 HTTP 查询，不是全系统所有数据库访问的全局限流。
SQLite 单次底层操作取消仍受其驱动行为约束；读取循环会逐条检查取消。禁用聚合时长区间仍查询聚合库，
不会回退到高成本的无限原始查询。

## 容量与告警

系统健康页展示历史库、聚合库、WAL/SHM、暂存和其他文件的分项实际占用、独立历史磁盘可用量、
保留策略及查询并发/拒绝/超时/扫描超限计数。`/api/health` 的 `historyCapacity`、`historyQueries`
提供同样状态；原有 `storage.historyBytes` 仍只表示原始库与聚合库文件之和。

容量快照启动时生成，此后约每 30 秒更新，接口使用缓存，扫描失败会标注不完整并产生告警。
历史盘与配置盘分别检查，零可用空间也会告警。目录大小包括 WAL/SHM 和暂存，不能仅按 `.db` 计算。

| 配置 | 默认值 | 含义 |
| --- | --- | --- |
| `DiskFreeWarnGb` | 5 | 配置盘或历史盘低于此可用 GB 时告警；0 关闭 |
| `HistorySizeWarnGb` | 0 | 历史目录总占用达到此 GB 时告警；0 关闭 |
| `HistoryCapacityWarnDays` | 7 | 预计距磁盘可用量告警阈值不足此天数时告警；0 关闭 |

增长估算至少积累 5 分钟，使用最近最多约 24 小时目录净变化。没有正增长或有效磁盘信息时，
预计天数为空；服务重启重新积累。其他程序写盘、WAL 检查点、保留清理和采样速率变化都会影响估算，
它不是容量承诺或硬配额。所有 GB 按 1024³ 字节换算。

Prometheus `/metrics` 新增 `scada_history_storage_bytes`、`scada_history_raw_bytes`、
`scada_history_aggregate_bytes`、`scada_history_journal_bytes`、`scada_history_disk_free_bytes`、
`scada_history_disk_total_bytes`、`scada_history_growth_bytes_per_day`、`scada_history_days_until_disk_warn`、
`scada_history_capacity_scan_complete`、`scada_history_capacity_checked_timestamp_seconds`、
`scada_history_queries_active`、`scada_history_queries_limit` 及查询拒绝/超时/读取超限累计计数。
无有效估算时对应指标为 `NaN`，不是 0。指标鉴权继续沿用系统现有配置。

## 保留与空间回收

| 配置 | 默认值 | 行为 |
| --- | --- | --- |
| `HistoryRetentionMonths` | 2 | 按 UTC 月删除截止月之前的完整原始月库，最小 1；截止月整月保留 |
| `Agg5MinRetentionDays` | 365 | 删除过期的 5 分钟聚合行，最小 1 天 |
| `AggHourRetentionDays` | 0 | 小时聚合默认永久保留；只有显式配置正天数才清理 |

维护在启动约 10 秒后执行一次，此后每 24 小时执行。缩短保留期会删除数据，修改前先备份并验证恢复。
删除原始库前释放该库的空闲连接池；文件忙时保留并记录错误，不在主文件删除失败时继续删除 WAL/SHM。
空间压力告警不会自行缩短保留期、删除有效历史或关闭归档。

SQLite 删除聚合行一般只产生可复用页，不保证 `.db` 立刻缩小。本轮不在线自动运行阻塞性的 `VACUUM`。
需要物理回收时应在维护窗口停止服务、完成备份及恢复验证后，对指定聚合库执行 `VACUUM`，
确保有足够临时磁盘空间，并在重新启动前执行完整性检查。不要在服务写库时手工删除数据库或其 WAL/SHM。

当前验证覆盖固定点数抽稀、跨月/跨年 SQLite 读取、精确分析限额、并发/超时/取消、容量分项和保留清理。
实际项目仍需按测点数、归档频率、保留期、存储介质及并发报表负载进行持续压测。# 历史数据与容量管理

## 查询保护

趋势查询按区间选择原始数据（不超过 6 小时）、5 分钟聚合（不超过 7 天）、小时聚合。
`/api/history` 和 `/api/v1/history` 均最多 50 个测点、366 天，总返回点数不超过 20,000。
响应的 `downsampled` 和 `pointLimit` 表明是否抽稀及点数预算。
真实 SQLite 存储逐行读取并按时间桶保留端点、最小值、最大值，以及预算允许时的异常质量样本；
趋势保留内存与点数预算相关，不再先把整个区间加载为列表。聚合点仍代表平均值，不能替代原始瞬时峰值。

趋势和精确分析共享以下资源限制（`Scada` 配置节）：

| 配置 | 默认值 | 含义 |
| --- | --- | --- |
| HistoryQueryMaxConcurrent | 4 | 同时执行数，实际限制在 1 至 64，超出立即拒绝，不无限排队 |
| HistoryQueryTimeoutSeconds | 30 | 执行超时，实际限制在 1 至 300 秒 |
| HistoryQueryMaxReadPoints | 2000000 | 单次趋势最多扫描点数，包含所有测点、所有分库 |
| HistoryAnalysisMaxPoints | 200000 | 单次精确分析最多读取原始点数 |

精确分析仍最多 20 个测点、24 小时，统计与质量区间不使用抽样数据。
超限时不返回部分成功结果：并发满为 HTTP 503（`Retry-After: 5`），超时为 504，读取超限为 422。
错误使用 Problem Details，`code` 分别为 `history_query_busy`、`history_query_timeout`、`history_query_read_limit`。
客户端断开会取消执行并释放查询槽位。报表和内部存储接口的精确统计语义保持不变，不在此查询限额之内。
这些措施不等同于数据库端聚合或无限吞吐：高密度原始查询仍需扫描数据，应结合采样周期、磁盘性能压测。

## 容量监测

“系统管理 / 系统状态”展示历史目录总占用、原始库、聚合库、WAL/SHM、暂存、其他文件，
以及历史盘可用空间、净增长估算、保留期和查询拒绝累计数。
`/api/health` 的 `historyCapacity` 与 `historyQueries` 提供相同数据；旧 `storage.historyBytes` 仍仅为原始库加聚合库。
容量扫描每 30 秒更新并缓存，包含历史目录的子目录，HTTP 请求不重复扫描。
历史盘按 `HistoryRoot` 单独识别，不再误用配置库所在磁盘；0 字节可用也会告警。
无法读取磁盘或扫描不完整会明确告警，不伪装成健康。

| 配置 | 默认值 | 含义 |
| --- | --- | --- |
| DiskFreeWarnGb | 5 | 配置盘或历史盘可用空间低于此值告警，0 关闭 |
| HistorySizeWarnGb | 0 | 历史目录总占用达到此值告警，0 关闭 |
| HistoryCapacityWarnDays | 7 | 预计在这些天内达到磁盘剩余空间告警阈值时预警，0 关闭 |

单位 GB 按 1024 的三次方计算。净增长在至少 5 分钟后才提供，使用进程内最近约 24 小时的样本；
重启后重新积累。负增长记为 0，零增长、样本不足或未知磁盘空间不提供剩余天数。
预测仅反映历史目录近期净增长，不包含其他程序未来占用，也不能消除 WAL 检查点、清理和业务突增的影响。
它是预警参考，不是容量承诺。容量阈值只告警，绝不因空间压力自动删除历史数据。

Prometheus `/metrics` 新增 `scada_history_storage_bytes`、`scada_history_raw_bytes`、
`scada_history_aggregate_bytes`、`scada_history_journal_bytes`、`scada_history_disk_free_bytes`、
`scada_history_disk_total_bytes`、`scada_history_growth_bytes_per_day`、`scada_history_days_until_disk_warn`、
`scada_history_capacity_scan_complete`、`scada_history_capacity_checked_timestamp_seconds`，
以及 `scada_history_queries_*` 并发、拒绝、超时、读取超限指标。未知预测值输出 `NaN` 而不是误导性的 0。

## 保留与空间回收

| 配置 | 默认值 | 行为 |
| --- | --- | --- |
| HistoryRetentionMonths | 2 | 按 UTC 月份删除过期原始月库，保留边界月份，最小值 1 |
| Agg5MinRetentionDays | 365 | 删除过期 5 分钟聚合行，最小值 1 |
| AggHourRetentionDays | 0 | 0 永久保留小时数据；正数才按天删除 |

维护任务在启动约 10 秒后执行，之后每天执行。更改配置后重启生效。
缩短保留期会不可逆地删除到期数据，执行前应完成备份与恢复演练。
Windows 清理月库前会释放该库的空闲 SQLite 连接；若主文件删除失败，不再继续删除其 WAL/SHM。
被活动查询锁住的月库可能延后清理，应检查维护日志。迟到数据重新写入已删除月份时会正确重建表。

SQLite 删除聚合行通常只释放库内页面，不立即缩小文件；后续写入可复用页面。
本实现不会自动对在线数据库执行阻塞性的 `VACUUM`，也不会宣称“删除行即归还磁盘空间”。
必须回收聚合文件空间时，安排维护窗口，停止服务并验证备份后用 SQLite 工具执行 `VACUUM`；
事先预留足够临时空间，完成后执行完整性检查再恢复服务。不要手动删除仍在使用的 WAL/SHM 文件。

## 验证范围

测试覆盖高密度多测点抽稀、跨月/跨年三种分辨率、峰值与原始异常质量样本、取消和超时释放并发槽、
读取超限不返回残缺分析、满盘与独立历史盘、容量分项、预测预热、保留清理及月库重建。
浏览器测试覆盖健康接口、Prometheus、桌面与手机容量展示，以及外部 REST 趋势预算。
实际可支撑的测点数和长期容量仍须在目标硬件、实际归档周期下压测确认。