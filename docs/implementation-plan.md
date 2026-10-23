# SCADA 系统落地方案

## 0. 已确认的决策

| 项目 | 决定 |
|---|---|
| 数据采集 | IoTClient（NuGet 包 `IoTClient`） |
| 第一期协议 | Modbus TCP（驱动层预留扩展，后续可加 Modbus RTU、S7、三菱 MC、欧姆龙 FINS） |
| 数据库 | EF Core + SQLite（`Microsoft.EntityFrameworkCore.Sqlite`） |
| 后端 | C# / .NET 10 / ASP.NET Core + SignalR |
| 前端 | Vue 3 + Vite + TypeScript + Element Plus + ECharts |
| 规模 | 500～5000 个测点 |
| 部署 | 单进程：一个 ASP.NET Core 服务同时负责采集、处理、存储、API，并托管前端静态文件 |

单进程的理由：SQLite 同一时刻只允许一个写入者，采集、存储和接口在同一个进程里，写入可以集中到一个后台任务，部署和运维也最简单。

## 1. 总体架构

```text
PLC / 仪表
   │  Modbus TCP（IoTClient）
   ▼
┌──────────────────── Scada.Server（ASP.NET Core 单进程）────────────────────┐
│  采集引擎（每台设备一个后台任务，读写串行）                                       │
│        │ 采集结果（工程量 + 质量码 + 时间戳）                                    │
│        ▼                                                                    │
│  实时数据缓存（内存）──► 数据分发通道（Channel）                                  │
│                         ├─► 历史存储（批量写 SQLite）                         │
│                         ├─► 报警引擎（判断限值，写报警事件）                     │
│                         └─► SignalR 推送（节流后推给前端）                     │
│  Web API（配置、查询、控制命令、登录鉴权）  +  托管前端静态文件（wwwroot）          │
└─────────────────────────────────────────────────────────────────────────────┘
   ▲
   │  HTTP + SignalR（WebSocket）
浏览器（Vue 3）
```

## 2. 解决方案结构

```text
scada/
├─ src/
│  ├─ Scada.Core/            # 领域实体、枚举、接口（不依赖 EF Core 和 IoTClient）
│  ├─ Scada.Data/            # DbContext、实体配置、迁移、仓储、按月分库工厂
│  ├─ Scada.Collector/       # 驱动抽象的 IoTClient 实现、地址解析、批量读取规划、采集引擎
│  ├─ Scada.Engine/          # 实时缓存、数据分发、历史写入、聚合、报警引擎、控制命令服务
│  └─ Scada.Server/          # ASP.NET Core：API、SignalR Hub、JWT 鉴权、托管 wwwroot
├─ web/                      # Vue 3 + Vite + TypeScript
├─ tests/
│  ├─ Scada.Engine.Tests/    # 报警、死区、聚合等纯逻辑单元测试
│  └─ Scada.Collector.Tests/ # 连接 Modbus TCP 模拟器的集成测试
├─ docs/
└─ Scada.sln
```

主要依赖：

- 后端：`IoTClient`、`Microsoft.EntityFrameworkCore.Sqlite`、`Microsoft.EntityFrameworkCore.Design`、`Serilog.AspNetCore`、`Microsoft.AspNetCore.Authentication.JwtBearer`、`Microsoft.Extensions.Hosting.WindowsServices`
- 前端：`vue`、`vue-router`、`pinia`、`element-plus`、`echarts`、`@microsoft/signalr`、`axios`
- 开发测试：Modbus TCP 模拟器（如 Modbus Slave、ModbusPal 或 diagslave）

## 3. 数据模型

### 3.1 配置库 `scada.db`

