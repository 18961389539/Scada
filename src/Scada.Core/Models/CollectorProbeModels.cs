namespace Scada.Core.Models;

// 设备测试读取复用采集连接的结果。
// null（而不是 Reused=false）表示该设备未在采集，调用方应回退到独立连接。
public sealed record CollectorProbeResult(bool Reused, RawTagValue? Value = null, string? Error = null);