# MQTT（通用 JSON / 主题映射）

驱动：`Scada.Collector.Mqtt.MqttDriver`（MQTTnet 5，MIT）。中心与网关均可运行。

## 设备参数（ProtocolOptions）

| 字段 | 默认 | 说明 |
|------|------|------|
| `clientId` | 空 | 空则每次连接生成唯一 ID |
| `version` | `V500` | `V500` / `V311` |
| `useTls` | false | TLS（系统信任链校验服务器证书） |
| `allowUntrustedCertificate` | false | 跳过证书校验，仅调试；须同时启用 TLS |
| `allowPlaintextPassword` | false | 未启用 TLS 时默认拒绝保存密码 |
| `qos` | 1 | 订阅与写入发布的 QoS（0/1/2） |
| `keepAliveSec` | 30 | 5～3600 |
| `cleanSession` | true | |
| `retainWrites` | false | 写入消息是否带保留标志 |
| `confirmWrites` | true | 写入后等待状态主题回报一致值 |
| `staleAfterMs` | 0 | 超过该时长无新消息则质量 `Uncertain`；0 不判定 |

主机/端口为代理地址（默认 1883，TLS 常用 8883）；用户名/密码为代理认证。

## 测点地址

```
状态主题[|JSON路径][;w=写入主题][;wpath=写入JSON路径][;ts=时间戳JSON路径]
```

- 例：`plant/boiler|$.temp;ts=$.ts`、`plant/pump/sp;w=plant/pump/sp/set`、`dev/1/state|$.run;w=dev/1/cmd;wpath=$.run`
- 主题不允许通配符 `+`/`#`，不能以 `$` 开头。JSON 路径支持 `$.a.b[0]`。
- 无 JSON 路径时载荷可以是纯数值、`true/false/on/off` 或 JSON 标量。
- 时间戳：数值（≥1e11 视为毫秒，否则秒）或 ISO 8601 字符串；未配置时使用接收时刻作为源时间戳。
- 写入载荷：有 `wpath` 时生成 JSON 对象（如 `{"value":12.5}`），否则为 JSON 标量文本；Bool 写 `true/false`，整数类型写整数。
- 可写测点必须配置 `w=`。

## 质量与时间戳

| 情况 | 质量 |
|------|------|
| 尚未收到该主题消息 | Bad（“尚未收到该主题的消息”） |
| JSON 路径不存在、不是数值 | Bad |
| 超过 `staleAfterMs` 无新消息 | Uncertain |
| 连接断开 | 整设备 CommFailure，引擎退避重连 |

订阅在首次读取时按测点主题建立；新订阅后最多等待 1 秒接收保留消息。

## 写入语义

1. 控制闭环通过权限/限值/复核后调用驱动；驱动按设备 QoS 发布到写入主题（QoS1/2 等代理确认）。
2. `confirmWrites=true`：在设备超时内等待状态主题收到**发布之后**的新消息且值与写入值一致，否则抛出“结果未知”，不重发。
3. 控制闭环随后回读缓存值作为反馈。

注意：若写入主题与状态主题相同，代理会把本客户端的发布回送给自己，确认只代表“代理已收到”，不代表设备已执行；建议设备使用独立的状态回报主题。

## 不支持 / 限制

- 通配符订阅、按消息内容动态生成测点、字符串/数组测点（产品数值模型限制）。
- 客户端证书（双向 TLS）与自定义 CA 文件尚未提供配置入口，当前使用操作系统信任链。
- 未在真实代理（Mosquitto/EMQX/HiveMQ）上做互操作与长稳测试；测试使用进程内 MQTTnet 代理。