| 实体 | 主要字段 |
|---|---|
| `Device` | Id、Name、Protocol（第一期只有 ModbusTcp）、Host、Port、StationNumber、ByteOrder（ABCD/BADC/CDAB/DCBA）、PollIntervalMs、TimeoutMs、Enabled |
| `Tag` | Id、DeviceId、Name、Description、Area（Coil/DiscreteInput/HoldingRegister/InputRegister）、Address、DataType（Bool/Int16/UInt16/Int32/UInt32/Float/Double）、Scale、Offset、Unit、Writable、ArchiveMode（不存/变化存储/周期存储）、Deadband、ArchivePeriodSec |
| `AlarmRule` | Id、TagId、Type（HH/H/L/LL/开关量变位）、Threshold、Level、DelaySec、RecoverDeadband、Enabled |
| `AlarmEvent` | Id、TagId、RuleId、Level、Value、StartTs、EndTs、AckUser、AckTs |
| `OperationLog` | Id、UserId、Ts、Action、TagId、OldValue、NewValue、Result |
| `User` / `Role` | 用户名、密码哈希、角色（查看员/操作员/管理员） |

### 3.2 历史库

| 文件 | 内容 | 默认保留 |
|---|---|---|
| `history/raw_yyyyMM.db` | `HistoryValue`：TagId、Ts、Value、Quality；主键 `(TagId, Ts)` | 最近 2 个月（可配置） |
| `history/agg_yyyy.db` | `Agg5Min`、`AggHour`：TagId、Ts、Avg、Min、Max、Count | 5 分钟聚合 1 年，小时聚合长期 |

### 3.3 SQLite 约定

- **时间一律存 `long`（Unix 毫秒，UTC）**。EF Core 的 SQLite 提供程序不能在数据库端对 `DateTimeOffset` 排序和比较，存 `long` 才能让时间范围查询走索引。
- 每个连接打开后执行 `PRAGMA journal_mode=WAL;`、`PRAGMA synchronous=NORMAL;`，让读写不互相阻塞。
- 原始历史按月分库：清理时直接删除整个文件，不用执行大批量 `DELETE`，也避免单库无限膨胀。
- 历史库使用同一个 `HistoryDbContext` 类型，由工厂按月份生成连接字符串；新的月份第一次写入时自动建库并执行迁移。

## 4. 采集模块（IoTClient + Modbus TCP）

1. **驱动抽象**：`Scada.Core` 定义 `IDeviceDriver`（`ConnectAsync`、`ReadAsync(ReadPlan)`、`WriteAsync(Tag, value)`、`IsConnected`）。`Scada.Collector` 用 IoTClient 的 `ModbusTcpClient` 实现 `ModbusTcpDriver`。业务代码只依赖接口，以后加协议只需新增一个实现。
2. **每台设备一个采集任务**：按 `PollIntervalMs` 循环。读和写都在这个任务里串行执行：控制命令先进入该设备的命令队列，在两次轮询之间执行。不允许多个线程同时操作同一个 `ModbusTcpClient`。
3. **批量读取规划**：启动或配置变更时，把同一设备、同一功能区的测点按地址排序，地址间隔小于阈值（如 10 个寄存器）就合并成一个区段，每段不超过 Modbus 单次上限（寄存器 125 个、线圈 2000 个）。每个区段读一次，再按偏移把值解析给各个测点。
4. **字节序**：32 位整数和浮点数按设备上配置的 ByteOrder 解析。现场最常见的问题就是字节序不对，配置页面要能直接切换并立刻看到效果。
5. **断线处理**：读取失败时，把该设备全部测点的质量码置为“通信中断”，按 1s、2s、4s… 最长 30s 的退避间隔重连，并产生“设备离线”报警；恢复后清除。
6. **结果处理**：原始值按 `工程量 = 原始值 × Scale + Offset` 换算，写入实时缓存（`ConcurrentDictionary<int, TagValue>`）；值或质量码发生变化时，把数据发到分发通道。

## 5. 处理与存储

### 5.1 历史写入

- 只有一个后台任务负责写原始历史：每 1 秒或攒够 2000 条时，用一个事务 `AddRange` + `SaveChanges`。写入期间关闭变更跟踪（`ChangeTracker.AutoDetectChangesEnabled = false`），每批写完换一个新的 DbContext。
- 存储策略按测点配置：
  - **变化存储**：与上一次存储值的差超过死区才存。
  - **周期强制存储**：超过 `ArchivePeriodSec`（默认 600 秒）没有存过，就强制存一次，保证曲线连续。
  - **开关量**：只在变位时存。
