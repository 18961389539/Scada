# 易用性审计

审计对象：`web/src`（Vue 3 + Element Plus 前端）。
审计方式：按操作员真实路径（看数据 → 发现问题 → 确认报警 → 追查趋势 → 处置/配置）逐页读源码定位摩擦点。
**未做运行时验证**：本次审计期间沙箱不可用（`dotnet`/`npm`/`git`/`rg` 全部无法执行），结论全部来自静态阅读，落地前需按第 5 节复验。

---

## 1. 现状基线（已具备，不要重复建设）

先记录已经做得好的部分，避免后续"改进"变成返工：

| 能力 | 位置 |
| --- | --- |
| 命令面板 + 收藏 + 中英别名模糊搜索 | `components/nav/CommandPalette.vue`、`nav/catalog.ts`、`nav/favorites.ts` |
| 33 条路由的逐页帮助（含页内搜索高亮） | `components/PageHelpButton.vue`、`help/pages.ts` |
| 配置类页面筛选条件 + 页码进 URL | `composables/useCatalogQuery.ts`（DevicesView、TagsView 已用） |
| 数据陈旧/断连/降级/HA Standby 全局提示 | `layout/AppLayout.vue:33-47`、`overview-stale` |
| 批量操作进度 + 失败明细 + 重试 | `composables/batchRunner.ts`、`AlarmsView.vue:81-86` |
| 危险操作二次确认 + 表单级错误定位 | `AlarmsView.vue:727-736`、`composables/formErrors.ts` |
| 移动端卡片降级 | `OverviewView.vue:91-128`、`RealtimeView.vue:85-95` |
| 首次上手引导（检查项 + 诊断入口） | `components/SetupGuide.vue` |
| 双语、主题、密度、a11y 属性 | `AppLayout.vue`、`theme.ts`、各 `aria-label` |

---

## 2. P0——高频路径上的硬摩擦

### P0-1 报警页没有任何键盘操作

**现象**：报警确认、写备注、暂挂、看时间线、翻页全部依赖鼠标点击；表格行无法用键盘选中。

**证据**
- `views/AlarmsView.vue` 全文件无 `@keydown` / `tabindex` 行级处理；表格仅在 `el-table-column type="selection"` 下有复选框。
- 全局只注册了一个快捷键：`layout/AppLayout.vue:347-353`（Ctrl/⌘+K 打开命令面板）。

**影响**：报警洪峰时操作员要处理几十条，鼠标点击 + 移动 + 二次确认的路径过长；ISA-101 / WinCC / Ignition 一类 HMI 的通行约定是 ↑↓ 选行、A 确认、空格勾选、Enter 打开详情。现场戴手套或用触控板时差距更明显。

**建议改法**（不引入新依赖）
1. 给 `el-table` 加 `highlight-current-row` + `ref.scrollToRow`，维护 `activeRowIndex`；`↑/↓` 移动、`Space` 切换该行勾选、`Enter` 打开时间线。
2. `A` 对当前行发起确认（复用 `openAck`），`Shift+A` 对已勾选批量确认（复用 `batchAck`）。
3. 快捷键仅在焦点不在输入框/下拉内生效，且页面处于 `tab === 'live'`。
4. 表格下方加一行极简提示（`A 确认 · Enter 详情`），可复用 `.alarm-scroll-hint` 样式位。

**工作量**：中（约 120 行，集中在 `AlarmsView.vue`，可抽 `composables/useRowKeyboard.ts` 供 ControlView 复用）。

---

### P0-2 监视/分析页的视图状态不进 URL，刷新即丢、也没法交接（✅ 已落地）

**现象**：报警页选了「Critical + 关键词 + 第 3 页」，趋势页选了 5 个测点 + 8 小时区间，刷新或分享链接后全部回到默认。

**证据**
- `AlarmsView.vue:381-384`：只**读** `route.query.state`，用户点击状态筛选（`:11-27`）时不写回 URL；`levelFilter`/`categoryFilter`/`keyword`/`livePage` 完全不进 URL。
- `TrendsView.vue:527-546`：只**读** `route.query.tagId`；`selectedTags`、时间范围、自动刷新开关均不写回。结果是从报警页跳过来的单测点视图无法扩展成可分享的多测点视图。
- 对照组：`composables/useCatalogQuery.ts:17-29` 已经把筛选 + 页码写回 URL，`DevicesView.vue:17-33`、`TagsView.vue:5-11` 都在用。

**影响**
1. F5 后丢失上下文（现场排查时很常见）。
2. 班组交接只能用口头描述「你点报警页，然后选 Critical」，无法发链接。
3. 同一套产品里两种交互口径，用户学两遍。

