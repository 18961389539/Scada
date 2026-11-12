namespace Scada.Core.Models;

/// <summary>
/// 控制结果以 <see cref="GatewayCommandOutcome"/> 为准。调用方把结果写在异常上，
/// 账本不再根据中文措辞猜测能不能重发。
/// </summary>
public static class CommandFailure
{
    public const string Key = "Scada.CommandOutcome";

    public static T Tag<T>(T exception, GatewayCommandOutcome outcome) where T : Exception
    {
        exception.Data[Key] = outcome;
        return exception;
    }

    public static GatewayCommandOutcome Read(Exception exception) =>
        exception.Data.Contains(Key) && exception.Data[Key] is GatewayCommandOutcome outcome
            ? outcome
            : GatewayCommandOutcome.Failed;
}

public static class CommandResults
{
    public static CommandExecutionResult Of(GatewayCommandOutcome outcome, string message, double? readBack = null, string? readBackText = null) =>
        new(outcome == GatewayCommandOutcome.Confirmed, message, readBack, outcome, ReadBackText: readBackText);

    /// <summary>补齐缺失的结果分类。没有显式分类的失败一律按已执行失败处理，不允许重发。</summary>
    public static CommandExecutionResult Normalize(CommandExecutionResult result, Guid? id = null)
    {
        var outcome = result.Outcome ?? (result.Success ? GatewayCommandOutcome.Confirmed : GatewayCommandOutcome.Failed);
        return result with
        {
            Success = outcome == GatewayCommandOutcome.Confirmed,
            Outcome = outcome,
            CommandId = result.CommandId ?? id
        };
    }
}
