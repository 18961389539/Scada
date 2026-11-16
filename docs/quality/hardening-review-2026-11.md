# 第二轮改进：安全边界、限流链路与一致性守门（2026-11-16）

承接 `core-review-2026-11.md` 的 Scada.Core 11 项改进。本轮重新通读当前代码后，
针对「静默失效 / 破权限边界」这一类问题落地，不重复上一轮清单，也不重复
「拆程序集 / ScadaSettings 按域拆 / ScreenNode 多态 / ScadaTime 全面换 TimeProvider /
CalcJob owned type」这些已明确记录为「有意不做」的结构性改动。

## P0

| 项 | 根因 | 改动 |
| --- | --- | --- |
| API Key 区域限制 fail-open | `ExternalApiKeyService.ParseAreas` 对损坏的 `AreaIdsJson` 执行 `catch { return null; }`；`null` 在 `AccessForKeyAsync` 里表示「未限定区域 = 全厂」，在 `Access.cs` 里 `AreaId is null` 对任意区域放行 | 改为三态：`null`+valid = 全厂、非空数组 = 限定区域、**空数组+invalid = 无任何授权**。`TryParseAreas` 拒绝空数组 / 非正数 / 超长 / 非规范化形状；`FindBySecretAsync` 的 `Accept` 直接拒绝损坏密钥并累计 `InvalidAreaRestrictionRejections` + 错误日志；`ExternalApiKeyView` 增加 `AreaRestrictionInvalid` 让管理页看见故障。反序列化用 `JsonNumberHandling.Strict`，否则 Web 默认会把 `["1"]` 当成合法 `[1]` |
| HA 回放 SQL 注入 | `HaRuntimeSync.Apply` 把 peer 传来的 `Table`、原始 WHERE 片段 `Key`、列名直接拼进 `DELETE` / `INSERT OR REPLACE` / `SELECT` | 新增 `IsIdentifier` / `SafeTable`（标识符形态 + 本机 `sqlite_master` 实存 + `Skip` 拒绝同步元数据表）/ `TryParseKey`（只接受 `Col=字面量 AND ...` 严格语法，最多 16 项）/ `WhereSql`（列名加引号、值全部参数化）。`PrimaryKeys`、`PredicateSql` 同步加白名单。非法输入整批回滚，`Applying` 标记不会卡住备机 |
| MCP 双限流中间件 | `ScadaApiPipeline.UseRateLimiter()` 与 `AddScadaMcp` 内各挂一次，`RequireRateLimiting("ScadaMcp")` 对每个请求取两次 lease，`Mcp:RequestsPerMinute` 实际减半 | 全进程只在 `ScadaResilience.AddRateLimiting` 注册一次 `AddRateLimiter`，MCP 策略与 `OnRejected` 合并进同一委托；`McpEndpoints` 去掉重复的 `UseRateLimiter()`，保留来源校验与请求体限长 |

## P1

