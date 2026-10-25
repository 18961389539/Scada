# OEE / 生产统计

菜单“OEE 生产统计”（`/oee`）提供产线配置、停机与产量修正、OEE 看板与导出；报表中心可用“OEE KPI”列定时生成签名归档报表。

## 统计口径

- **可用率 Availability** = 运行时间 ÷ 计划生产时间。计划生产时间 = 班次窗口时长 − 计划停机。班次外时间不计入分母与分子。
- **表现率 Performance** = (理想节拍秒 × 总产量) ÷ 运行秒数。总产量 = 好品 + 不良 + 报废。默认超过 100% 时封顶为 1.0 并标记“已封顶”；阈值可改为保留原值并标记“超 100%”。
- **质量率 Quality** = 好品 ÷ 总产量。
- **OEE** = A × P × Q。
- **MTBF** = 运行时间 ÷ 故障次数；**MTTR** = 故障时长合计 ÷ 故障次数。
- **停机帕累托**：班次内停机按原因汇总时长与次数（不含运行）。
- 半开区间 `[from, to)`。无停机事件的空隙视为运行。

## 数据来源

| 来源 | 说明 |
|---|---|
| 状态测点 | 产线 `StateTagId` + `StateMapJson`（缺省非 0=运行、0=停机）。采集器开闭 `DowntimeEvent`（`source=tag`） |
| 产量计数器 | 好品 / 不良 / 总产量测点增量写入分钟预聚合；回绕按新值起算 |
| 人工停机 | `POST /api/oee/downtimes`；可关联原因码 |
| 报警候选 | `POST /api/oee/downtimes/import-alarms` 导入为未确认候选，操作员确认后计入 |
| 产量修正 | `POST /api/oee/corrections`，必须填原因，写差分审计 |

## 配置对象

- **产线**：区域、状态/产量测点、默认产品、理想节拍或额定产量、小时目标。
- **产品（SKU）**：理想节拍或额定产量；可全局或按区域。
- **生产班次**：起止分钟（可跨零点）、星期掩码；与通知值班班次独立。
- **停机原因**：代码、OEE 损失类别（Availability/Performance/Quality）、是否计划停机。

以上四类进入工程包与配置快照（停机事件、产量修正、预聚合不入包）。

## 预聚合

- 表 `OeeRollup`：粒度 0=分钟（UTC 整分）、1=小时、2=日（`ShiftTimeZone` 本地零点）；按产线（+可选产品）汇总 Planned/Run/Down/Fault/Setup 与产量。
- 后台 `OeeRollupService` 按停机事件与产量修正重建，备机不运行。
- 保留：分钟 14 天、小时 90 天、日 1095 天（可配）。
- 查询：≤ `OeeAnalyticsRawMaxDays`（默认 31）天现算；更长时早期用小时预聚合，最近窗口现算。

## 接口与权限

| 接口 | 权限 |
|---|---|
| 看板 `/api/oee/analytics`、导出 | 查看；结果按区域过滤 |
| 产线/产品/班次/原因 CRUD | 区域配置（全局对象需全局配置） |
| 停机录入/确认、产量修正 | 区域控制 |
| 阈值、预聚合重算、报警导入 | 全局配置 |

## 报表

新列类型 `OeeKpi`，`output` 取：`availability`、`performance`、`quality`、`oee`、`plannedHours`、`runHours`、`downHours`、`good`、`reject`、`scrap`、`total`、`scrapRate`、`mtbfHours`、`mttrHours`、`faultCount`、`productionVsTarget`、`target`。按模板区域下的产线统计；汇总行整期重算。

## 局限

- 未接真实 PLC 计数器长稳验证；采集器在状态变化时刻之前的时长记为变化前状态。
- 预聚合回补以停机事件与人工修正为准；历史测点反演仅用于现场后续增强。
- 多产线合并 KPI 为时长与产量直接相加后重算 A/P/Q。
- 班次外运行不计入 OEE（记入 Outside）。