**建议改法**：把 `useCatalogQuery` 推广到 `AlarmsView`（`state/level/category/keyword/page`）与 `TrendsView`（`tagIds` 逗号分隔、`from/to`、`preset`）。`useCatalogQuery` 当前只处理 `Record<string,string>`，需要扩一个 `syncArray` 变体或让 Trends 自己做 `router.replace`（注意用 `replace` 而非 `push`，避免污染历史栈）。

**工作量**：小-中（每页 30-50 行）。

---

### P0-3 权限不足与无效地址都是"静默跳走"（✅ 已落地）

**现象**：用户点开别人发的链接，或者地址栏打错，页面悄悄变成总览页，没有任何说明。

**证据**
- `router/index.ts:112-114`：策略不通过直接 `return defaultLandingPath(auth.user)`，无提示、无 toast。
- `router/index.ts:83`：`{ path: '/:pathMatch(.*)*', redirect: '/' }`，未知路径吞掉后同样落到总览。

**影响**：最常见的现场投诉形态是"我这个页面打不开/不见了"。运维和班组长无法自助判断是"没权限"还是"链接过期"，只能找管理员。

**建议改法**
1. 未知路径改为一个轻量 404 页（`views/NotFoundView.vue`），给出「返回总览 / 打开命令面板」两个出口。
2. 策略不通过时 `router.replace({ path: landing, query: { denied: to.fullPath } })`，`AppLayout` 读到 `denied` 后弹一次 `ElMessage.warning`（一次性，读取后清掉 query），文案带上目标页标题。

**工作量**：小（约 60 行，新增 1 个 view + AppLayout 挂一个 watcher）。

---

## 3. P1——明确影响效率或一致性

### P1-1 报警页在移动端没有卡片降级，7 列表格硬塞进手机（✅ 已落地）

**证据**：`AlarmsView.vue:991-995` 在 `max-width: 768px` 下只把 `.alarm-table-wrap` 高度限制成 `clamp(280px, 50vh, 520px)`，仍然渲染 `el-table`（`:125-206`，含状态/等级/类别三枚标签、来源、值、起止时间、确认人、5 个行内按钮）。
**对照**：`OverviewView.vue:91-128`、`RealtimeView.vue:85-95` 都有专门的 `.mobile-list` 卡片布局。
**建议**：照 Overview 的 `mobile-list` 结构补一版报警卡片（等级色条 + 消息 + 来源 + 持续时长 + 「确认」主按钮），桌面表格保持不动。这是报警页在手机/平板上可用的前提。
**工作量**：中（约 100 行模板 + 复用现有样式类）。

### P1-2 活动报警的「已持续」不会走动

**证据**：`AlarmsView.vue:534-540` 的 `duration()` 用 `Date.now()` 计算，但页面没有引入 `useNowTicker`；只有推送或 30s 轮询（`:860`）触发重渲染时才更新。
**对照**：`RealtimeView.vue:151,173`、`SetupGuide.vue:41,54` 都用了 `useNowTicker`；`ControlView.vue:302` 用自己的 `now` ref 驱动倒计时。
**影响**：报警持续时长是判断严重程度的核心依据，停着不动会让人误判「刚发生」或「已经很久」。
**建议**：引入 `useNowTicker()`，`duration()` 读 `now.value`。1 行 import + 1 行改动。
**工作量**：极小。

### P1-3 趋势选点器撑不住大点数站点

**证据**：`TrendsView.vue:5-18` 是单个 `el-select multiple filterable`，选项是**全量** `tags`，硬上限 `:multiple-limit="50"`，标签文案 `设备名 / 测点名` 在渲染时才拼。
**影响**：几千点位的站点打开下拉会明显卡顿；用户找点只能靠记名字；常用组合每次重选；50 上限是硬编码，超了静默拒绝。
**建议**（分两步，先做 1）
1. 加「最近使用测点」（`localStorage` 按用户存最近 10 个 tagId，复用 `nav/favorites.ts` 的写法），下拉顶部固定展示。
2. 若点位规模确实上千：改成按设备分组的 `el-option-group`，或做远程搜索（`filter-method` + 分页取 tag）。
**工作量**：第 1 步小（约 40 行）；第 2 步中。

### P1-4 趋势页日期控件写死 360px，小屏溢出

**证据**：`TrendsView.vue:31-38` 内联 `style="width: 360px"`。
**对照**：`AlarmsView.vue:938` 的处理是 `flex: 0 1 390px; width: 390px; min-width: 240px; max-width: 100%`，并在 `:1002-1009` 为移动端重排了日期面板。
**建议**：把内联样式换成 class，套用 AlarmsView 的响应式尺寸 + 移动端 popper 规则（可直接复制 `alarm-date-popper` 那段，考虑提成全局 `.scada-date-popper`）。
**工作量**：小。

