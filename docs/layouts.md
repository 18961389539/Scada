# 多屏布局与工作站

- **布局**（/config/layouts）：一个布局包含多个显示器（monitor），每个显示器是 N×M 网格，区域类型：`screen`（画面，带独立前进/后退/主页历史）、`alarmBanner`、`alarmList`（可确认）、`navigation`（画面目录，点击在首个画面区域打开）。
- **运行**（/console/:layoutId?）：未指定布局时按本机工作站解析。弹窗动作打开可拖动的浮动面板（faceplate）；区域可"在新窗口打开"，浏览器支持 Window Management API（`getScreenDetails`）时可选择目标显示器，否则退化为普通新窗口（/kiosk/screens/:id，无菜单外壳）。
- **工作站**：按名称登记，绑定默认布局及 `AllowControl`。浏览器"设为本机"后，所有请求附带 `X-Scada-Workstation` 头；`AllowControl=false` 的工作站提交控制类 POST（指令、画面动作、审批、顺控启动、配方下发）返回 403 并写审计。
- **安全说明**：工作站头由客户端声明，可被伪造，仅为纵深防御（防止大屏/看板误操作）；真正授权仍是用户的区域权限。
- 布局随工程包导出（`hmiLayouts` 段，画面以引用名转换）；被布局引用的画面不可删除。
