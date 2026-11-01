using Scada.Core.Enums;

namespace Scada.Core.Models;

// 简化版 ISA-18.2 优先级矩阵：先看后果，再看允许响应时间。
// 现场若要调整分档，只需改这一处，规则保存、模板实例化和工程包导入都会走同一套计算。
public static class AlarmPriority
{
    public static AlarmLevel Compute(AlarmConsequence consequence, int responseTimeSec) => consequence switch
    {
        // 安全后果无论响应时间多长都按严重处理。
        AlarmConsequence.Safety => AlarmLevel.Critical,
        // 严重后果：5 分钟内必须响应 → 严重；否则警告。
        AlarmConsequence.Severe => responseTimeSec is > 0 and <= 300 ? AlarmLevel.Critical : AlarmLevel.Warning,
        // 较大后果：10 分钟内必须响应 → 警告；否则提示。
        AlarmConsequence.Major => responseTimeSec is > 0 and <= 600 ? AlarmLevel.Warning : AlarmLevel.Info,
        _ => AlarmLevel.Info
    };
}
