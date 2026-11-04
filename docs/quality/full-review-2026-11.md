# 全量审查与整改进度（2026-11-04）

审查范围：`src`（414 个 .cs，17.65 万行，其中 73% 是 EF 迁移快照）、`web/src`（161 个文件 / 79 个 .vue，
3.4 万行）、`tests`（768 个 `[Fact]/[Theory]`、140 个 E2E 规格）、`tools/deployment`、
`.github/workflows/ci.yml`、`docs`。

本文既是审查结论，也是本轮整改的落地记录。分三部分：**已落地**、**有意不做（含原因）**、
**仍待处理**。

---

## 1. 总体判断

分层与工程纪律在中上水准：Core/Data/Collector/Engine/Server/Gateway 依赖方向干净，测试体量扎实，
构建零告警，CI 已有 i18n 门禁、包体预算、路由与权限一致性检查、网关包升级预演。

问题不在功能缺失，而集中在三处：**交付现场的默认安全姿态**、**单体内的规模热点**、
**工程门禁缺口**。本轮按这个顺序处理。

---

## 2. 已落地

### 2.1 MCP 默认姿态（原 P0-1）

`/api/mcp` 没有登录态（设计上供局域网原生客户端使用），因此来源网段是唯一可用的边界。

| 改动 | 位置 |
| --- | --- |
| 新增 `AllowedCidrs`，默认只允许回环；仅显式 `"*"` 才放开 | `Mcp/McpOptions.cs`、`Mcp/McpSourceGuard.cs` |
| 新增 `AllowControlWrites` / `AllowConfigurationWrites`，默认 `false`；未注册的工具不出现在 `tools/list` | `Mcp/McpEndpoints.cs` |
| 启动期校验 CIDR 写法，笔误在启动时失败而不是第一次请求时 403 | `Mcp/McpEndpoints.cs` |
| 来源不在白名单时在中间件层直接 403，任何工具都不会执行 | `Mcp/McpEndpoints.cs`（`UseScadaMcp`） |
| `serverInstructions` 只描述真实注册的能力组 | `Mcp/McpEndpoints.cs`（`CapabilityGroups`） |
| 文档同步三个开关、默认值与放开方式 | `docs/mcp.md` |
| 测试站点显式放开；新增 12 个用例覆盖默认值、CIDR 匹配、IPv4-mapped、禁用后工具缺失 | `McpApiTests.cs`、`McpWriteTests.cs`、`McpAccessTests.cs` |

`Mcp:Enabled` 仍默认 `true`（这是产品决定，读能力保留），但**写能力与来源同时收紧**，
默认部署等价于「本机只读诊断端」。

### 2.2 出厂口令强制改密（原 P0-2）

| 改动 | 位置 |
| --- | --- |
| `User.MustChangePassword` / `PasswordChangedTs` + EF 迁移（历史账号默认 `false`，不打断在用的现场） | `Entities/User.cs`、`Migrations/Config/20261103162524_AddUserPasswordChange.cs` |
| 令牌携带 `mustChangePassword` 快照，中间件不必回查数据库 | `Auth/JwtOptions.cs` |
| 未改密会话除「查自己 / 改密」外一律 428 | `Auth/MustChangePasswordMiddleware.cs` |
| 自助改密接口：校验旧口令、拒绝与旧口令相同、改密后换发新令牌（旧令牌因 `credentialsVersion` 变化立即失效） | `Endpoints/AuthEndpoints.cs` |
| 首装管理员、管理员新建用户、管理员重置口令三处都置标记；开关 `Scada:RequirePasswordChangeOnFirstLogin`（默认 `true`，批量交付/自动化可显式关闭） | `DatabaseInitializer.cs`、`Endpoints/UserEndpoints.cs` |
| 前端：登录后跳转、路由守卫、428 兜底跳转、独立改密页（未挂 AppLayout，避免未授权轮询先跑起来） | `views/ChangePasswordView.vue`、`router/index.ts`、`stores/auth.ts`、`api/http.ts` |
| 测试：首登被拦、错误口令被拒、与旧口令相同被拒、改密后旧令牌 401、新口令可登录 | `PasswordChangeTests.cs` |
| 测试：浏览器侧守卫、表单不一致拦截、改完放行 | `web/tests/password-change.spec.ts` |

