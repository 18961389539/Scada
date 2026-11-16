# 本机采集与可选的 Gateway.Host 回环隔离

默认部署仍是一体化：`Scada.Server` 在同一进程内运行 `CollectorEngine`，采集未分配网关的设备。这是受支持的默认值，不必拆进程。

## 可选模式：关闭本机采集，全部走网关

把 `Scada:LocalCollectionEnabled` 设为 `false` 后：

- Server **不再**对未分配网关的设备建链、轮询、重连、试连或本机 OPC 浏览/导入。
- 已启用且 `GatewayId` 为空的设备，在 REST/MCP 保存时被拒绝。
- 对这些设备的立即读取、控制写入，以及 `POST /api/devices/{id}/test` 与 `POST /api/devices/test-draft`，会返回明确错误（试连与重连均为 HTTP 409），不会在中心进程内下发或建链。已分配网关的网页工程试连保持原状：已保存设备的 `/{id}/test` 走网关诊断；未保存的 `/test-draft` 仍提示先保存后再测。
- `/api/health` 与系统监视会出现 `local_collection_disabled`；若仍有未分配网关的启用设备，再出现 `local_collection_unassigned`。
- 已分配网关的设备仍由对应的 `Gateway.Host` 采集、浏览与执行命令。备机本来就不采集、不接网关；网关应连到主机（可配置 `FailoverServerUrls`）。

更改该开关后需要**重启中心**。

这不是驱动插件进程隔离：插件仍在 `Gateway.Host` 进程内加载。Server 工程对 Collector 的项目引用仍然保留，默认一体化部署不变。

## 同机回环部署

1. 在中心登记网关（`POST /api/gateways`，需要全局配置权限），记下编号和密钥。
2. 把要采集的设备分配到该 `GatewayId`。
3. 中心配置（示例见 `docs/examples/appsettings.LocalCollection.GatewayLoopback.json`，需自行合并，不会自动加载）：

```json
{
  "Scada": { "LocalCollectionEnabled": false },
  "Gateway": { "AllowLoopbackHttp": true }
}
```

HTTP 回环接入必须同时打开 `Gateway:AllowLoopbackHttp`。默认关闭，避免把明文 HTTP 网关认证暴露到非回环地址。

4. 在同一台机器启动 `Scada.Gateway.Host`，指向中心回环地址（示例见 `docs/examples/gateway-agent.Loopback.json`）：

```json
{
  "GatewayAgent": {
    "ServerUrl": "http://127.0.0.1:5080/",
    "AllowLoopbackHttp": true,
    "GatewayId": 1,
    "Token": "替换为登记时得到的密钥",
    "DataRoot": "data/gateway-local"
  }
}
```

网关侧也必须设置 `AllowLoopbackHttp`，否则拒绝 `http://127.0.0.1`。密钥不要写入版本库。

启动示例（按实际路径与配置文件调整，不要对现场设备做一次“试连”来验证）：

```powershell
.\Scada.Gateway.Host.exe --GatewayConfig=C:\path\gateway-agent.Loopback.json
```

可用 `--ValidateConfig=true` 只校验配置、不启动采集。

## 已知限制（受支持的受限模式）

- 所有**启用**的设备都必须分配网关，否则不会被采集，保存也会被拒绝。
- 网关路径对部分协议的立即读取、以及不带命令号的字符串写入，沿用既有限制（见控制与追溯相关实现）。
- MCP `reconnect_device` **不会**向网关下发工程重连命令；已分配网关的设备请用网页诊断重连，或重启对应 `Gateway.Host`。
- 对本机未分配网关的设备发起重连时，REST 返回 409、MCP 返回 `success=false`，原因都是「本机采集已关闭」（`LocalCollection.DisabledMessage`），不再说成「配置已变化」。网页对已分配网关设备的工程重连不受影响。
- 本模式不提供插件崩溃隔离以外的“驱动沙箱”；只是把采集从中心进程挪到网关进程。
- 不要为了“看起来隔离”而删除 Server 对 Collector 的项目引用。
- 验证时不要启动真实控制链路或连接现场设备。

一体化默认（`LocalCollectionEnabled=true`）无需改网关或回环开关。
