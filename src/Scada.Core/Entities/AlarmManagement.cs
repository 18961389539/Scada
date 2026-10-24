using Scada.Core.Enums;

namespace Scada.Core.Entities;

// 暂挂：操作员临时屏蔽某条报警规则，限时，到期自动恢复；ClearedTs 为空表示仍在暂挂。
public class AlarmShelve
{
    public int Id { get; set; }
    public int RuleId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public long StartTs { get; set; }
    public long UntilTs { get; set; }
    public long? ClearedTs { get; set; }
    public string? ClearedBy { get; set; }
}

// 报警实例的处理时间线：触发、确认（含备注）、备注、暂挂、抑制、升级、外发、恢复。
public class AlarmActivity
{
    public long Id { get; set; }
    public int AlarmEventId { get; set; }
    public long Ts { get; set; }
    public string? User { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class NotificationChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType Type { get; set; }
    public bool Enabled { get; set; } = true;
    // 非敏感参数（JSON）：邮件 host/port/useSsl/userName/from；webhook/短信 url/headerName。
    public string SettingsJson { get; set; } = "{}";
    // 敏感参数（SMTP 口令、webhook/短信鉴权头的值）以 DataProtection 加密保存，接口永不回显。
    public string? SecretProtected { get; set; }
}

public class DutyShift
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // 本地时间的分钟数（0..1439）；End <= Start 表示跨零点。
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    // 星期掩码：bit0=周日 ... bit6=周六；按班次开始那天判断。
    public int Days { get; set; } = 127;
    public bool Enabled { get; set; } = true;
}

public class NotificationRoute
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int ChannelId { get; set; }
    // null 表示全部区域。
    public int? AreaId { get; set; }
    public AlarmLevel MinLevel { get; set; } = AlarmLevel.Warning;
    // null 表示不限班次。
    public int? ShiftId { get; set; }
    // 收件人：邮箱或手机号，逗号/换行分隔；webhook 可为空。
    public string Recipients { get; set; } = string.Empty;
    public bool OnRaise { get; set; } = true;
    public bool OnRecover { get; set; }
    // 有值时为升级路由：报警持续未确认达到该分钟数才发送一次，不参与触发/恢复通知。
    public int? EscalateAfterMinutes { get; set; }
}

public class AlarmNotificationLog
{
    public long Id { get; set; }
    public int AlarmEventId { get; set; }
    public int RouteId { get; set; }
    public int ChannelId { get; set; }
    public string Action { get; set; } = string.Empty;
    public long Ts { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

// 通知发件箱：每条“报警×路由×动作”一行，DedupKey 唯一保证幂等；发送失败按退避重试，重启后继续。
public class NotificationOutbox
{
    public long Id { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public int AlarmEventId { get; set; }
    public int RouteId { get; set; }
    public int ChannelId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = NotificationOutboxStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public long NextAttemptTs { get; set; }
    public long? LeaseUntilTs { get; set; }
    public long CreatedTs { get; set; }
    public long? SentTs { get; set; }
    public string? LastError { get; set; }
}

public static class NotificationOutboxStatus
{
    public const string Pending = "Pending";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}