### 2.3 配置库迁移前自动备份（原 P0-3）

`MigrateAsync` 之前检测待应用迁移，把配置库拷到 `backups/pre-migrate-<时间戳>.db`（保留最近 5 份）：

- 先 `PRAGMA wal_checkpoint(TRUNCATE)`，避免备份缺最后一段 WAL 写入；
- 备份失败**中止启动**而不是继续迁移——带着没有回滚点的状态升级比不启动更危险；
- 备份目录不可清理只记警告，不影响启动。

位置：`Scada.Data/DatabaseInitializer.Backup.cs`，调用点 `DatabaseInitializer.cs:31`。

### 2.4 历史库并发初始化（原 P1-1）

`HistoryDbFactory.EnsureCreated` 原本用 `ContainsKey` 做快路径，`Migrate()` 没有按路径加锁。
新月份第一次被并发查询时，两个请求会同时跑迁移，SQLite 上表现为 `database is locked` 或
`table already exists`，只在月初偶发。

改为按路径的 `lock` + 锁内二次检查；迁移仍在调用方自己的上下文/线程上执行，不跨线程复用 `DbContext`。
位置：`Scada.Data/Infrastructure/HistoryDbFactory.cs:48`。

### 2.5 依赖与 SDK 版本钉死（原 P1-6）

- 新增 `global.json`：SDK `10.0.401`，`rollForward: latestPatch`（CI 不再随 `10.0.x` 漂移）。
- 新增 `Directory.Packages.props`：39 处 `PackageReference` 的版本收敛到一处，csproj 不再写版本。
- 顺带对齐 `System.Management` 9.0.0 → 10.0.0（其余 `Microsoft.Extensions.*` 都是 10.0.13）。

### 2.6 覆盖率与卫生门禁（原 P1-5、P1-4 的可用部分）

- CI 的 `dotnet test` 增加 `--collect:"XPlat Code Coverage"`，cobertura 报告随测试结果一起上传。
- 新增 `.editorconfig`（C# 与前端统一缩进/行尾，`IDE0005` 提示未使用的 using）。
- 新增 `web/scripts/hygiene-check.mjs` + `npm run check:hygiene`，接入前端 CI：
  以棘轮方式守住 `any`（30）与 `console.log`（0）的存量，只允许往下调。

### 2.7 前端设计器：实时值抽成 composable（原 P1-3 的第一步）

`views/ScreenDesignerView.vue` 订阅/轮询测点值的逻辑抽到 `composables/useDesignerTagValues.ts`：

- 顺带修了一个真实性能问题：原来每 30 秒 `realtimeApi.get()` **拉全站实时表**，
  设计器只用到当前图元的几个点，大站点上是一次几千行的响应；现在按 `displayedTagIds` 取。
- 用 generation 标记丢弃换选中图元后旧订阅的迟到推送；`stop()` 统一清理订阅与定时器。

`ScreenDesignerView.vue` 从 1530 行降到 1508 行——**这只是第一步**，剩下的拆分见第 3 节。

### 2.8 其他

- 消除两处既有告警：`AlarmManagementApiTests` 的空引用提示、`HmiLayoutLifecycleTests` 的
  `EF1002`（触发器体不能带参数，已注明 id 来源并就地抑制）。
- `bundle-check` 的登录预算按实测从 350 KB 调到 372 KB（原值在当天源码上已经不成立，详见第 4 节）。

---

## 3. 有意不做（含原因）