| 项 | 改动 |
| --- | --- |
| 限流落在匿名桶 | 认证在 `UseAuthentication`、授权在 `UseAuthorization`，限流夹在中间；API 密钥与网关凭据此时还没进 `http.User`。新增 `CredentialPartitionKey`（JWT 用户 / `X-Api-Key` / `Gateway` 授权头的 SHA-256 指纹，不做鉴权判断），`PartitionKey(http, identity)` 统一按「身份 + 终端地址」分桶。来源地址缺失时退回 `local` 桶，不再 `GetNoLimiter` 整体放行 |
| MCP 来源三重 fail-open | `McpSourceGuard.IsAllowed(remote: null)` 原来直接 `return true`；改为只有部署方显式写出 `*` 才允许无来源地址。集成测试需要 `AllowedCidrs = ["*"]` |
| 审计 QueryString 明文落库 | 新增 `Scada.Core/Security/AuditQuery.cs`：`Scrub` 只保留参数名、命中 token/key/secret/password/signature/session/... 的值换 `***`，限 64 段 + 512 字符。`AiFilters`、`ExternalRestEndpoints` 手工序列化 `Details` 的两处改用它（这两条链不经过 `AuditDiff` 的 `[Sensitive]` 差分） |
| 审计写入失败静默 | 新增 `Scada.Server/Auth/AuditFailures.cs`（累计次数 + `Scada.Audit` 错误日志）。`AiFilters`、`ExternalRestEndpoints`、`AccessAuthorization`（中间件与结果过滤两处）、`AuditFailuresFilter`、`ExternalApiKeyService.TouchAsync` 的 `catch { }` 全部改为 `Report(...)`，响应结果不变但故障可见 |
| 输入校验 | `ParseScopes` 加 `Enum.IsDefined` / `All` 位掩码判断，拒绝 `"31"` 这类未定义 flag；`BackupService.VerifyAsync` 对 manifest 的 `file.Path` 做 `Path.GetFullPath` + 根目录前缀校验，拒绝 `../` 与绝对路径 |
| i18n 门禁只扫 5 个目录 | `LocalizationTests.EveryUserFacingBackendMessageHasAnEnglishTranslation` 改为扫 8 个产品程序集（`Services` / `Reports` / `External` / `Mcp` / `Ha` 等此前全在白名单外）。历史欠账记在 `tests/Scada.Server.Tests/i18n-baseline.json`（977 条），**新增**未翻译中文立即变红；只有显式 `SCADA_I18N_GENERATE_BASELINE=1` 才能重写基线 |

## P2：一致性守门（防「静默失效」）

- `tests/Scada.Collector.Tests/ProtocolRegistryConsistencyTests.cs`：把 `ProtocolType` 枚举、
  `DeviceDriverFactory.BuiltInProtocols`、构造函数里的 `_builtIns` 字典、`ProtocolCatalog.All`
  四份清单钉在一起。新增枚举而漏驱动 / 漏 spec 立即变红；IEC 61850 是唯一显式「仅插件」成员。
- `tests/Scada.Server.Tests/EndpointRegistrationTests.cs`：扫描 48 个 `MapXxxEndpoints` 定义，
  逐个断言已登记进 `EndpointRegistration.cs`，漏登记不再只是运行时 404。
- `tests/Scada.Core.Tests/AuditQueryTests.cs`、`tests/Scada.Server.Tests/SecurityBoundaryTests.cs`、
  `HaRuntimeSyncTests` 增补：三态区域解析、损坏密钥拒绝认证、凭据分桶、MCP fail-closed、
  审计失败计数、未定义 scope、HA 注入六组负例 + 复合/带引号主键参数化正例。
- **覆盖率棘轮**：`tools/coverage/coverage-gate.mjs` 新增 `--check-ratchet`（对比 base 提交的
  `coverage-baseline.json`，下降必须在 `tools/coverage/coverage-drops.json` 登记 `from/to/reason`），
  `--update` 同样拒绝未说明的下降；`.github/workflows/ci.yml` 增加一步 `git fetch base` 后执行。
  本轮已把 Collector `77.09 → 64.14` 的下降写明原因（统计口径由「单份报告取最大」改为
  「跨项目按 file+line 取并集」，分母变化）。

## 顺带修掉的既有回归

`AuditOutboxTests.SaveInsideAnExplicitTransactionDoesNotWaitForTheOutbox` 稳定超时 30.5 秒。
根因：`AuditInterceptor.CompleteAsync` 在调用方显式事务尚未提交时另开配置库连接做内联投递，
`SqlitePragmaInterceptor` 在连接打开时执行的 `PRAGMA journal_mode / auto_vacuum` 要抢库级锁，
而拦截器里的原生 `DbCommand` 不受 EF `SetCommandTimeout(2)` 约束，会一直等到默认 30 秒命令超时。
改为 `context.Database.CurrentTransaction is null` 时才内联投递；发件箱行已随业务事务落库，
提交后由后台按 `SourceId` 幂等补投。

