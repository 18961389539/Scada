# BACnet/IP 客户端

协议值 `BacnetIp`，默认端口 47808（UDP）。基于 NuGet 包 **BACnet 4.0.0**（ela-compil，MIT 许可，已核对 nuspec `license=MIT`）。

## 支持范围

| 功能 | 说明 |
|------|------|
| Who-Is / I-Am | 连接时向设备 IP 单播 `Who-Is(实例,实例)`，收到匹配实例号的 I-Am 即采用其源地址与最大 APDU；未收到时（部分设备只广播 I-Am）回退为 ReadProperty 读取 `Device,<实例>` 的 Object_Identifier，失败则连接失败（实例号不存在时提示“没有该设备对象”）。 |
| ReadPropertyMultiple | 每对象读取 `Present_Value` + `Status_Flags`，同一对象的多个测点合并，按“单次读取对象数”分包。设备 Reject/Abort（不支持 RPM）后自动永久回退为逐个 ReadProperty；整包 Error 时本周期逐个读取以定位到具体对象。 |
| WriteProperty | 写 `Present_Value`，带优先级（设备默认 8，测点 `;pri=` 覆盖，6 保留不可用）。BO/BV 写 ENUMERATED 0/1，MSV 写 UNSIGNED（≥1 整数），AO/AV 写 REAL。 |
| COV | 选项 `useCov`：对读计划中的对象发送 SubscribeCOV（非确认通知，时长 `covLifetimeSec`，过半续订）。收到通知的对象使用推送值，不再轮询；设备对某对象返回错误（不支持 COV）则该对象继续轮询；续订超时则清除缓存回到轮询、10 秒后重试。 |
| 对象类型 | AI、AO、AV、BI、BO、BV、MSV |

## 测点地址

```
对象类型:实例号[;pri=1～16]
AI:1        AV:10;pri=12        BO:3        MSV:2
```

- BI/BO/BV 必须为 Bool；AI/AO/AV/MSV 不能为 Bool；MSV 须为整数类型。
- 只有 AO/AV/BO/BV/MSV 可设为可写；`pri` 只能用于可写测点。

## 品质与时间戳

| 来源 | 品质 |
|------|------|
| Status_Flags.FAULT | Bad（“对象故障”） |
| Status_Flags.OUT_OF_SERVICE | Uncertain |
| Status_Flags.OVERRIDDEN | Uncertain |
| Status_Flags.IN_ALARM | 不影响品质（告警由本系统规则处理） |
| 对象/属性错误、Present_Value 为 NULL 或非数值 | Bad，错误文本含 BACnet 错误类/码 |

BACnet Present_Value 不带时间戳：轮询值使用服务器采集时间，COV 值使用通知到达时间。

## 写入与控制闭环

写入只经控制闭环（权限、复核、限值、审计、写后回读）。驱动语义：

- 设备 Error/Reject/Abort 应答 → “设备拒绝写入，命令未执行”。
- 未收到确认 → “写请求已发送但未收到确认，结果未知”，写命令不重发（读请求按 `readRetries` 重发）。
- 写入时清除该对象的 COV 缓存，回读一定走实际读取。
- 不支持写 NULL 释放优先级（Relinquish）；需要时请在设备工具中操作。

## 重连与诊断

连接失败或一个周期内所有读请求都超时时断开，由采集引擎 1s→30s 退避重连。全部测点走 COV 时，设备掉线最迟在订阅时长过半的续订时发现。驱动公开 `CovNotifications`、`ReadMultipleActive`、`PeerMaxApdu` 供测试/诊断。

## 设备参数（ProtocolOptions）

| 字段 | 默认 | 说明 |
|------|------|------|
| deviceInstance | 必填 | 0～4194302 |
| whoIs | true | 连接时单播 Who-Is |
| readMultiple | true | 使用 RPM |
| maxObjectsPerRequest | 16 | 1～64 |
| writePriority | 8 | 1～16，6 保留 |
| readRetries | 1 | 0～3 |
| useCov | false | COV 订阅 |
| covLifetimeSec | 300 | 60～28800 |
| localPort | 0 | 本机 UDP 端口，0 = 自动 |

## 限制

- 仅 BACnet/IP 单播直连；不支持经路由器访问的 MS/TP 设备（DNET/DADR）、BBMD 外部设备注册、广播发现列表。
- 仅 Present_Value/Status_Flags；不支持其他属性、优先级数组读取、Reliability、日程/趋势/告警服务、字符串值。
- 不支持分段（segmentation）超出单个 APDU 的大响应：请减小“单次读取对象数”。
- 测试使用同一库实现的进程内服务器回环，未在真实控制器上验证。
