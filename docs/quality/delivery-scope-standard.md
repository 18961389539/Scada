# 配置管理 / 报表 / 生产管理 页面交付范围与标准

适用范围：`web/src/views/` 下三个模块的全部页面、子页面、弹窗与状态。
基线日期：2026-10-27。

## 1. 范围界定

### 1.1 配置管理（14 个页面路由）

| 优先级 | 页面 | 路由 | 批次 | 主要边界（弹窗 / 空态 / 异常） |
| --- | --- | --- | --- | --- |
| P0 | 设备配置 | `/config/devices` | 1 | 新增/编辑设备、批量启停/删除、连接测试、筛选空态、加载失败 |
| P0 | 采集网关 | `/config/gateways` | 1 | 网关新增/编辑/删除、设备分配、诊断、OPC 证书、空态 |
| P1 | 图库与模板 | `/config/hmi-library` | 1 | SVG/JSON 导入、模板编辑（嵌入画面设计器）、删除确认、加载失败 |
| P1 | 运行布局与工作站 | `/config/layouts` | 2 | 布局编辑、工作站编辑、区域拖拽/尺寸校验、版本与脏数据确认 |
| P0 | 设备对象与模板 | `/config/object-templates` | 1 | 批量生成、升级预览、冲突处理、删除确认 |
| P0 | 设备模板编辑 | `/config/object-templates/:id` | 1 | 非法/不存在 id、校验/发布/版本历史、未保存离开确认 |
| P0 | 测点配置 | `/config/tags` | 1 | 新增/编辑、批量、分页、筛选空态、加载失败 |
| P0 | 工程管理 | `/config/project` | 2 | 快照、对比、回滚影响预览、导入冲突、加载失败 |
| P1 | 计算测点 | `/config/calculations` | 2 | 编辑、删除、轮询、未保存离开确认 |
| P1 | OEE 配置 | `/config/oee` | 2 | 产线/产品/班次/原因/停机/修正/阈值 7 类弹窗、空态 |
| P0 | 报警规则 | `/config/rules` | 1 | 编辑、批量、筛选空态、加载失败 |
| P1 | 报警通知 | `/config/notifications` | 2 | 路由/通道/班次弹窗、失败重试队列、空态、加载失败 |
| P2 | 声光联动 | `/config/annunciators` | 3 | 面板编辑、删除确认、空态 |
| P1 | 对外接口 | `/config/external-api` | 3 | 密钥创建/吊销、区域授权、空态 |

### 1.2 报表（2 个页面路由）

| 优先级 | 页面 | 路由 | 批次 | 主要边界 |
| --- | --- | --- | --- | --- |
| P0 | 历史报表查询 | `/reports` | 1 | 目录加载、时间快捷段、生成、导出、失败重试、无测点空态 |
| P0 | 报表归档与验签 | `/report-center` | 2 | 模板编辑、生成、预览、验签、归档、删除确认、空态 |

### 1.3 生产管理（4 个页面路由）

| 优先级 | 页面 | 路由 | 批次 | 主要边界 |
| --- | --- | --- | --- | --- |
| P0 | 配方管理 | `/recipes` | 1 | 编辑/版本、下载预检、与实时值对比、空态 |
| P0 | 生产追溯 | `/traceability` | 2 | 任务配置、扫码绑定、SN 查询、分页、空态 |
| P0 | OEE 生产统计 | `/oee` | 2 | 时间/区域/产线/产品/班次筛选、KPI、图表、导出、无数据提示 |
| P1 | 计算任务 | `/calc-jobs` | 2 | C# 编辑器、编译/预览、超时恢复、快照、删除确认 |

### 1.4 不纳入本次范围

- 运行监控：`/overview`、`/realtime`、`/screens*`、`/console*`、`/control`、`/trends`、`/alarms`、`/alarm-analytics`
- 运维与系统：`/ops`、`/system`
- 入口与大屏：`/login`、`/kiosk/*`
- 画面设计器本体（`ScreenDesignerView`，仅在模板编辑中以嵌入方式被间接覆盖）
- 后端接口契约与数据库结构（本次仅在前端页面内优化；如发现后端缺陷另立任务）

## 2. 交付标准（量化阈值与验收方式）

