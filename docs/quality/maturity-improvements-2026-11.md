# 成熟度改进落地（2026-11-23）

承接审查建议的六项，在不改产品默认决策（Timescale、AD/OIDC、默认强制 HTTPS、IEC 61850 内置 MMS、`FilterPublishedScreensByArea`、MCP 控制模式持久化、MCP 对已分配网关走工程重连）的前提下落地。

## 1. 历史库与旧库空间回收闭环

- 新增 `SpaceReclaimService`：探测 `auto_vacuum` / 空闲页，对配置库、运行库和 `agg_*.db` 做可中断回收。
- 已是 incremental 的库走 `incremental_vacuum`；仍为 `none` 的老库先 `quick_check`，再限时 `VACUUM` 并切换到 incremental。
- 历史查询仍占用槽位时跳过独占 VACUUM，失败记为可跳过，不让整个维护任务失败。
- `/api/health` 与系统健康页展示回收模式、空闲页和上次回收结果。

## 2. 核心组态并发保护

- `Device`、`AlarmRule`、采集通道、通知路由、计算任务增加 `Revision` 并发令牌，默认 1。
- 测点网页普通保存也会校验并递增已有的 `CalculationRevision`（Excel / 工程导入与此对齐）。
- **兼容策略**：请求省略修订号视为旧客户端，允许写入但仍然递增；一旦带上修订号则必须与当前值一致，否则 409「配置已被其他用户修改，请重新打开并合并」。这样已更新的网页和 MCP 受保护，旧脚本不会突然全部失败。

## 3. 发布包内的停机恢复剧本

- `Scada.BackupTool` 随绿色包发布到 `tools/`。
- 新增 `Restore-ScadaService.ps1` 与 `restore.sh`：停服务 → 还原到空暂存 → 校验 → 切换数据目录（保留站点配置与日志）→ 健康检查；失败切回。
- 不提供在线一键恢复 HTTP API。界面和手册写清：完整备份包（`full_*.zip` / `.zip.enc`）与维护任务留下的 `scada_*.db` 配置库拷贝不是同一类东西。

## 4. 网关死信回放

- 预览：校验载荷、配置修订和将写入/跳过的条数，不产生副作用。
- 回放：按原数据流/序号幂等执行（已回放的再次调用直接成功），用归档游标跳过已写入的同一时标，不回退 `ProcessedThrough`，避免重复归档或卡住后续序号。
- 不允许的情况返回明确错误。不改 MCP `reconnect_device`。

## 5. 趋势与分析标出历史缺口

- 趋势与分析响应增加 `gaps`：坏质量时段、采样空洞，以及本进程记录的拒收 / 暂存丢弃窗口。
- 精确分析仍然在超过读取预算时整次失败，不用截断数据充完整统计。不改存储后端。

## 6. 测试站点与临时目录

- Server 测试站点继续通过 `TestDatabaseTemplate` 复用已迁移的空库；统一更多测试项目的 `TempCleanup`。
- 报表归档用例按测试方法使用独立 `ReportsRoot`，避免共享目录下的偶发“归档文件缺失”。