### P1-5 帮助文档只能搜当前页，33 篇无法跨页检索

**证据**：`PageHelpButton.vue:99-113` 只按当前路由加载一个 key 的内容；`:140-177` 的搜索只在该页 `bodyLines` 上做。
**影响**：用户知道"有个设置要改"但不知道在哪个页面时，帮助系统帮不上忙——这正是帮助系统最该发挥作用的时刻。命令面板搜的是页面标题，不是帮助正文。
**建议**：帮助抽屉加一个「搜索全部帮助」开关：加载整份 `help/zh.json`（本地 JSON，体积可接受），跨 key 检索并列出「页面标题 + 命中行」，点击跳转该页并自动打开其帮助。
**工作量**：中（约 80 行）。

### P1-6 命令面板无"最近访问"，35 个页面里找路靠背（✅ 已落地）

**证据**：`CommandPalette.vue:114-137` 分组只有「收藏」和「全部页面」；`nav/favorites.ts` 只有 `loadFavorites/toggleFavorite`，没有访问记录。
**影响**：收藏是主动动作，多数人不会用；「最近访问」是被动、零成本的，对页面多的产品收益更高。
**建议**：`AppLayout` 的 `afterEach` 里记录最近 8 条到 `localStorage`（按用户名），命令面板空查询时插到「收藏」后面。约 20 行。
**工作量**：小。

---

## 4. P2——打磨项

| 编号 | 问题 | 证据 | 建议 |
| --- | --- | --- | --- |
| P2-1 | Critical 报警弹窗不可点击跳转，只能关闭 | `AppLayout.vue:292-309` | 弹窗主体点击 → `router.push('/alarms?state=unack&tagId=…')`；保留×关闭。 |
| P2-2 | 登录页无大写锁定提示 | `LoginView.vue:12-22` | 密码框 `keydown` 检查 `getModifierState('CapsLock')`，命中时在框下显示一行提示。 |
| P2-3 | 报警直播页固定 100 条/页，历史页可选 50-500 | `AlarmsView.vue:355` vs `:224-226` | 直播页也给 pageSize 选择器，或至少跟历史页共用同一常量来源。 |
| P2-4 | 无 `?` 快捷键一览 | `AppLayout.vue:347-353` | 增加 `?` 打开快捷键帮助浮层（内容随 P0-1 落地后有实际可列项）。 |
| P2-5 | 未知路由无 404，权限拒绝无提示 | 同 P0-3 | 合并到 P0-3 一起做。 |

---

## 5. 建议落地批次

| 批次 | 内容 | 目的 | 预估 |
| --- | --- | --- | --- |
| B1 | P0-3 + P1-2 + P1-4 + P2-3 | 低成本、当天可见：错误不再静默、时长会走、小屏不溢出 | 小 |
| B2 | P0-2（Alarms + Trends 状态进 URL） | 刷新不丢、能交接；同时统一交互口径 | 中 |
| B3 | P0-1（报表键盘操作） | 报警洪峰吞吐量提升，收益最大但需要交互设计确认 | 中 |
| B4 | P1-1（报警移动端卡片）+ P1-3 第 1 步 + P1-6 | 现场移动端可用性 | 中 |
| B5 | P1-5（跨页帮助搜索） | 自助率提升 | 中 |

B3 涉及交互约定（键位选取、是否要"确认并下一条"流程），建议先出一版键位表确认再写代码。

## 6. 复验清单（沙箱恢复后执行）

1. `npm run build`（含 `vue-tsc`）——确认无类型回归。
2. `npm run i18n:check`——新增文案必须同时进 `zh-CN.ts` / `en-US.ts`。
3. `npm run check:access`——若改路由策略提示，需回归权限用例。
4. `npm run test:smoke -- --repeat-each=3`——重点覆盖 `alarms`、`trends`、`realtime` 三页。
5. 手工：报警页刷 URL 往返、趋势页多测点复制链接给另一账号、无权限账号访问 `/system`、手机宽度 375px 下报警列表。

## 7. 不建议做的

- **不要**给所有表格加虚拟滚动来"提升易用性"——当前分页 100 条已足够，收益低、回归面大。
- **不要**重做导航结构。`nav/catalog.ts` 单一数据源、命令面板与侧栏共用，是目前最健康的部分。
- **不要**为移动端单独做一套交互流程。现有"桌面表格 + 移动卡片"的分叉模式已经够用，报警页补齐即可。

