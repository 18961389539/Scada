# Scada.Core 改进记录（2026-11-14）

范围：`src/Scada.Core`（116 个 .cs，约 8.8k 行，零 NuGet 依赖）。按"先修看得见的错，再收重复的规则，
最后补门禁"的顺序处理；每条都能落到具体文件与用例上。

本文同时是**已落地**、**有意不做（含原因）**与**待验证**三部分的记录。

---

## 1. 总体判断

Core 的分层方向没问题：它是全仓唯一的共享内核，实体/校验/协议规格/表达式引擎/许可都在这里，
`Data`/`Engine`/`Collector`/`Server` 单向依赖它。真正的问题不是"缺功能"，而是三张**手写清单**和一条
**无对象的校验通道**：

| 结构 | 证据 | 后果 |
| --- | --- | --- |
| `ScreenExpander.Expand` 逐字段抄 44 个属性 | `Models/ScreenModels.cs` | 漏抄 `Hidden`，发布后隐藏图元变可见（已修，见 2.1） |
| `ApplyEditableFrom` 三份字段清单 | `Tag` 20 项 / `Device` 27 项 / `AlarmRule` 23 项 | 新增字段不报错，只表现为"保存后值丢了"（已加守门用例） |
| 校验只返回 `string?` | `ConfigurationValidator`、12 个 `IProtocolSpec` | 英文站点弹中文；一次只报一个错（已对象化） |
| 出站安全有两套 CIDR | `Net/IpRule.cs` vs `Protocols/OutboundTargetPolicy.cs` | 边界处理已经分叉（已合并） |
| 口令哈希无算法标识 | `Security/PasswordHasher.cs` | 无法升级算法；损坏哈希把登录打成 500（已修） |
| 审计脱敏是硬编码名单 | `Security/AuditDiff.cs` | 新加 `ApiKey` 默认不脱敏（已改特性驱动） |

---

## 2. 已落地

### 2.1 模板展开丢失隐藏标记（P0，行为缺陷）

`Expand` 用对象初始化器重建节点时没有复制 `Hidden`，模板实例自身的 `Hidden` 也被忽略。运行时是按
`node.hidden` 过滤的（`web/src/components/ScreenCanvas.vue:321`），而发布路径
（`Screens/ScreenPublication.cs`）直接 `Expand` 后固化，于是：

- 组态时隐藏的模板实例 → 发布后整块显示；
- 模板内部隐藏的图元 → 展开后按可见渲染。

修复：`Expand` 跳过 `Hidden == true` 的模板实例，并把 `inner.Hidden` 复制到展开节点；`Locked`/`GroupId`
明确丢弃并注明原因（编辑态字段，展开后不再被设计器操作）。同时把 `Texts`/`Points`/`States`/`Commands`/
`GaugeBands`/`TableColumns`/`ChartOptions` 改为新建集合——此前发布物与图库模板共享同一个 `List`，
后续改模板会"隔着引用"改掉已发布画面。

用例：`tests/Scada.Core.Tests/ScreenExpanderTests.cs`（4 条）。

### 2.2 字段复制完整性守门

`EditableFieldCopyTests` 用反射断言：`Tag`/`Device`/`AlarmRule` 除显式排除清单外的所有可写属性，
经 `ApplyEditableFrom` 后都必须与源对象一致；遇到未覆盖的属性类型直接失败。
`ScreenExpanderTests.ExpandCopiesEveryRuntimeField` 对 56 个 `ScreenNode` 属性做同样的事（按 JSON 比对），
`Hidden` 那条就是这样被钉住的。

新增字段若不复制、也没进排除清单，`dotnet test` 立刻变红——不再靠"记得改"。

### 2.3 校验结果对象化

新增 `Models/ValidationIssue.cs`：`(Code, Field, Message)` 三件套。`ConfigurationValidator` 增加
`ChannelIssues`/`DeviceIssues`/`TagIssues`/`AlarmIssues`，独立问题一次全部返回；旧的
`Device`/`Tag`/`Channel`/`Alarm` 保留为"取首条文案"的包装，24 处 `is string error` 调用点零改动。

错误码稳定（`device.name`、`tag.text.length`、`alarm.message.placeholderOnly`…），界面可据此本地化与
定位字段，不再匹配中文文案。协议规格（12 个 `IProtocolSpec`）仍然返回字符串，由包装层统一成
`device.protocol.<Protocol>` / `tag.address.<Protocol>`，避免一次性改写 12 个文件。

