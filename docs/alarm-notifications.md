# 报警外发（Webhook）

> 本文的全局 `AlarmWebhookUrl` 仍可用，视为“全部区域”。按区域/等级/班次分发、邮件/短信、未确认升级请用“报警通知”通道与路由，见 `alarm-management.md`。暂挂、抑制、抖动中的报警不外发。

站内推送只对打开着的页面有效；没人盯屏时，报警就等于没发生。配置一个 webhook 后，报警会同时推送到外部系统（企业微信/钉钉机器人、自建通知网关、短信平台的中转接口）。

## 配置

```json
{
  "Scada": {
    "AlarmWebhookUrl": "http://notify.local/hooks/scada",
    "AlarmWebhookMinLevel": "Warning",
    "AlarmWebhookCooldownSec": 60,
    "AlarmWebhookTimeoutMs": 5000,
    "AlarmWebhookQueueSize": 500
  }
}
```

| 配置 | 默认 | 说明 |
|---|---|---|
| `AlarmWebhookUrl` | 空 | 留空即关闭外发，只做站内推送；仅接受 http/https |
| `AlarmWebhookMinLevel` | `Warning` | 低于此等级的报警不外发（`Info` < `Warning` < `Critical`） |
| `AlarmWebhookCooldownSec` | 60 | 同一测点同一等级在冷却期内只发一次；设 0 表示不去重 |
| `AlarmWebhookTimeoutMs` | 5000 | 单次投递超时 |
| `AlarmWebhookQueueSize` | 500 | 待发队列长度，满了就丢并计数，不阻塞采集 |

## 请求

```
POST <AlarmWebhookUrl>
Content-Type: application/json; charset=utf-8
Content-Length: <显式长度>
```

```json
{
  "type": "alarm",
  "action": "raise",
  "sentAt": "2026-10-24T12:22:52.1549079+00:00",
  "alarm": {
    "id": 1, "tagId": null, "deviceId": 1, "gatewayId": null,
    "level": 1, "value": 0, "startTs": 1792844572126, "endTs": null,
    "ackUser": null, "ackTs": null, "message": "设备离线：由于目标计算机积极拒绝，无法连接。"
  }
}
```

- `action` 取 `raise`（触发）、`recover`（恢复）；`change`（数值变化）不外发。
- 正文带显式 `Content-Length`，不依赖 chunked：现场接收端（PLC 网关、老式 HTTP 服务）未必支持分块传输。
- 中文不做 `\uXXXX` 转义，接收端直接落到聊天窗口或日志里可读。
- 接收方返回 2xx 视为成功，其余状态码按失败处理。

## 去重与恢复

- 键是「测点 + 等级」，设备离线报警没有测点时按 `-` 归并。
- 冷却期内的重复触发被抑制并计入 `suppressed`，避免现场抖动刷屏。
- `recover` 不受冷却限制，一定会发出，并清除该键的冷却，使下一次再触发能立刻上报。

## 失败处理

- 采集侧只入队，不等待网络；队列满则丢弃并累计 `dropped`。
- 投递失败不抛出、不影响报警本身，累计 `failed` 与连续失败次数。
- 连续失败达到 3 次后，`/health` 的 `warnings[]` 里出现一条 `alarm_webhook_failing`，健康状态转 `degraded`；恢复成功后自动消失。
- 失败日志按分钟限流，避免长时间故障刷爆日志。

## 观测

`/health` 增加：

```json
"alarmWebhook": { "enabled": true, "sent": 1, "suppressed": 0, "failed": 0, "dropped": 0, "lastError": null }
```

排障顺序：`enabled` 是否为 true → `dropped` 是否增长（接收端太慢）→ `failed` 与 `lastError`（地址、鉴权、网络）→ `suppressed`（这是去重生效，不是故障）。

## 已知限制

- 只有单次尝试，没有重试队列：失败即丢，靠 `failed` 计数暴露问题。需要可靠投递时应在接收端或中间网关做缓冲。
- 没有签名或鉴权头，接收端若需要 token，请在 URL 里带或在前面加一层网关。
- 队列在内存中，进程退出会丢掉未发出的报警。
