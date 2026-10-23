using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class AlarmRule
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public AlarmRuleType Type { get; set; }
    public double Threshold { get; set; }
    public AlarmLevel Level { get; set; } = AlarmLevel.Warning;
    public int DelaySec { get; set; }
    public double RecoverDeadband { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Message { get; set; }
}
