# Scada.Protocols

协议编解码层。**只管字节到语义的翻译**，不建连接、不做重试、不认识数据库和 DI 容器。

```
Protocols/
  ProtocolFormatException.cs   报文不合规（继承 IOException）
  RegisterValueCodec.cs        值层：数据类型 + 字节序 ↔ 寄存器字节块
  Modbus/ModbusFunctions.cs    Modbus 区域 → 读功能码
  S7/S7StringCodec.cs          S7 STRING（2 字节长度头 + UTF-8）
  Iec104/Iec104Codec.cs        IEC 60870-5-104：APCI/ASDU 编解码、帧扫描、CP56Time2a
```

## 两层职责

| 层 | 做什么 | 放哪里 |
| --- | --- | --- |
| 帧层 | 起始字节、长度、序号、校验、拆包/粘包 | 各协议目录（如 `Iec104Codec.Scan`） |
| 值层 | 测点数据类型、字节序、位偏移、字符串编码 | `RegisterValueCodec` |

驱动只负责"什么时候发、发失败怎么办"；把字节拼错、把 float 读歪，都是这一层的问题。
新增协议时先判断有没有可复用的值层——Modbus 的寄存器、S7 的 DB 字节区共用 `RegisterValueCodec`，
不要因为"我们是 XX 协议"复制一份。

## 字节序定义

四种排列都作用在整段字节上，32 位与 64 位语义一致（16 位只有"换字节/不换"两种）：

| 选项 | 32 位（ABCD） | 64 位（ABCDEFGH） |
| --- | --- | --- |
| ABCD | 原序 | 原序 |
| BADC | 每 16 位字内换字节 | 每 16 位字内换字节 |
| CDAB | 交换两个 16 位字 | 交换两个 32 位字 |
| DCBA | 整体反序 | 整体反序 |

> 2026-11 之前 64 位的 `DCBA` 实际是"16 位字反序"，导致真·小端 double 无配置可读；现已与 32 位对齐。
> 若现场需要"16 位字反序"，那是第五种排列，要新增枚举值，不能借用 `DCBA`。

四张排列表自逆，编码与解码共用一张，所以**自编码自解码的用例永远通过**——
每个字节序都必须有 wire 字节的黄金用例（见 `tests/Scada.Protocols.Tests`）。

## 错误约定

- 报文不合规 → `ProtocolFormatException`（继承 `IOException`，链路层原有的断链重连逻辑照旧生效）。
- 写入值超出测点类型范围 / 非有限值 → `ArgumentOutOfRangeException`。**绝不饱和截断**：
  写路径上"写进去的是另一个值"比"写失败"危险得多。
- 采集热路径优先用 `TryDecodeRegister` / `TryEncodeRegister`，避免用异常做分支。

## 测试

`tests/Scada.Protocols.Tests` 覆盖：四种字节序的黄金字节、取值范围与舍入、畸形帧（长度不符、
SQ 截断、未知类型、COT/IOA 越界）、流重同步、CP56Time2a 字段布局。