| 编号 | 维度 | 量化阈值 | 验收方式 |
| --- | --- | --- | --- |
| D1 | 功能完整性与正确性 | 范围内 e2e 用例 100% 通过；每个页面至少 1 条主流程用例 | `npx playwright test tests/...`（配置 73 项、报表+生产 41 项） |
| D2 | 交互流程一致性 | 100% 页面具备：首屏加载态、加载失败可见且可重试、列表空态文案、危险操作二次确认、保存防重复提交 | 代码审查 + `ui-delivery-audit` + 失败态 e2e |
| D3 | 视觉还原度 | 仓库无设计稿；以现有设计令牌（`style.css`）与 Element Plus 主题为基线，页面截图留存；0 处横向溢出 | `test-results/ui-audit/*.png` 截图基线；4 种视口巡检 |
| D4 | 数据准确性与加载性能 | 页面 `load` ≤ 2000 ms（本地基线，实测最大 214 ms）；单页 JS 传输 ≤ 1 MB（未压缩，实测最大 878 KB）；列表轮询在页面隐藏时暂停 | `ui-delivery-audit` 的 `loadMs` 与 `jsTransferBytes`；`npm run build` 产物体积；`usePolling` 代码审查 |
| D5 | 异常与边界场景 | 0 条控制台错误 / 未捕获异常 / 4xx-5xx 未处理；非法路由参数不发起无效请求；加载失败可原地恢复 | `ui-delivery-audit`；`config-load-errors` 等失败态用例 |
| D6 | 浏览器与分辨率兼容 | Edge（Chromium 内核）下 1600×1000 / 1280×800 / 768×1024 / 390×844 均无横向溢出、无阻断性布局错误 | `ui-delivery-audit` 四视口断言 |
| D7 | 文案与术语规范 | `i18n:check` 0 问题；英文界面 0 条未翻译中文文案；每页浏览器标题唯一 | `npm run i18n:check`；`ui-i18n-audit`；巡检 `title` 唯一性断言 |
| D8 | 可访问性 | 每页有 `h1` 与 `main`；可交互按钮均有可访问名；错误提示使用 `role="alert"`；筛选控件具备 `aria-label` 或可见标签 | `ui-delivery-audit`（无名按钮=0）；Element Plus `el-alert`/`el-dialog` 语义 |

判定规则：D1–D8 全部达标视为页面通过；任一维度未达标进入下一轮，直到复验通过。

## 3. 迭代轮次记录

### 轮次 1（已完成）

待优化项清单：

1. 8 个页面首屏无加载态，空数据与“仍在加载”不可区分（图库、布局、通知、OEE 配置、OEE 统计、工程管理、设备模板编辑、对象模板）。
2. 加载失败后缺少原地重试入口，只能刷新整页。
3. 设备模板编辑对非法/不存在 id 直接请求 `/api/object-templates/NaN`，产生 404 与控制台报错。
4. 20 个页面浏览器标题全部为“SCADA 监控系统”，多标签页无法区分。
5. 批量生成对象默认选中设备列表第一台，可能协议不匹配，预览满屏“区域与设备协议不匹配”。
6. OEE 时间范围/区域/产线/产品/班次、工程对比版本、图库预览状态缺少可访问名称。
7. 计算任务 e2e 因测试服务未启用 C# 脚本能力而长期失败（环境缺陷）。

修改说明（文件）：

- `web/src/views/LayoutsView.vue`、`NotificationsView.vue`、`HmiLibraryView.vue`、`OeeConfigView.vue`、`OeeView.vue`、`ProjectView.vue`：补 `v-loading` 与加载失败重试按钮。
- `web/src/views/ObjectTemplateEditorView.vue`：新增 `validId` 前置校验与 `load()` 重试入口。
- `web/src/views/ObjectTemplatesView.vue`：按模板测点区域选择协议兼容的默认设备。
- `web/src/router/index.ts`：`afterEach` 写入“页面名 · SCADA”标题。
- `web/src/i18n/locales/zh-CN.ts` / `en-US.ts`：新增 `common.retry`、`common.notFound`、`common.timeRange`、`common.compareFrom/To`。
- `web/src/views/OeeView.vue`、`ProjectView.vue`、`HmiLibraryView.vue`：筛选控件补 `aria-label`。
- `web/tests/start-server.mjs`：启用 `--Scada:CSharpScripts:Enabled=true`，使计算任务用例可真实执行。
- 新增 `web/tests/ui-delivery-audit.spec.ts`（四视口交付巡检）与 `web/tests/ui-i18n-audit.spec.ts`（英文文案巡检）。

自检结果：

- `npm run build`：通过（vue-tsc + vite，16–18 s）。
- `npm run i18n:check`：`i18n ok: 3462 keys, 105 files`。
- `npm run check:access`：`6 policies, 18 guarded nav items, 20 guarded routes`。
- `ui-delivery-audit`：20 页 × 4 视口通过；0 控制台错误、0 页面异常、0 横向溢出、标题唯一；`load` 最大 214 ms。
- `ui-i18n-audit`：英文界面 0 条未翻译文案。
- 配置模块 e2e：73 passed。
- 报表与生产模块 e2e：41 passed。

### 轮次 2（待启动）

候选项（未在轮次 1 处理，需按 D1–D8 复验后决定是否纳入）：

1. Element Plus 按需引入，降低首屏 `index` chunk（当前 1.46 MB / gzip 500 KB）。
2. 大数据量列表分页/虚拟滚动（设备、报警规则等当前一次性渲染，依赖筛选）。
3. Firefox / WebKit 兼容性验证（当前环境仅安装 Edge）。
4. 像素级视觉回归（需提供设计稿或批准以当前截图为基线）。