- 如果压测发现 EF Core 写入跟不上，只把这一处热点改为 `Microsoft.Data.Sqlite` 的预编译参数化插入，其他地方仍然用 EF Core。

### 5.2 容量估算（按 5000 点上限）

| 假设 | 数值 |
|---|---|
| 测点数 | 5000 |
| 死区过滤后，平均每个点每 20 秒存一次 | 250 行/秒 |
| 每天行数 | 约 2160 万行 |
| 每行占用（含主键索引） | 约 40～50 字节 |
| 每天原始历史 | 约 0.9～1.1 GB |
| 保留 2 个月 | 约 60 GB |

SQLite 批量写入 250 行/秒没有压力，查询单点一段时间的数据可以走 `(TagId, Ts)` 主键，速度也足够。真正需要控制的是磁盘占用，所以要做到：

- 死区和采集周期按测点合理配置，不要所有点都 1 秒采集、0 死区。
- 原始数据只保留最近几个月；长期趋势查 5 分钟聚合和小时聚合（5000 点的 5 分钟聚合，每天约 144 万行，约 70 MB）。
- 如果实际数据量明显超过估算，第二期把历史存储换成 TimescaleDB 或 InfluxDB。存储通过 `IHistoryStore` 接口访问，替换时上层不用改。

### 5.3 聚合

后台任务每 5 分钟对上一个时间窗计算 Avg/Min/Max/Count 写入 `Agg5Min`，每小时再汇总写入 `AggHour`。趋势查询根据时间跨度自动选择数据源：

| 查询跨度 | 数据源 |
|---|---|
| 6 小时以内 | 原始数据 |
| 6 小时～7 天 | 5 分钟聚合 |
| 7 天以上 | 小时聚合 |

### 5.4 报警引擎

- 订阅数据分发通道，按 `AlarmRule` 判断限值。超限持续 `DelaySec` 后才产生报警；回到“限值 ∓ 恢复死区”以内才算恢复，防止在限值附近反复跳动。
- 报警产生、恢复、确认都写入 `AlarmEvent`，并通过 SignalR 推送。
- 测点质量码异常（通信中断）时，不判断限值报警，只保留设备离线报警。

### 5.5 控制命令

流程：校验 JWT 和角色，检查测点是否 `Writable`、值是否在允许范围内，把命令放进设备命令队列，执行写入，回读确认，最后写 `OperationLog` 并把结果返回前端。命令超时（如 5 秒）没有执行完就返回失败。

## 6. 后端接口

### 6.1 REST API

| 路径 | 说明 |
|---|---|
| `POST /api/auth/login` | 登录，返回 JWT |
| `/api/devices`、`/api/tags`、`/api/alarm-rules` | 配置增删改查；测点表支持 Excel 导入导出 |
| `POST /api/devices/{id}/test` | 测试连接，读取指定地址 |
| `GET /api/realtime?tagIds=` | 实时值快照 |
| `GET /api/history?tagIds=&from=&to=` | 历史查询，按跨度自动选择数据源 |
| `GET /api/alarms`、`POST /api/alarms/{id}/ack` | 报警查询、确认 |
| `POST /api/commands` | 下发控制命令 |
| `GET /api/logs` | 操作日志 |
| `GET /health` | 健康检查（采集任务状态、通道积压、磁盘空间） |

### 6.2 SignalR Hub（`/hubs/realtime`）

- 前端进入页面时按测点组订阅，离开时取消订阅。
- 后端每 500 毫秒合并一次变化，只推送订阅的、有变化的值。
- 报警的产生、恢复、确认全量推送给所有在线客户端。

### 6.3 鉴权

JWT + 角色：

- **查看员**：只能查看。
- **操作员**：可以下发控制、确认报警。
- **管理员**：可以修改配置、管理用户。

