# 对外 TCP 服务

可配置监听地址/端口、可选 TLS、最大连接数、IP 白名单。默认关闭（`External:Tcp:Enabled=false`）。

## 帧格式

每个消息：`4 字节大端无符号长度` + `UTF-8 JSON`（长度仅为 JSON 字节数）。不支持未分帧的粘包裸 JSON，避免半包解析风险。

最大帧：`MaxFrameBytes`（默认 256 KiB）。空闲超时：`IdleTimeoutSec`（默认 120）。

## 消息（`type` 字段）

客户端 → 服务端：

| type | 字段 | 说明 |
|---|---|---|
| `auth` | `apiKey`, `id?` | 使用与 REST 相同的 API 密钥 |
| `ping` | `id?` | 心跳 |
| `subscribe` | `tagIds` | 订阅（按密钥区域过滤） |
| `unsubscribe` | `tagIds` | 取消订阅 |
| `poll` | `tagIds?` | 快照；省略则用当前订阅集。需 `ReadTags` |
| `write` | `tagId`, `value`, `confirmed?`, `requestId?` | 走现场控制闭环。需 `Write` |

服务端 → 客户端：

| type | 说明 |
|---|---|
| `hello` | 连接后立即发送，含 `protocol=1` |
| `authOk` / `error` | 认证结果；`error.code` 见下 |
| `pong` | 心跳应答 |
| `subscribed` / `unsubscribed` | 当前订阅列表 |
| `snapshot` | poll 结果 |
| `update` | 订阅测点变化推送（约 500ms） |
| `writeResult` | 控制结果（同 REST） |

错误码：`unauthorized` / `forbidden` / `bad_request` / `rate_limited` / `not_found` / `internal` / `frame_too_large`。

## 配置示例

```json
{
  "External": {
    "Tcp": {
      "Enabled": true,
      "ListenAddress": "0.0.0.0",
      "Port": 7500,
      "Tls": false,
      "TlsCertPath": null,
      "MaxConnections": 64,
      "IpAllowlist": ["127.0.0.1"],
      "IdleTimeoutSec": 120,
      "MaxFrameBytes": 262144
    }
  }
}
```

## 限制

- 推送为服务端轮询缓存差分，非点级事件总线
- TLS 需自备 PFX；未启用证书用户双向认证
- 不支持压缩或多路复用
