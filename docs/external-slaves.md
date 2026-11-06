# 对外从站：Modbus TCP / IEC 104 / OPC UA A&C

三个能力解决同一个问题：**本系统不只是"往下采"，还要"往上送"**。
上级调度、集团平台、第三方 PLC 或数采网关需要按标准协议读到本系统的实时值、
收到报警事件，并把控制指令发回来。这三项都默认关闭，配置在 `External` 节。

| 能力 | 端口默认 | 方向 | 典型场景 |
| --- | --- | --- | --- |
| Modbus TCP 从站 | 502 | 上级主站读/写 | 把本系统当协议转换网关；PLC 直接读本系统聚合值 |
| IEC 60870-5-104 从站 | 2404 | 调度主站总召唤/遥控 | 电力、水务、热力上送调度 |
| OPC UA A&C | 4840（复用现有端点） | 上级客户端订阅报警 | 集团 MES / 中央监控订阅本站报警并确认 |

## 共同约束

- **默认关闭**。未显式 `Enabled=true` 时不监听任何端口。
- **许可门控**：`ILicenseEnforcement.OutputsAllowed` 为假时不启动监听，日志给出原因。对外输出与报表同属"输出类"能力。
- **写入不是旁路**。三个从站的写入都走 `ControlCommandService.SubmitAsync`，与站内操作员执行同一套检查：
  就地/远程、检修挂牌、操作限幅、单次变化率、许可互锁、许可授权、同点回读、审计。
  外部主站不能因为"协议是标准的"就绕开这些约束。
- **地址映射随配置版本重建**：改测点或点表后无需重启，下一次请求即按新映射处理。
- **点表问题不阻断启动**：重复地址、越界地址、引用不存在的测点会在日志里逐条告警，其余点照常工作。

## Modbus TCP 从站

```json
{
  "External": {
    "ModbusSlave": {
      "Enabled": true,
      "ListenAddress": "0.0.0.0",
      "Port": 502,
      "UnitIds": [1],
      "IpAllowlist": ["10.20.0.0"],
      "AutoAddressing": true,
      "ByteOrder": "ABCD",
      "AllowWrite": true,
      "MaxReadRegisters": 125,
      "MaxReadBits": 2000
    }
  }
}
```

### 地址语义

`AutoAddressing=true` 时按测点自身的 `Area + Address` 直通：同一份点位表既是采集地址也是对上地址，
等价于把本系统当成一台协议转换网关。计算点没有现场地址，不参与直通。

`AutoAddressing=false` 时只用 `Points` 显式映射：

```json
"Points": [
  { "tagId": 12, "unitId": 1, "area": "HoldingRegister", "address": 100 }
]
```

四张地址表相互独立，与 Modbus 标准一致：Coil（FC01/05/15）、DiscreteInput（FC02）、
HoldingRegister（FC03/06/16/23）、InputRegister（FC04）。

多寄存器类型（Int32/UInt32/Float 占 2 个，Double 占 4 个）会一并占住后续地址，
后续测点不能落在它的中间；写入从中间地址开始会被拒绝（`0x02`），
单寄存器写（FC06）对多寄存器类型返回 `0x03`——半个 Float 是脏数据，宁可拒绝也不猜高低字。

### 功能码

FC01 / 02 / 03 / 04 / 05 / 06 / 15 / 16 / 23。未实现的功能码返回 `0x01`。
未映射地址读返回 0（兼容主站整段读），写返回 `0x02`。

### 质量处理

Modbus 没有质量位。默认保持最后一次有效值；配置 `BadQualityValue` 后，坏质量返回该替代值。
这一取舍必须与上级系统约定清楚——"保持旧值"意味着主站无法从数据本身判断设备已掉线。

### 写数量上限

一次多线圈/多寄存器写最多触发 64 个控制点（内部硬上限）。
控制下发是串行的现场动作，放开到 2000 会让一个报文占用控制链路几十秒，也会把一次误操作放大成批量动作。

## IEC 60870-5-104 从站

104 没有"地址直通"概念（IOA 与现场地址没有对应关系），**必须显式配置点表**。

```json
{
  "External": {
    "Iec104Server": {
      "Enabled": true,
      "Port": 2404,
      "CommonAddresses": [1],
      "IpAllowlist": ["10.30.1.10"],
      "K": 12,
      "W": 8,
      "T1Sec": 15,
      "T2Sec": 10,
      "T3Sec": 20,
      "UseTimeTag": true,
      "AllowControl": true,
      "Points": [
        { "tagId": 12, "ioa": 1001, "kind": "SinglePoint" },
        { "tagId": 15, "ioa": 4001, "kind": "MeasuredFloat" }
      ]
    }
  }
}
```

