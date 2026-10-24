# 区域授权与审计

本文说明资源级权限模型、各接口的强制规则、审计内容和升级行为。前端只按权限隐藏或禁用入口，**最终以服务端校验为准**。

## 模型

- **区域（Area，车间）**：`Area` 表。升级时自动建立 `#1 默认区域`，旧库所有设备的 `Device.AreaId` 默认为 1，不丢数据。默认区域不能删除；区域内仍有设备、被计算测点指定或被授权引用时也不能删除。
- **设备 → 区域**：`Device.AreaId`（必填，默认 1）。
- **测点 → 区域**：
  - 物理测点继承所属设备的区域；
  - 计算测点：可显式指定 `Tag.AreaId`；未指定时，判定权限要求对 **全部输入测点所在区域** 都有该权限（递归展开计算输入）。没有输入时归入默认区域。
  - 这样计算测点不能被用来读出无权区域的数据。保存计算测点时还要求能 **查看全部输入测点**。
- **权限（Permission）**：`View` 查看、`Control` 控制、`AckAlarm` 确认报警、`Configure` 配置、`ManageUsers` 用户与授权管理、`System` 系统维护（日志、备份、诊断）。
- **内置角色**：语义不变，视为“全部区域”授权。
  - `Viewer` = View；`Operator` = View + Control + AckAlarm；`Admin` = 全部。
  - 新增 `None`：没有全局角色，只通过区域授权获得权限。
- **自定义角色（AccessRole）**：一组权限。通过 `UserRoleAssignment(UserId, RoleId, AreaId?)` 分配给用户；`AreaId` 为空表示全部区域。一个用户可有多条授权，最终权限为内置角色与全部授权的并集。

## 判定规则

- `Perm:X` 策略：至少在一个区域拥有 X，才可进入接口；接口内再按具体对象的区域细判。
- `Global:X` 策略：要求“全部区域”授权（内置角色或 AreaId 为空的自定义授权）。
- 模型中找不到的对象（刚创建或已删除）只对全局授权开放，其余一律拒绝。
- 授权模型缓存 3 秒，配置/授权变更时立即失效；网关命令授权下发、工程回执和 OPC 导入落库时直接查库复核，不用缓存。

## 接口强制一览

| 接口 | 规则 |
|---|---|
| `GET /api/realtime` | 只返回有 View 的测点（无权的直接过滤，不报错） |
| `GET /api/history`、`/api/reports`、`/api/reports/export` | 请求的测点必须全部有 View，否则 403 |
| `GET /api/alarms` | 按报警的测点（或设备）区域过滤 |
| `POST /api/alarms/{id}/ack` | 需该报警区域的 AckAlarm，否则 403 并审计 |
| `POST /api/commands`、画面运行 `POST /api/screens/{id}/actions/{node}` | 需目标测点区域的 Control，否则 403 并审计；网关下发和执行授权时再按库复核 |
| `GET /api/devices`、`/api/tags`、`/api/alarm-rules`、`/api/calculated-tags` | 只返回有 View 的对象；单个对象无权时 404 |
| 设备增改删 | 需设备所在区域的 Configure；改区域需新旧两个区域都有 Configure |
| 测点增改删、报警规则增改删 | 需测点（及目标设备）区域的 Configure |
| 计算测点增改删/预览 | 见上文计算测点规则 |
| `POST /api/devices/{id}/test` | 需设备区域的 Control（网关工程回执再按库复核） |
| 采集诊断/重连、OPC 浏览/导入/证书（设备级） | 需设备区域的 Configure |
| 测点 Excel 导出 | 只导出有 Configure 的区域；**导入要求全局 Configure** |
| 网关管理、设备模板、画面组态（草稿/发布）、OPC 全局证书、区域增删改 | 全局 Configure |
| 用户、角色、授权 | 全局 ManageUsers（防止区域管理员给自己提权） |
| 操作日志、备份、诊断包 | 全局 System |
| SignalR `Subscribe` | 只登记有 View 的测点并返回实际生效的列表；**推送时按当前授权逐条再过滤**，被撤销授权或设备换区域后无需重连即停止推送 |
| SignalR 报警推送 | 只发给对该报警区域有 View 的连接 |

`GET /api/auth/me`、登录响应和 `GET /api/auth/access` 返回 `access: { global: [...], areas: [{ areaId, permissions }] }`，供前端控制菜单和按钮。

## 审计

`OperationLog` 新增 `TargetType`、`TargetId`、`Details`：

- 配置类变更（设备、测点、计算测点、报警规则、用户、角色、用户授权、区域）在 `Details` 写字段级差分：
  `{"op":"create|update|delete","changes":{"字段":{"old":...,"new":...}}}`，只列出变化的字段；口令/密钥类字段只显示 `***`。
- **被拒绝**：区域级拒绝和策略拒绝（非 GET）都记一条，`Result` 以“拒绝：”开头。
- **失败**：设备/测点/报警规则/用户/角色/区域/授权等写接口返回 400/404/409/422 时记一条，`Result` 以“失败：HTTP xxx”开头并带服务端消息。
- 读请求被拒只返回 403，不写审计，避免刷屏。
- 系统管理 → 操作日志里可点开“变更明细”查看差分 JSON。

## 升级

迁移 `AddAreasRolesAndAudit`：

- 新建 `Area`（插入默认区域）、`AccessRole`、`UserRoleAssignment`；
- `Device` 追加 `AreaId`（默认 1）、`Tag` 追加 `AreaId`（可空）、`OperationLog` 追加三列；
- 区域列不建数据库外键，旧库升级只追加列，不重建设备/测点表；区域存在性与删除约束由接口检查。
- 已有用户的内置角色不变，升级后行为与之前一致（回归测试覆盖）。

## 已知限制

- 画面本身不分区域：所有人可看到已发布画面的结构，但实时值、控制动作都按测点区域过滤/拒绝。
- 网关、设备模板与画面组态是跨区域的工程对象，只开放给全局配置权限。
- 报警列表对区域受限用户“先多取再过滤”，单次最多取 5000 条；极端情况下返回条数可能少于请求上限。
- 后台定时任务（报警评估、历史归档、报警 webhook）按系统身份运行，不受区域授权限制；webhook 外发不按区域分流。