顺带把重复了 5 次的串口校验收进 `Protocols/SerialSettings.cs`（通道、设备、汇川、信捷、台达共用）。

### 2.4 出站安全统一到 IpRule

`OutboundTargetPolicy` 删除自己那份 CIDR 实现，改为调用 `IpRule.IsMatch`；只保留"主机名/通配/方括号"的
语义决策。新增交叉用例：同一组规则两个入口必须一致。
同时用一条用例把"白名单是额外放行、不是仅允许"这个容易误解的语义钉住。

### 2.5 口令哈希加固

- 存储格式改为 `pbkdf2-sha256$<迭代>$<盐>$<密钥>`，算法名进哈希串，将来换 Argon2id 可逐条升级；
- 迭代数 100k → 600k（新口令），校验时允许 ≥100k 的存量；`NeedsRehash` 给登录成功路径用
  （`AuthEndpoints` 已接入，用户无感原地升级）；
- `Verify` 对格式损坏、迭代越界、盐/密钥长度异常、未知算法一律返回 `false`。此前 `iterations=0`
  会让 `Pbkdf2` 抛 `ArgumentOutOfRangeException`，登录接口直接 500。

用例：`SecurityTests.PasswordHasherTests`（含 9 组损坏哈希）。

### 2.6 审计脱敏改为特性驱动

新增 `[Sensitive]`（`Security/SensitiveAttribute.cs`），`AuditDiff` 按类型反射收集，保留旧名单兜底
（对象是 `JsonElement` 时拿不到类型）。已标注 11 个字段，包括扫描时新发现的 `DeviceLink.OpcPasswordProtected`、
`GatewayCommandGrant/Receipt.ExecutionToken`、`GatewayDeviceConfiguration.OpcPassword`。

`SecurityTests.AuditSensitivityTests.EverySecretNamedPropertyIsMarkedSensitive` 是 fail-closed 扫描：
名字像密钥的字符串属性必须带特性，否则失败——新加一个 `ApiKey` 不会再默认进审计日志。

### 2.7 权限模型递归加深度上限

`AccessModel.Build` 的依赖解析此前无深度限制；工程包可以导入任意深度的计算测点链，而权限模型在
启动/刷新路径上构建，一条构造过的万级链会栈溢出（进程崩溃，不是可捕获异常）。
新增 `MaxDependencyDepth = 64`（与 `CalculationGraph` 一致），超限按默认区域处理（最小权限）。
用例：5000 层链 + 环依赖。

### 2.8 许可判定下沉到 Core

新增 `Licensing/LicenseEvaluation.cs`：机器码归一化与比对、到期、剩余天数、时钟回拨、到期预警。
`LicenseService` 里重复两遍的机器码比较、三处天数计算、回拨判断全部改为调用它；状态机与文案仍在
Server（涉及落盘与对外提示）。`MachineFingerprint.Normalize` 委托给 Core 的同一实现。
用例：`LicenseEvaluationTests`（13 组边界）。

### 2.9 配置启动期校验

新增 `Options/ScadaSettingsValidation.cs`，覆盖 102 个配置项里的范围与互斥关系：0 并发/0 超时、
负保留天数、暂存总容量小于分段、分析点数超过查询上限、备份目标缺路径、通知重试上限小于首次间隔、
时区名不存在等。`Program.cs` 在绑定配置后立刻调用，失败时写 `startup.log` + 弹窗 + 退出码 2，
一次列出全部问题。范围刻意保守（测试里的 `PushIntervalMs=100`、`HistoryQueryTimeoutSeconds=1` 等仍然合法）。

### 2.10 协议注册表可扩展

`ProtocolCatalog.Register(spec, replace)` 允许插件在启动阶段注册协议规格；覆盖已注册协议必须显式
`replace: true`（与驱动覆盖的 SHA-256 允许列表同一强度）。内置 12 个协议仍是默认集合。

### 2.11 演示数据移出 Core

`Demo/signals.json`、`Demo/traceability.json` 与 `DemoSignal(s)` 移到 `Scada.Data`（`Demo/`），
采集器与网关的发布包不再携带这两份 JSON；模拟器按需引用 Data。

### 2.12 独立测试项目与门禁

