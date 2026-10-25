# 对外 OPC UA 服务端

把本系统测点以标准 OPC UA 地址空间对外发布，供 MES / 第三方客户端订阅与写入。默认关闭（`External:OpcUa:Enabled=false`）。

许可：使用 OPC Foundation .NET Standard Stack 2.0.1（RCPL，允许商用闭源分发）。

## 地址空间

```
Objects
└── Scada
    └── Area{areaId}
        └── {DeviceName}
            └── {TagName}_{tagId}   (Double，可读；Writable 测点可写)
```

- 读：实时缓存值与质量（Good / Uncertain / Bad）。
- 订阅：按 `PublishingIntervalMs`（默认 500ms）把缓存刷新到节点，驱动标准 MonitoredItem。
- 写：走现场控制闭环（确认、双人复核、限幅、挂牌、就地等），与 REST/TCP/画面同一路径。
- 配置变更（测点增删改）会自动重建地址空间。

## 认证

| 方式 | 配置 | 说明 |
|---|---|---|
| 匿名 | `AllowAnonymous=true` | 默认可读；**默认不允许写** |
| 用户名/密码 | `AllowUsernamePassword=true`（默认） | ① SCADA 用户账号；② API 密钥：用户名任意，密码为 `scada_…` 明文 |
| 证书用户 | `AllowCertificateUser=true` | 框架已留，需客户端证书与信任列表 |

API 密钥的区域与 Write scope 在写入时生效；无 Control 权限返回 `BadUserAccessDenied`。

## 安全与证书

- 监听：`External:OpcUa:EndpointUrl`（默认 `opc.tcp://0.0.0.0:4840/Scada`）。
- `SecurityModes`：`None` / `Sign` / `SignAndEncrypt`（逗号分隔）。`Sign` 与 `SignAndEncrypt` 均打开 SignAndEncrypt 策略集。
- 服务端 PKI 目录：默认 `{ConfigDb 目录}/keys/opcua-server`（可用 `ServerCertPath` 覆盖）。首次启动自动生成自签名证书。
- 主备部署请同步该目录，否则备机证书指纹会变。

## 配置示例

```json
{
  "External": {
    "OpcUa": {
      "Enabled": true,
      "EndpointUrl": "opc.tcp://0.0.0.0:4840/Scada",
      "SecurityModes": "None,Sign,SignAndEncrypt",
      "AllowAnonymous": false,
      "AllowUsernamePassword": true,
      "AllowCertificateUser": false,
      "MaxSessionCount": 50,
      "PublishingIntervalMs": 500
    }
  }
}
```

管理页「对外接口」可查看启用状态与实际端点；密钥管理与 REST/TCP 共用。

## 不做的事项

- 不暴露报警/历史 HA 节点（历史请用 REST `/api/v1/history`）。
- 不把复杂类型、字符串测点映射为 OPC UA 类型（当前一律 Double）。
- 不替代采集侧 OPC UA **客户端**驱动。
