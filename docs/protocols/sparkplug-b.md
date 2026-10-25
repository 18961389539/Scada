# MQTT Sparkplug B

驱动：`Scada.Collector.Mqtt.SparkplugDriver`。MQTT 传输用 MQTTnet（MIT）；Sparkplug B protobuf 载荷由 `SparkplugPayload` 自行编解码（仅 Payload/Metric 标量字段，无第三方 protobuf 依赖）。

本驱动作为**主机应用（Host Application）**消费一个边缘节点（可选其下一个设备）的数据。

## 设备参数

| 字段 | 说明 |
|------|------|
| `groupId` / `edgeNodeId` | 必填，不含 `/ + #` 与空白 |
| `deviceId` | 空：测点为节点级度量（NBIRTH/NDATA/NCMD）；非空：设备级（DBIRTH/DDATA/DCMD） |
| `primaryHostId` | 非空时以保留 QoS1 发布 `spBv1.0/STATE/{id}` = `{"online":true,"timestamp":…}`，遗嘱与正常断开发布 `online:false`。**同一代理上只应在一台设备上填写**，否则多个连接相互覆盖 STATE |
| `requestRebirth` | 默认 true：未收到出生证书、序号缺口或死亡后收到数据时发送 `NCMD Node Control/Rebirth` |
| `confirmWrites` | 默认 true：写入后等待 DATA 回报一致值 |
| 其余 | `clientId`、`version`、`useTls`、`allowUntrustedCertificate`、`allowPlaintextPassword`、`keepAliveSec`，同 MQTT |

## 测点地址

度量名（metric name），如 `Inputs/Temperature`，必须出现在对应 BIRTH 中。

## 协议行为

- 订阅 `spBv1.0/{g}/NBIRTH|NDEATH|NDATA/{n}`，设备级时再订阅 `DBIRTH|DDEATH|DDATA/{n}/{d}`。
- BIRTH 建立 名称 ↔ 别名 ↔ 数据类型 映射；DATA 可只带别名；BIRTH 未声明的度量被忽略。
- `seq`：NBIRTH 起 0～255 循环，节点所有消息（除 NDEATH）共享；出现缺口时计数（`SeqGaps`）、值降为 Uncertain 并请求 Rebirth，重新出生后恢复。
- `bdSeq`：NDEATH 的 bdSeq 与当前 NBIRTH 不一致时视为旧会话遗嘱，忽略。
- NDEATH / DDEATH：测点 CommFailure（错误文本含原因），写入拒绝；死亡后不主动刷 Rebirth，等待节点自行出生。
- 设备级时，NBIRTH 会使设备进入“等待 DBIRTH”状态。
- 时间戳：度量时间戳优先，其次载荷时间戳。`is_null` → Bad。`is_historical` 的 DATA 不更新实时值。

## 写入

按 BIRTH 中的别名与数据类型发送 DCMD（设备级）或 NCMD（节点级），QoS0（Sparkplug 规范要求）。String/Text/DateTime 等类型拒绝写入。`confirmWrites` 时须在超时内收到该度量更新且值一致，否则“结果未知”。

## 不支持 / 限制

- DataSet、Template、PropertySet（含度量 Quality 属性）、数组类型不解析。
- 一个设备对应一个边缘节点/设备；不做整组自动发现与自动建点。
- 只做 Host Application 消费端，不作为 Edge Node 发布数据。
- 未与 Ignition / Cirrus Link / Eclipse Tahu 实物互操作；测试为进程内代理 + 模拟边缘节点。
