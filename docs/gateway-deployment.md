# 独立网关运行说明

当前已有远程采集、持久补传和受控远程命令进程。控制与回执见 [remote-control.md](remote-control.md)。远程 OPC 浏览/证书管理、详细诊断、设备离线报警和管理界面仍在实施，完整范围见 [remote-gateway.md](remote-gateway.md)。独立进程已经在本机通过实际中心、OPC UA 模拟器及 TCP 报文计数验证；Windows 服务安装、真实 PLC/串口、满盘、掉电、长稳与规模验收尚未完成。

## 中心准备

1. 启动已升级配置数据库的中心，并配置具有可信证书的 HTTPS 地址。
2. 管理员通过 `POST /api/gateways` 创建网关，保存返回的编号及一次性密钥；详情和列表不会再次返回密钥。
3. 停用目标设备并等待原采集任务完全退出，通过 `POST /api/gateways/assignments/{deviceId}` 分配 `gatewayId`，再启用设备。设备的网络地址、串口、PLC 设置均以现场网关所在主机为准。
4. OPC UA 的客户端证书和信任库位于现场网关，不使用中心的 PKI。网关在线确认配置后，在设备页面“OPC UA 证书”选择该网关，核对指纹并信任服务器、下载客户端公钥供服务器信任；详见 [远程工程操作](remote-engineering.md)。网关不会自动信任未知证书或降低安全策略。

## 发布及控制台启动

在仓库根目录发布 Windows x64 的框架依赖版本，目标主机需要 .NET 10 运行时：

```powershell
dotnet publish .\src\Scada.Gateway.Host\Scada.Gateway.Host.csproj -c Release -r win-x64 --self-contained false -o .\publish\gateway
```

使用环境变量启动，不把密钥写入命令行参数或 URL。以下编号和地址须替换为实际值；密钥通过隐藏输入取得：

```powershell
$env:GatewayAgent__ServerUrl = 'https://scada.example.com/'
$env:GatewayAgent__GatewayId = '1'
$env:GatewayAgent__DataRoot = 'D:\ScadaGatewayData\edge-1'
$env:GatewayAgent__Token = [System.Net.NetworkCredential]::new('', (Read-Host '网关密钥' -AsSecureString)).Password
try {
    & .\publish\gateway\Scada.Gateway.Host.exe
} finally {
    Remove-Item Env:GatewayAgent__Token -ErrorAction SilentlyContinue
}
```

正常停止使用 Ctrl+C；运行进程会取消并等待驱动真正退出。退出卡在未响应取消的现场 I/O 时，不应同时启动另一个安装目录去采集相同设备。强制结束进程不会把已开始命令当作可重发，也不会清空持久队列；中心的旧会话需等租约到期或在确认旧进程退出后由管理员释放。

## 配置项

以下均在 `GatewayAgent` 节下，环境变量使用 `GatewayAgent__名称`：

| 名称 | 默认值 | 说明 |
|---|---|---|
| `ServerUrl` | 必填 | 中心 HTTPS 基地址，无用户信息、查询参数或片段 |
| `GatewayId` / `Token` | 必填 | 管理员创建时返回的编号及密钥 |
| `DataRoot` | `data/gateway` | 相对发布程序目录解析；部署建议使用独立的绝对目录 |
| `HttpTimeoutMs` | 5000 | 单次请求及响应读取超时，100～60000 毫秒 |
| `HeartbeatIntervalMs` | 5000 | 心跳间隔，500～10000 毫秒 |
| `ConfigurationPollMs` | 5000 | 配置条件检查间隔，500～60000 毫秒 |
| `RetryDelayMs` | 1000 | 链路失败后的重试间隔，100～30000 毫秒 |
| `MaxPendingBytes` | 268435456 | 待传 JSON 字节上限；不是 SQLite 文件或总磁盘空间上限 |
| `CommandTimeoutMs` | 5000 | 采集引擎命令超时，同时受中心执行授权期限限制；中心另用 `Gateway:CommandTimeoutMs`，默认 10000 毫秒 |
| `AllowLoopbackHttp` | false | 只允许显式本地测试，且地址必须为回环地址；中心也须单独开启对应测试选项 |

程序禁用 HTTP 自动重定向并使用系统正常 TLS 校验。主机环境中的 HTTPS 信任配置和代理设置应在部署验收时核对；没有忽略证书校验开关。

## 状态及恢复

数据目录下的 `status.json` 是可覆盖的诊断快照，不参与队列确认。它包含更新时间、状态、配置版本、稳定数据流、设备状态、积压字节/批数及已确认序号，不包含密钥或 OPC 密码。应结合 `updatedTs` 判断进程是否仍在更新，不能仅凭旧文件中的 `Running` 判断存活。

| 状态 | 含义 |
|---|---|
| `Connecting` | 尚未取得可用配置，等待中心 |
| `ApplyingConfiguration` | 正在停止旧驱动、保存或启动新配置；尚未确认新版 |
| `Running` | 中心交换正常；具体设备健康仍看 `devices` |
| `OfflineCollecting` | 中心暂不可达，继续按已确认配置采集 |
| `AcquisitionPaused` | 至少一个采集任务异常暂停，如队列满；查看设备错误及积压 |
| `Blocked` | 配置无效、身份/会话被拒绝或序号冲突，禁止离线恢复旧配置 |

网络恢复后按原数据流和序号补传，只在收到精确确认后删除本地包。队列释放至容量 80% 以下后自动尝试恢复异常任务，不通过删除队列恢复。中心和本地持久接收序号不一致时停采并保留队列，须核查数据库恢复历史，不能直接改序号或清空数据。

重启离线采集仅适用于同一中心、编号和密钥下的已确认配置。已收到撤权或会话冲突后会持久阻止离线启动；密钥轮换后须在线重新取配置并确认。换中心或编号应使用新目录并单独处理旧队列，不能把已有目录重新绑定给其他网关。

## Windows 服务与数据保护

进程已接入 Windows Service 生命周期。正式服务部署应把发布程序放到固定路径，给专用服务账户设置目录权限，并由部署系统提供 `GatewayAgent` 配置；随后注册发布的 `Scada.Gateway.Host.exe`，使用 SCM 的正常停止操作。仓库不自动注册系统服务、写机器级密钥或修改服务账户。本轮没有执行 SCM 安装与重启验收。

数据目录包含 `gateway.db`、运行时 WAL/SHM、`keys/`、`pki/` 及独占锁文件。离线配置已加密；Windows 上 Data Protection 密钥再受当前账户的 DPAPI 保护。目录及启动配置应只允许服务账户和受信管理员访问；日志和诊断快照也属于工程数据。

备份应在正常停机后一起保存数据库、密钥、PKI、启动配置及对应应用版本。仅复制 `gateway.db` 不足以恢复；直接搬到其他账户或主机不能保证解密 Windows DPAPI 保护的密钥。跨账户/主机恢复方案、介质故障、磁盘空间告警及完整安装包仍需随运维阶段完善并实际验收。