## 7. 前端（Vue 3）

| 页面 | 内容 |
|---|---|
| 登录 | 用户名密码登录，Token 存储与过期跳转 |
| 总览 | 设备在线率、当前报警数、关键指标卡片 |
| 设备与测点配置 | 表格编辑、Excel 导入导出、测试连接、字节序切换预览 |
| 实时监视 | 按设备或区域查看测点列表；SVG 工艺流程图，用测点绑定颜色、文字、动画 |
| 趋势曲线 | 多点叠加、时间范围选择、缩放、导出 CSV（ECharts） |
| 报警 | 实时报警窗（带声音提示）、确认、历史报警查询 |
| 报表 | 日报、月报（按小时或天统计），导出 Excel |
| 系统管理 | 用户、角色、操作日志 |

开发时用 Vite 开发服务器，把 `/api` 和 `/hubs` 代理到后端；发布时把 `web/dist` 复制到 `Scada.Server/wwwroot`，由后端统一托管。

## 8. 部署与运维

- 发布：`dotnet publish src/Scada.Server -c Release -r win-x64 --self-contained`
- 签名密钥：`Jwt:Key` 不写进仓库，部署时用环境变量 `Jwt__Key`（≥32 字节）提供，未配置则启动失败；开发环境未配置时自动生成临时密钥。
- 运行方式：Windows 上注册为 Windows 服务（`UseWindowsService()`）；以后要上 Linux 可以用 systemd（`UseSystemd()`）。
- 数据库：启动时自动执行配置库迁移；每天用 SQLite 在线备份接口备份 `scada.db`；历史库按保留策略自动删除过期文件。
- 日志：Serilog 按天滚动，保留 30 天。
- 监控：`/health` 端点，外加前端“系统状态”页显示各设备通信状态和统计。

## 9. 分阶段计划（约 8～10 周，1～2 人）

| 阶段 | 周期 | 交付内容 | 验收标准 |
|---|---|---|---|
| 0. 骨架 | 第 1 周 | 解决方案结构、配置库 DbContext 与迁移、Serilog、前端脚手架、Modbus TCP 模拟器环境 | 后端启动后自动建库；前端能通过代理访问后端 |
| 1. 采集与实时 | 第 2～3 周 | 驱动抽象、`ModbusTcpDriver`、批量读取规划、采集引擎、实时缓存、SignalR 推送、设备与测点配置页、实时监视页 | 连接模拟器，100 个点 1 秒刷新，断开模拟器能看到质量码变化并自动重连 |
| 2. 历史与趋势 | 第 4～5 周 | 历史写入、死区与周期存储、按月分库、5 分钟和小时聚合、趋势页 | 模拟 5000 点连续运行 24 小时，写入无积压，7 天趋势 2 秒内返回 |
| 3. 报警与控制 | 第 6～7 周 | 报警引擎与报警页、控制命令与回读、JWT 登录与角色、操作日志 | 限值报警产生、恢复、确认完整；操作员能写值，查看员被拒绝 |
| 4. 完善与上线 | 第 8～10 周 | SVG 流程图画面、报表、Excel 导入导出、Windows 服务部署、压测与 7×24 小时稳定性测试 | 现场试运行一周无崩溃、无内存持续增长 |

## 10. 风险与对策

| 风险 | 对策 |
|---|---|
| SQLite 磁盘占用随点数快速增长 | 死区和周期存储、按月分库、聚合表、保留策略；预留 `IHistoryStore` 接口，必要时换时序库 |
| 多线程访问同一个 Modbus 连接导致报文错乱 | 一台设备一个任务，读写都串行执行 |
| 现场字节序、地址偏移（从 0 还是从 1 开始）配置错误 | 配置页提供测试读取和字节序切换预览；地址规则写进文档和界面提示 |
| 通信不稳定 | 第一阶段就实现质量码、退避重连、设备离线报警 |
| 前端推送数据量过大导致浏览器卡顿 | 按页面订阅、500 毫秒合并推送、只推变化值 |