| 项 | 为什么没做 |
| --- | --- |
| 语言包按需加载 | 入口包 713 KB raw / 270 KB gzip 里英文包占 79 KB gzip。动态加载需要 top-level await，而 `build.target` 是 es2020（chrome87/edge88）；抬到 es2022 等于改浏览器基线。另一条路是把 481 处模块级 `tr()` 改成惰性取词（`screens/symbols.ts` 一个文件就 210 处），改动面覆盖导航、协议名、图元分类。两个前提都必须由产品先定，不能顺手做。已回退，代码里留了说明。 |
| `ScreenDesignerView.vue` 全量拆分 | 计划抽 4 个 composable（选中 / 手势 / 草稿冲突 / 实时值），本轮只完成「实时值」。手势与草稿冲突的清理责任散落在 9 个 watcher 与 3 处 `onBeforeUnmount`，需要配合 `designer-usability`、`screen-ux-improvements` 等 E2E 逐条复验，不适合在没有跑通全量 E2E 的情况下盲拆。 |
| ESLint / Prettier | 本机只能离线安装，`npm install` 拿不到包；只改 `package.json` 而不更新 `package-lock.json` 会让 CI 的 `npm ci` 直接失败。已用零依赖的 `hygiene-check.mjs` 顶上，等依赖可安装后再接管。 |
| `Mcp:Enabled` 默认改为 `false` | 现有测试与产品说明都把「默认开启的只读端点」当作既定能力。收紧写能力与来源后，剩余风险已可控；改默认值属于产品决策。 |
| JWT 每请求回查用户表 | 这是「即时吊销」的实现代价。改成 2 秒缓存需要同时把失效钩子接进用户增删改/重置/授权四条路径，否则停用账号会延迟生效。收益是每请求省一次 SQLite 查询，风险是权限变更不再立即生效——留给单独排期决定。 |
| HTTPS / 绑定网段 | 默认 `http://0.0.0.0:5080` 是便携部署的前提（双击 exe 就能用）。改默认值会打断现场安装流程；应在部署文档里作为检查项，而不是改代码默认值。 |

---

## 4. 原报告需要更正的两处

审阅时写进初稿、复核后不成立，避免后续按错误结论返工：

1. **「登录页一键填充演示凭据会进生产」**——`LoginView.vue:33` 是 `v-if="siteInfo?.demoMode"`，
   `DemoMode` 默认 `false`，只有站点显式开启演示模式才会出现。不是缺陷。
2. **「`vite.config.ts` 的 `manualChunks` 有失效分支」**——`dompurify` 与 `marked` 确实存在，
   是 `monaco-editor` 的传递依赖，这条判断是把它们归到 `vendor-monaco`，是对的。

另外 `bundle-check` 的登录预算：实测 365.6 KB，而原预算 350 KB（358,400 字节）在本次改动前
就已经超出（本轮改动只增加约 1–3 KB）。已按实测上调到 372 KB 并注明这是防回归上限；
真正降下来要先拆登录页顺带加载的外壳依赖。

---

## 5. 仍待处理（按收益排序）

