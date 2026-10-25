# 协议扩展总览

除 Modbus TCP/RTU、Siemens S7 与 OPC UA（见 `../protocols.md`）外，新协议统一采用“文本地址”接入方式：

- **设备**：`Device.Protocol` 选择协议；协议专属参数以 JSON 对象保存在 `Device.ProtocolOptions`（≤16000 字符，未知字段拒绝）；需要认证的协议复用 `OpcUserName` / 受保护密码字段（加密保存，导出与诊断不含密码，网关配置下发时按需解密）。
- **测点**：区域固定为 `Address`（文本地址），地址文本保存在 `Tag.NodeId`，语法由各协议在 `Scada.Core.Protocols` 中的 `IProtocolSpec` 校验；API、Excel 导入、对象模板、工程包导入均走同一校验。
- **采集**：驱动实现 `IDeviceDriver`，由 `DeviceDriverFactory` 按协议创建；中心与远程网关共用同一驱动与工厂，因此所有新协议都可分配到网关采集。连接失败/断线后由采集引擎按 1s→30s 指数退避重连，诊断页的轮询、点位质量、重连次数和 Prometheus 指标自动覆盖。
- **写入**：只经现有控制闭环（权限、双人复核、限值、审计、写后回读）。驱动 `WriteAsync` 内部完成协议级确认（如 104 选择-执行、MQTT 状态回报）；确认缺失一律报告“结果未知”，不自动重发。
- **工程包/快照**：`ProtocolOptions` 与文本地址随设备、测点反射导出，导入时重新校验；密码不导出。

| 协议 | 文档 | 实现方式 | 状态 |
|------|------|----------|------|
| MQTT（通用 JSON/主题） | `mqtt.md` | MQTTnet | 已实现，进程内代理测试 |
| MQTT Sparkplug B | `sparkplug-b.md` | MQTTnet + 自研 protobuf 编解码 | 已实现，进程内代理测试 |
| 三菱 MC（3E，二进制/ASCII，TCP/UDP） | `mitsubishi-mc.md` | 自研 | 已实现，模拟器测试 |
| 欧姆龙 FINS（UDP/TCP） | `omron-fins.md` | 自研 | 已实现，模拟器测试 |
| IEC 60870-5-104 主站 | `iec104.md` | 自研（未用 GPL 的 lib60870） | 已实现，子站模拟器测试 |

## 第三方库许可清单

| 包 | 版本 | 许可 | 用途 | 引入位置 |
|----|------|------|------|----------|
| MQTTnet | 5.2.0.1603 | MIT | MQTT 3.1.1/5.0 客户端 | Scada.Collector |
| MQTTnet.Server | 5.2.0.1603 | MIT | 进程内 MQTT 代理（仅测试） | Scada.Collector.Tests |

明确未引入：lib60870.NET、libiec61850（GPLv3/商业双许可），以免产品整体受 GPL 约束。