`HealthSnapshotService` 的 1.5 秒 TTL 缓存让「已恢复」的告警继续显示：`ProjectService.SnapshotAsync`
成功时会清 `_snapshotFailure`，但紧接着查 `/api/health` 命中了旧缓存，
`ProjectApiTests.ScreenPublishSnapshotFailureIsReportedInResultAndHealth` 稳定失败。
现在把三个内存态告警来源（`SystemWatch` / webhook / `IHealthContributor`）抽成 `CurrentWarnings()`，
其字符串签名参与缓存键：**告警集合变化立即穿透 TTL**，磁盘与设备枚举等慢路径仍享受缓存。

另外两处是本轮 fail-closed 改动带出的测试契约更新，不是放宽：
`McpAccessTests.Source_Guard_Defaults_To_Loopback` 改为断言 `IsAllowed(null, []) == false`
且 `IsAllowed(null, ["*"]) == true`；`McpMissingSettingTests.Site` 显式写出
`Mcp:AllowedCidrs:0 = "*"`（TestServer 没有 `RemoteIpAddress`，本用例测的是「缺 Enabled 配置」而非来源拒绝）。
i18n 门禁在 13 分钟的全量跑期间抓到并行会话新增的 6 条中文提示，已补进 `messages.en-US.json`
（未放进欠账基线），门禁按设计生效。

## 验证

| 项目 | 结果 |
| --- | --- |
| `dotnet build Scada.sln -c Release`（独立输出目录） | 0 错误 |
| `Scada.Core.Tests` | 124 / 124 |
| `Scada.Collector.Tests` | 365 / 365（含新增 3 条协议清单用例） |
| `Scada.Data.Tests` | 27 / 27（含 outbox 事务修复） |
| `Scada.Gateway.Tests` | 80 / 80 |
| `Scada.Desktop.Tests` / `Scada.Protocols.Tests` | 96 / 96、48 / 48 |
| `Scada.Engine.Tests` | 452–453 / 453，唯一失败为 `AlarmEngineFloodTests` 时序用例，单跑通过 |
| `Scada.Server.Tests` 定向（安全边界 / 端点登记 / HA / i18n / MCP 来源 / 快照健康） | 40 / 40 |
| `Scada.Server.Tests` 全量首轮（595 项） | 591 / 595，4 项已逐一定位并修复（见上） |
| `Scada.Server.Tests` 全量复跑（596 项） | **594 / 596**；仅 `ReportSigningTests` 两条在全量并行下报「归档文件缺失」，单独跑 4/4 通过，属共享归档目录的时序 flaky（`archive.ReadCheckedAsync` 与本轮改动无调用关系） |
| 修复后定向回归 | 40 / 40（含上述 4 项） |

> 用 `-p:BaseOutputPath=<共享目录>` 编译全部项目时，`Scada.Server/appsettings.json` 会落进
> 同一个输出目录，其中的 `Kestrel:Endpoints:Http:Url=http://0.0.0.0:5080` 被 Gateway 测试的
> `WebApplication.CreateBuilder()` 读到，导致 14 条用例端口冲突。这是验证方式的副作用，
> 用各项目默认输出目录重跑为 80/80。**CI 使用默认输出，不受影响。**

## 明确未做（单独排期）

1. 服务定位器残留（`GatewayAcquisitionRuntime` 一次反查 7 类 8 次、`ReportArchiveService` 等 ≥9 类）。
2. 上帝类：`GatewayCenterService` 668 行 / `CalcJobEngine` 764 行。
3. 前端 `issues[]` 消费已由 `web/src/composables/formErrors.ts` 按 `field` 标红闭环，
   但 `Code` 的 i18n 映射是否逐个覆盖仍待对照 `messages.en-US.json` 核验。

## 协作提示

本仓库存在并行会话同时编辑 `ScadaResilience.cs`、`appsettings.json`、`coverage-gate.mjs` 等文件。
改前重读、改后 grep 旧符号是否仍被引用；全量构建优先用独立 `BaseOutputPath`，避免与运行中的
预览服务 / e2e 进程抢 `bin`、`obj` 文件锁。