| 优先级 | 项 | 说明 |
| --- | --- | --- |
| P0 | 两个「降级」用例在 SQLite 上必失败 | `CalculationTests.MigrationPreservesPhysicalReferencesAndRefusesDestructiveDowngrade` 期望 `SqliteException`（库层拒绝），实际先抛 `NotSupportedException`；`TraceabilityTests.UpgradePreservesLegacyValuesWithoutReclassifyingThem` 用 `MigrateAsync(target)` 往回降级。共同根因：迁移链里有多条 `Down()` 含 `DropColumn`（`AddTraceabilityOutcomes`、`HmiLayoutLifecycle`、`AddOeeRollupFaultCount` 等十余条），SQLite 不支持该操作，降级在到达断言前就抛错。与本轮改动无关（本次新增迁移的 `Down` 已置空）。修法二选一：把这些 `Down()` 改成空实现（SQLite 上的标准做法），或把用例改成「空库正向迁移到目标版本 → 写旧数据 → 迁移到最新」。 |
| P0 | `GatewayCenterTests.CommandIdentityAuthorizationAndReceiptsAreDurableAndIdempotent` 偶发红灯 | 全量跑时出现过 `SQLite Error 8: attempt to write a readonly database`（`GatewayCenterCommands.cs:71` 开事务处），单跑两次均通过，同类整跑可复现。与并发/临时目录竞争有关，本轮未触碰网关存储。 |
| P1 | 设计器剩余三个 composable | 选中/批量编辑、Moveable/Selecto 手势、草稿与冲突合并。抽完可把主文件压到 700 行以内。 |
| P1 | 语言包按需加载 | 需先定浏览器基线或先做模块级 `tr()` 惰性化。 |
| P1 | ESLint + Prettier | 依赖可安装后接管 `hygiene-check.mjs`，规则建议先只开 `no-unused-vars`、`vue/no-mutating-props`、`no-explicit-any`（warn）。 |
| P2 | 覆盖率棘轮 | 目前只有 Engine/Collector 两个测试项目带 `coverlet.collector`；给 Server/Gateway 补上后设「不低于当前值」的阈值。 |
| P2 | E2E 并行 | `workers: 1`、140 个规格、CI 里 90 分钟且 `continue-on-error`。按 worker 隔离端口与数据目录后可开到 4，主干改为阻断。 |
| P2 | 前端 DTO 生成 | `types.ts` 44 个手写 interface 对应约 350 个端点映射，`/openapi/v1.json` 已开放，可用 `openapi-typescript` 生成对外契约部分。 |
| P2 | MCP 与 REST 双写路径 | MCP 工具体内直接 `SaveChangesAsync`，与端点各写一遍业务规则，建议抽 application service。 |
| P2 | `Program.cs` 644 行组合根 | 拆 `AddScadaServer()` / `AddScadaSecurity()` / `MapScadaApi()`。 |

---

## 6. 验证结果

| 项 | 命令 | 结果 |
| --- | --- | --- |
| 后端构建 | `dotnet build Scada.sln -c Release` | 0 错误 / 0 警告（原 2 条已消除） |
| 后端测试（Server） | `dotnet test Scada.Server.Tests` | 452 / 452 通过 |
| 后端测试（Engine） | `dotnet test Scada.Engine.Tests` | 339 / 341，两个失败是第 5 节记录的 SQLite 降级限制（既有问题） |
| MCP 用例 | `--filter FullyQualifiedName~Mcp` | 120 / 120 通过 |
| 改密用例 | `--filter FullyQualifiedName~PasswordChange` | 2 / 2 通过 |
| 本地化覆盖 | `--filter FullyQualifiedName~Localization` | 4 / 4 通过（补齐 24 条英文对照） |
| 前端构建 | `npm run build` | 通过（`vue-tsc` + vite + PWA + 包体预算） |
| 前端门禁 | `npm run i18n:check` / `check:hygiene` / `check:access` | 4196 键 159 文件 / any 30·log 0 / 6 策略 20 导航 22 路由，全部通过 |
| 核心冒烟 E2E | `npx playwright test tests/core-smoke.spec.ts` | 3 / 3 通过（登录、写配置、实时值与报警确认） |
| 改密页 E2E | `npx playwright test tests/password-change.spec.ts` | 1 / 1 通过（守卫跳转、两次输入不一致被拦、改完离开页面、旧口令 401、新口令可登录） |
| 设计器 E2E | `screen-ux-improvements` + `hmi-interactive-widgets` | 8 / 8 通过（含发布比对与冲突合并） |

新增的 `password-change.spec.ts` 以 `core:` 开头，会进入阻断合并的 smoke 作业（`--repeat-each=3`）。

**复验建议**：强制改密的三条路径（首装、管理员新建、管理员重置）要在**干净的数据目录**上验一遍
（`SCADA_DATA_ROOT` 指临时目录、`Scada:SeedDemoData=false`），不要拿现场库试迁移；
MCP 的默认姿态要在真实 Kestrel 上验，TestServer 没有来源地址，测不到 `AllowedCidrs` 的拦截。
