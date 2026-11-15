using NModbus;

namespace Scada.Collector.Common;

/// <summary>把 Modbus 从站异常码写成现场能看懂的一句原因。</summary>
public static class ModbusSlaveReason
{
    public static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SlaveException slave || slave.SlaveExceptionCode == 0) continue;
            return slave.SlaveExceptionCode switch
            {
                SlaveExceptionCodes.IllegalFunction => "从站拒绝：非法功能码",
                SlaveExceptionCodes.IllegalDataAddress => "从站拒绝：非法数据地址",
                SlaveExceptionCodes.IllegalDataValue => "从站拒绝：非法数据值",
                SlaveExceptionCodes.SlaveDeviceFailure => "从站拒绝：从站设备故障",
                SlaveExceptionCodes.Acknowledge => "从站拒绝：已接受，处理尚未完成",
                SlaveExceptionCodes.SlaveDeviceBusy => "从站拒绝：从站设备忙",
                10 => "从站拒绝：网关路径不可用",
                11 => "从站拒绝：网关目标无响应",
                _ => $"从站拒绝：异常码 {slave.SlaveExceptionCode}"
            };
        }
        return exception.Message;
    }
}