---

## 8. 第一轮落地记录（2026-11-01）

已实现 P0-2、P0-3、P1-2、P1-4。

| 项 | 改动 | 文件 |
| --- | --- | --- |
| P0-3 未知路由 | 新增可解释的 404 页（显示实际访问的地址，给出"返回总览 / 打开命令面板"两个出口），替换原来的静默 `redirect: '/'` | `views/NotFoundView.vue`（新增）、`router/index.ts:81-82` |
| P0-3 权限不足 | 守卫改为带 `denied=原地址` 跳转，`AppLayout` 提示一次后清参数；落地页自身被拒时不再重定向（防无限重定向） | `router/index.ts:111-118`、`layout/AppLayout.vue:157-166`、`:383` |
| P1-2 持续时长 | 报警页接入 `useNowTicker`，"已持续"每秒走动 | `views/AlarmsView.vue:421-422`、`:575` |
| P1-4 日期溢出 | 趋势页日期控件由内联 `width:360px` 改为可收缩 class + 小屏 popper 规则（对齐报警页做法） | `views/TrendsView.vue:36-37`、`:650-658` |
| P0-2 报警页状态 | `state/level/category/keyword/sort/page` 写入地址栏并在初始化时读回；页码被自动纠正时同步 | `views/AlarmsView.vue:408-422`、`:459-473`、`:487`、`:614`、`:709`、`:749` |
| P0-2 趋势页状态 | `tagIds`/`preset`/`from`/`to` 写入地址栏，刷新与分享可还原多测点视图；空选择写 `tagIds=` 以免刷新回退 | `views/TrendsView.vue:532-597`、`:392`、`:345`、`:634` |

### 第二轮（2026-11-01，下午）

| 项 | 改动 | 文件 |
| --- | --- | --- |
| P1-1 报警移动端 | ≤768px 隐藏 7 列表格，改用卡片：等级/状态标签 + 持续时间 + 消息 + 来源/数值/时间 + 确认人 + 确认/备注/时间线按钮 + 趋势链接；Critical 未恢复卡片加左侧色条 | `views/AlarmsView.vue`（模板 `.alarm-mobile-list`、样式 `.alarm-card*`） |
| P1-6 最近访问 | 新增 `nav/recents.ts`（按用户存最近 8 条，`localStorage`）；`AppLayout` 在导航后按导航目录归一并记录；命令面板空查询时插在收藏下方，且与收藏去重 | `nav/recents.ts`（新增）、`layout/AppLayout.vue`、`components/nav/CommandPalette.vue` |

`nav.recent` 文案键此前已存在于 `zh-CN.json` / `en-US.json`（一直未被使用），本轮直接复用，未新增 i18n 键。

两个实现细节值得记住，改动时别踩回去：

1. **报警页 `tagId` 与 `syncUrl` 存在竞态**：清除测点筛选和写回地址栏是两条异步导航。`syncUrl` 用 `tagFilterClearing` 标志在这段时间内不再把 `tagId` 加回 URL（`AlarmsView.vue:377-384`、`:461`）。
2. **趋势页不要动 `tagId`**：`tagId` 由报警/实时页入口写入，`syncUrl` 只增不改。若在写 `tagIds` 时删掉 `tagId`，会触发 `watch(() => route.query.tagId)` → `applyTagContext()` → `clearResults()`，把正在进行的查询取消掉。

文案键新增在 `i18n/locales/zh-CN.ts` 与 `en-US.ts` 的 `uxShell` 段（`notFoundTitle/notFoundBody/notFoundBack/notFoundSearch/deniedMessage`）。

### 验证结果

以下为两轮改动合并后的最终结果。

| 检查 | 结果 |
| --- | --- |
| `vue-tsc -b` | 通过（无输出） |
| `vite build`（输出到临时目录） | 通过，3440 modules transformed，PWA precache 195 entries |
| `node scripts/i18n-check.mjs` | `i18n ok: 4174 keys, 148 files` |
| `node scripts/access-check.mjs` + 单测 | `access ok: 6 policies, 20 guarded nav items, 22 guarded routes`；5/5 通过 |
| Playwright 冒烟 | **未执行**：`npm run build` 清理 `dist/assets` 时被本机安全删除守卫拦截（188 个文件 > 阈值 50），属环境限制而非代码问题 |

### 剩余清单

未做的项仍按第 5 节批次排列：P0-1（报警键盘操作，需先定键位）、P1-3（趋势选点最近使用）、P1-5（跨页帮助搜索）、P2-1/P2-2/P2-3。其中 B3（键盘操作）建议先出键位表再动手。