### 支持的信息类型

| kind | 上送类型 | 何时用 |
| --- | --- | --- |
| `SinglePoint` | M_SP_NA_1 / M_SP_TB_1（带时标） | 开关量、运行状态 |
| `MeasuredFloat` | M_ME_NC_1 / M_ME_TF_1（带时标） | 模拟量（短浮点，IEEE 754 小端） |
| `Auto` | 按测点类型选择 | Bool → 单点，其余 → 短浮点 |

归一化（M_ME_NA）、标度化（M_ME_NB）、累计量（M_IT_NA）未实现。
它们需要测点量程与累计量语义，半成品比特型错误比"不支持"更难排查。

### 时序与状态机

- STARTDT / STOPDT / TESTFR 完整处理。
- k 窗口未确认满时停止发送新 I 帧；收到 w 个 I 帧后回 S 帧。
- t2 无数据时补 S 帧；t3 空闲发 TESTFR；t1 有未确认帧且超时才断开——空闲链路不会被 t1 误判。
- 总召唤：ACT_CON（COT=7）→ 按类型分组的批量数据（COT=20）→ ACT_TERM（COT=10）。
  单点与遥测分成不同 ASDU，因为一个 ASDU 只能有一个类型标识。
- 变化上送（COT=3）：首轮只建基线不发报文，主站连上后应主动总召唤取全量。
- 遥控：单点（C_SC_NA_1）与双点（C_DC_NA_1）。选择阶段只回确认，执行阶段才真正下发；
  双点的"中间态/不确定"不是有效命令，直接拒绝而不是猜一个。
- 对时（C_CS_NA_1）：**只回确认，不修改系统时钟**。服务器时间牵动历史库分区、报表班次与
  调度事件顺序，必须由运维显式决定，不能由一条网络报文触发。

### 品质映射

| 站内质量 | SIQ/QDS |
| --- | --- |
| Good | 0x00 |
| Uncertain | 0x40（NT，非当前值） |
| CommFailure / Bad | 0x80（IV，无效） |

## OPC UA Alarm & Condition

复用现有对外 OPC UA 端点（`External:OpcUa`），把报警规则挂成 `AlarmConditionType` 实例：

```
Objects/Scada/Alarms/<规则 Key>
```

```json
{
  "External": {
    "OpcUa": {
      "Enabled": true,
      "ExposeAlarmConditions": true,
      "AlarmPublishIntervalMs": 500
    }
  }
}
```

- 状态由站内 `AlarmEngine` 决定，这里只做投影，不重复实现判定。
- `Active` / `Acked` / `Confirmed` / `Suppressed` / `Shelved` / `Enabled` 与站内四态模型对齐。
- `ConditionName` 用规则 Key，`SourceNode` / `InputNode` 指向测点变量。
- 客户端调用 `Acknowledge` 方法时，写入 `AlarmService.AckAsync`，落库后才返回 `Good`；
  若该规则当前没有未确认的活动报警，返回 `BadNothingToDo`。
- 状态变化才上报事件，不按固定周期刷事件。

限制：只暴露 `AlarmConditionType` 基类语义，没有 `LimitAlarmType` 的高低限状态机。
需要按限值分支处理（例如区分 HighHigh 与 High）的客户端，应读测点值或规则属性自行判断。

## 排查清单

端口提示：Modbus 502 与 IEC 104 2404 都属于特权端口（< 1024），
Windows 上需要管理员运行或 `netsh` 端口映射，Linux 上需要 `setcap` / `CAP_NET_BIND_SERVICE`。
现场若不方便提权，直接用 5020 / 24040 之类的高位端口，上级主站一侧配置对应端口即可。

| 现象 | 检查 |
| --- | --- |
| 端口没监听 | `Enabled`、许可是否允许输出、`/api/external/status` 里 `running` |
| 主站读到全 0 | `AutoAddressing` 与 `Points` 是否覆盖；测点是否为计算点（直通模式跳过） |
| 读到的浮点值乱序 | `ByteOrder`（ABCD/BADC/CDAB/DCBA），从站侧统一编码，与采集侧无关 |
| 写返回 `0x04` | 控制闭环拒绝了：设备停用、就地、挂牌、限幅、变化率、许可条件 |
| 104 一直不响应总召唤 | 公共地址不匹配、未先发 STARTDT、点表为空 |
| 104 连接被断开 | t1 超时：主站未回 S 帧或 TESTFR_CON |
| OPC UA 看不到报警节点 | `ExposeAlarmConditions`、规则是否 `Enabled`、规则类型是否为 `BadQuality`（不暴露） |