新增 `tests/Scada.Core.Tests`（零宿主依赖，77 条用例），加入解决方案与 CI 采集循环。
新增 `ArchitectureTests`：`Scada.Core.csproj` 不允许出现 `PackageReference` 或 `ProjectReference`——
零依赖是插件只引用 Core 的前提，现在有东西守着了。

---

## 3. 有意不做（含原因）

| 项 | 为什么不做 |
| --- | --- |
| 拆 `Scada.Domain` / `Scada.Hmi` / `Scada.Contracts` | `ScreenModels.cs`（481 行）确实自成一体，可以单独抽 `Scada.Hmi`；但其余拆分要改 17 个 csproj 的 using，收益小于一次大 diff 的风险。等 P0/P1 稳定后单独排期。 |
| 实体与"EF 实体 / 接口契约 / 工程包格式"三identity解耦 | 成本远高于收益，除非后续要做外部 SDK 或多租户。当前用 `[JsonIgnore]` + `ApplyEditableFrom` 已经能控制导出面。 |
| `ScadaSettings` 按域拆成 `AlarmOptions`/`HistoryOptions`… | 102 个属性被约 500 处调用点直接读取，改名属于纯机械大 diff；本轮先把"启动期校验"补上，拆分留到有明确收益（如 per-section 热更新）时再做。 |
| `CalcJob` 12 对草稿/已发布字段改成 owned type | 需要 EF 迁移 + 接口契约 + 前端同步改，属于独立的schema 变更；本轮只记录问题（漏抄一组就"发布不生效"）。 |
| `ScadaTime.NowMs()`（389 处）全面改 `TimeProvider` | 纯替换没有行为收益，且会淹没真正的改动。新代码（许可评估）已经改成显式传入 `now`。 |
| 枚举统一挪进 `Enums/` | 纯文件搬移，`git blame` 价值大于可读性收益。 |
| 装配 `ProtocolForm`（81 个字段、13 个字面量 label）里的 i18n 键 | 键名（`protocols.k920416`）由生成器产出，改名要同步前端 4172 个键与 `i18n:check`；留给"协议表单生成化"单独做。 |

---

## 4. 验证

| 项 | 结果 |
| --- | --- |
| `dotnet build Scada.sln -c Release` | 0 错误 / 0 警告 |
| `dotnet test tests/Scada.Core.Tests -c Release` | **77 / 77** 通过 |
| `Scada.Collector.Tests` / `Scada.Engine.Tests` | 362 / 362、445 / 445 |
| `Scada.Data.Tests` / `Scada.Gateway.Tests` | 27 / 27、71 / 71 |
| `Scada.Server.Tests`（按类分批） | PasswordChange + License + Hardening 21 / 21；McpRateLimit + HaCluster 2 / 2 |
| 新增用例覆盖 | 模板展开 4、字段复制 3、校验 6、安全 15、出站 8、权限 4、许可 13、配置 10、注册表 3、架构 1 |

> 说明：`Scada.Server.Tests` 全量 541 项单次需 8～15 分钟，本轮按类分批验证，未再跑完整一轮（并行会话
> 曾两次因超时中断）。首轮全量的 3 个失败已逐一定位：MCP 的 429 缺 `Retry-After`、HA 心跳被全局限流
> 拦截（均已修，见工作日志），以及 `PasswordChangeTests` 在 `DisposeAsync` 里的一次 NRE（单跑通过，
> 属并行资源争用）。另修正 `HistoryReliabilityTests` 的严格 JSON 解析——`appsettings.json` 允许 `//`
> 注释，直接解析它的测试必须用 `JsonDocumentOptions { CommentHandling = Skip }`。
> Core 的验证不依赖这些宿主级用例，`Scada.Core.Tests` 已独立覆盖全部新增逻辑。

## 5. 后续（需要决策）

1. **覆盖率基线**：Core 当前 69.31%，是除 `Gateway.Host` 外最低。新测试项目接入后应复测并把基线棘轮上去。
2. **校验码接到前端**：`ValidationIssue.Code` 已经稳定，前端表单可以按 `field` 标红、按 `code` 本地化；
   需要一并决定错误响应的形状（现在仍是 `{ message }`）。
3. **`ScreenNode` 联合类型**：56 个可空属性覆盖 46 种图元，`Expand` 与 `ScreenValidator` 都在维护同一份
   字段语义。真正的解法是按类型拆分（多态 JSON），但会改动画面格式与设计器，需要单独立项。
