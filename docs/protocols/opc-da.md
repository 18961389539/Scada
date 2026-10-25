# OPC DA 客户端（仅 Windows，未经真实环境验证）

协议值 `OpcDa`。OPC DA 基于 COM/DCOM，只能在 Windows 上运行。

> **重要**：本驱动只在 Linux 上编译并用假客户端测试了映射逻辑，**从未在真实 Windows + OPC DA 服务器上验证**。生产环境推荐使用 **DA→UA 包装器**（如 OPC Foundation UA COM Wrapper、KEPServerEX/Matrikon 自带的 UA 服务端、或其他网关产品），然后以本系统成熟的 **OPC UA** 协议接入。

## 运行条件

- 采集端（中心服务或网关）运行在 Windows 上；Linux/macOS 上连接时报告：“OPC DA 依赖 Windows COM/DCOM，当前操作系统不支持……建议使用 DA→UA 包装器”。
- 安装并注册 OPC Foundation **OPC Core Components**（含自动化包装器 `OPCDAAuto.dll`，ProgID `OPC.Automation.1`）。驱动通过后期绑定 COM 调用该包装器，不引入任何 NuGet 包，也不引入 OPC Foundation 的 .NET API（其许可需另行评估）。
- 远程节点需正确配置 DCOM 权限与防火墙；服务账户需有访问权限。

## 设备参数（ProtocolOptions）

| 字段 | 默认 | 说明 |
|------|------|------|
| progId | 必填 | 服务器 ProgID，如 `Kepware.KEPServerEX.V6` |
| node | 空 | DCOM 远程主机，空 = 本机 |
| readFromDevice | false | true = SyncRead(OPC_DS_DEVICE)，false = 读激活组缓存 |
| updateRateMs | 1000 | 组更新周期 50～3600000 |
| timestampsUtc | false | 自动化接口返回的时间按 UTC 解释，否则按本地时区 |

设备的主机/端口字段对 OPC DA 不使用。

## 测点地址

服务器 ItemID 原文（如 `Channel1.Device1.Tag1`），首尾不能有空格，≤512 字符。首次读取时注册到组，被拒绝的 ItemID 测点标 Bad（附 HRESULT），不再重复添加。

## 品质与时间戳

| OPC 品质（QQ 位） | 本系统 |
|-------------------|--------|
| Good（11） | Good；子状态 Local Override → Uncertain |
| Uncertain（01） | Uncertain，错误文本给出子状态（最后可用值、传感器不准确、超出工程单位…） |
| Bad（00） | Bad，错误文本给出子状态（配置错误、未连接、设备故障、通信故障、停用…） |

服务器时间戳作为源时间戳（1980 年之前视为无效）。字符串、数组等非数值 VARIANT 标 Bad。

## 写入

经控制闭环。按测点数据类型转换为规范 VARIANT（Bool/Int16/UInt16/Int32/UInt32/Float/Double），越界或整数写小数直接拒绝。服务器返回错误码 → “服务器拒绝写入，命令未执行”；COM/RPC 异常 → “结果未知”并断开重连，不重发。

## 重连

读或写出现 COM 异常即断开，由采集引擎 1s→30s 退避重连。连接受 max(设备超时, 5 s) 约束。

## 限制

- 仅 SyncRead/SyncWrite，无订阅回调（DataChange）、无浏览、无 HDA/A&E。
- COM 调用在线程池线程上进行，依赖 COM 自动列集到包装器的套间；超时后的阻塞调用无法取消。
- 未在真实环境测试。
