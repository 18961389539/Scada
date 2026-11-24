using Scada.Core.Models;

namespace Scada.Core.Configuration;

/// <summary>
/// 核心组态写入的修订号约定。
/// 省略修订号视为旧客户端：允许写入，但仍递增，避免手工脚本与未升级的集成突然全部失败。
/// 一旦请求带上修订号，必须与当前值一致，否则视为冲突，要求重新打开并合并。
/// </summary>
public static class ConfigRevision
{
    public const string ConflictMessage = "配置已被其他用户修改，请重新打开并合并";
    public const string ConflictCode = "config.revision.conflict";
    public const string Field = "revision";

    public static bool TryAccept(long current, long? expected, out ValidationIssue? issue)
    {
        if (expected is null || expected == 0 || expected == current)
        {
            issue = null;
            return true;
        }

        issue = new ValidationIssue(ConflictCode, Field, ConflictMessage);
        return false;
    }

    public static long Next(long current) => checked(current < 1 ? 2 : current + 1);

    public static long Initial => 1;
}
