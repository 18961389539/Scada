namespace Scada.Core.Scripting;

/// <summary>
/// C# 脚本宿主与 Worker 共用的限额（注意与 <see cref="Scada.Core.Calculations.ScriptLimits"/> 区分：
/// 后者是数字表达式 DSL 的编译限额）。这些值以前在两侧各写一份，已经出现"宿主少校验一项"的漂移；
/// 统一放这里，任何一侧改限额都必须同时影响另一侧与文档。
/// </summary>
public static class CSharpScriptLimits
{
    public const int MaxSourceChars = 16_384;
    public const int MaxLibraryChars = 16_384;
    public const int MaxInputs = 64;
    public const int MaxWritableTags = 16;
    public const int MaxOutputs = 32;
    public const int MaxLogs = 16;
    public const int MaxLogChars = 1024;
    // 预览用的调用轨迹与串口遥测是诊断信息，超出后截断而不是报错。
    public const int MaxTrace = 64;
    public const int MaxSerialUses = 32;
    public const int MaxDiagnostics = 20;
    public const int MaxDiagnosticChars = 4_000;
    public const int MaxSerialCommandChars = 512;
    public const int MaxSerialReplyChars = 4_096;
    public const int MaxFrameBytes = 131_072;
}
