using Scada.Core.Entities;

namespace Scada.Engine.Calculations;

/// <summary>
/// 发布副本同步：把 <see cref="CalcJob"/> 的 Published* 字段搬进运行时字段。
/// 这是唯一入口；新增发布字段必须在这里复制，否则草稿值会在运行中生效。
/// </summary>
public static class CalcJobPublishedSync
{
    public static void UsePublished(CalcJob job)
    {
        if (!job.IsScript || job.PublishedRevision == 0) return;
        job.Script = job.PublishedScript; job.ReadTags = job.PublishedReadTags; job.WriteTags = job.PublishedWriteTags;
        // 库名也要跟随发布副本：运行时用它做依赖检查与"是否需要重新发布"的判定，
        // 漏抄会让草稿里新加的库名立刻影响正在运行的脚本。
        job.Libraries = job.PublishedLibraries;
        job.HistoryWindowSec = job.PublishedHistoryWindowSec; job.AlarmSnapshot = job.PublishedAlarmSnapshot;
        job.Trigger = job.PublishedTrigger; job.IntervalMs = job.PublishedIntervalMs;
        job.ChangeDeadband = job.PublishedChangeDeadband; job.ChangeCooldownMs = job.PublishedChangeCooldownMs;
        job.AlarmMinLevel = job.PublishedAlarmMinLevel; job.AlarmOnRaise = job.PublishedAlarmOnRaise;
        job.AlarmOnAck = job.PublishedAlarmOnAck; job.AlarmOnRecover = job.PublishedAlarmOnRecover;
    }
}