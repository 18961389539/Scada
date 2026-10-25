# 三菱 MC 协议（3E 帧 / SLMP）

驱动：`Scada.Collector.Mitsubishi.MitsubishiMcDriver`，自研实现（无第三方依赖）。适用于 Q/L/iQ-R/iQ-F（FX5）等以太网口或以太网模块开放的 MC/SLMP 端口。

## 设备参数

| 字段 | 默认 | 说明 |
|------|------|------|
| `format` | `Binary` | `Binary` / `Ascii`，须与 PLC 端口设置一致 |
| `transport` | `Tcp` | `Tcp` / `Udp` |
| `networkNo` / `pcNo` | 0 / 255 | 本站访问为 0 / 0xFF |
| `moduleIo` / `moduleStation` | 0x03FF / 0 | 请求目标模块 IO 号与站号（多 CPU、经网络中继时修改） |
| `monitoringTimer` | 8 | PLC 侧等待时间，单位 250ms |
| `highWordFirst` | false | 32/64 位值字顺序，三菱默认低字在前 |
| `maxGap` | 16 | 批量读合并允许的空隙（字或位） |

## 地址语法

`软元件 + 编号[.位]`，不区分大小写：

| 类别 | 软元件 | 编号 |
|------|--------|------|
| 位 | X Y B SB DX DY | 十六进制 |
| 位 | M L F V SM TS TC SS SC CS CC | 十进制 |
| 字 | W SW | 十六进制 |
| 字 | D R ZR SD TN SN CN Z | 十进制 |

- 位软元件只能用 Bool；字软元件可用 Int16/UInt16（1 字）、Int32/UInt32/Float（2 字）、Double（4 字）。
- 字的位：`D100.3`（位号 0～F）只读。写入需要读-改-写整字，可能覆盖 PLC 同时修改的其他位，故禁止。
- 字符串类型不支持（产品测点为数值模型）。

## 读写

- 读：按软元件分组，按编号合并为批量读（字单位最多 480 字、位单位最多 1792 点，空隙 ≤ `maxGap`）。命令 0401，子命令 0000（字）/0001（位）。
- 写：命令 1401；位软元件按位单位写 1 点，字软元件按字单位写整值。
- PLC 返回非零结束代码时只把该区段测点标为 Bad（错误文本含代码），连接保持；超时或通信错误关闭连接，由采集引擎退避重连。写入超时报告“结果未知”，不重发。
- UDP 下每次请求前丢弃迟到的旧应答；3E 帧无序列号，一次只有一个在途请求。

## 不支持 / 限制

- 4E 帧（带序列号）、1E/2E 帧、随机读写（0403/1402）、监视登记、远程 RUN/STOP、标签（label）访问。
- 双字软元件（LTN/LCN/LZ 等 iQ-R 专有）。
- 测试使用自研模拟器（`tests/Scada.Collector.Tests/McSimulator.cs`）；**未连接真实三菱 PLC 验证**，ZR 等编号进制以 Q 系列手册为准，iQ-R 上请现场核对。
