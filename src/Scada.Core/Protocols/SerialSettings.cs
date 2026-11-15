using Scada.Core.Enums;

namespace Scada.Core.Protocols;

/// <summary>
/// 串口参数校验的唯一实现。此前同一句「串口通信参数无效，Modbus RTU 使用 8 个数据位」
/// 在通道、设备、汇川/信捷/台达三个协议规格里各写了一遍，改一处漏四处。
/// </summary>
public static class SerialSettings
{
    public const int MinBaudRate = 300;
    public const int MaxBaudRate = 921_600;
    public const int RequiredDataBits = 8;
    public const int MaxPortNameLength = 128;

    public static string? ValidatePortName(string? port) =>
        string.IsNullOrWhiteSpace(port) || port.Length > MaxPortNameLength ? "串口名称无效" : null;

    public static string? Validate(string? port, int baudRate, int dataBits, SerialParity parity, SerialStopBits stopBits)
    {
        if (ValidatePortName(port) is string portError) return portError;
        return baudRate is < MinBaudRate or > MaxBaudRate
            || dataBits != RequiredDataBits
            || !Enum.IsDefined(parity)
            || !Enum.IsDefined(stopBits)
            ? "串口通信参数无效，Modbus RTU 使用 8 个数据位"
            : null;
    }

    /// <summary>RTU 采集站号：1～247，不支持广播写入。</summary>
    public static string? ValidateStation(byte station) =>
        station is < 1 or > 247 ? "RTU 采集站号须在 1～247 之间，不支持广播写入" : null;
}
