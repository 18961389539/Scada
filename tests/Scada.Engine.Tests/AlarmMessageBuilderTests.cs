using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Engine.Alarms;

namespace Scada.Engine.Tests;

// 报警文案必须自带对象与数据：设备/测点、当前值、单位、阈值、恢复条件，状态类必须有变化方向。
public class AlarmMessageBuilderTests
{
    private static Tag NewTag(string name = "排气温度", string? unit = "℃") =>
        new() { Id = 1, DeviceId = 7, Name = name, Unit = unit };

    private static Device NewDevice() => new() { Id = 7, Name = "1# 螺杆空压机" };

    private static AlarmRule Rule(AlarmRuleType type, double threshold, string? message = null, double deadband = 1) =>
        new() { Id = 1, TagId = 1, Type = type, Threshold = threshold, Level = AlarmLevel.Warning, RecoverDeadband = deadband, Message = message };

    [Fact]
    public void Auto_Message_Carries_Device_Value_Unit_Threshold_And_Recovery()
    {
        var text = AlarmMessageBuilder.Build(NewTag(), NewDevice(), Rule(AlarmRuleType.High, 95), 101.4);
        Assert.Equal("1# 螺杆空压机 排气温度 高限（当前 101.4 ℃，阈值 95 ℃，恢复 ≤ 94 ℃）", text);
    }

    [Fact]
    public void Auto_Message_For_Low_Limit_Uses_Greater_Or_Equal_Recovery()
    {
        var text = AlarmMessageBuilder.Build(NewTag("汽包水位", "mm"), NewDevice(), Rule(AlarmRuleType.LowLow, -120, deadband: 5), -131);
        Assert.Contains("恢复 ≥ -115 mm", text);
    }

    [Fact]
    public void State_Change_Message_Shows_Direction_Without_Rule_Text()
    {
        var text = AlarmMessageBuilder.Build(NewTag("给水泵运行", null), NewDevice(), Rule(AlarmRuleType.StateChange, 0.5), 0, previousValue: 1);
        Assert.Equal("1# 螺杆空压机 给水泵运行 状态变化：1 → 0", text);
    }

    [Fact]
    public void Custom_Message_Gets_Current_Value_Appended()
    {
        var text = AlarmMessageBuilder.Build(NewTag(), NewDevice(), Rule(AlarmRuleType.High, 95, "空压机排气高温，请检查冷却和润滑"), 101.4);
        Assert.Equal("空压机排气高温，请检查冷却和润滑（当前 101.4 ℃，阈值 95 ℃，恢复 ≤ 94 ℃）", text);
    }

    [Fact]
    public void Placeholder_Template_Is_Rendered_Without_Suffix()
    {
        var rule = Rule(AlarmRuleType.High, 95, "{device} {tag} 超限：{value}（限 {threshold}），请检查冷却");
        var text = AlarmMessageBuilder.Build(NewTag(), NewDevice(), rule, 101.4);
        Assert.Equal("1# 螺杆空压机 排气温度 超限：101.4 ℃（限 95 ℃），请检查冷却", text);
    }

    [Fact]
    public void Legacy_Label_Only_Message_Falls_Back_To_Auto_Text()
    {
        var text = AlarmMessageBuilder.Build(NewTag(), NewDevice(), Rule(AlarmRuleType.High, 95, "高限"), 101.4);
        Assert.StartsWith("1# 螺杆空压机 排气温度 高限（当前 101.4 ℃", text);
        Assert.True(Scada.Core.Models.AlarmRuleText.IsPlaceholderOnly("状态变化"));
        Assert.False(Scada.Core.Models.AlarmRuleText.IsPlaceholderOnly("出口温度高高限"));
    }

    [Fact]
    public void Device_Offline_Message_Contains_Device_Name_And_Reason()
    {
        Assert.Equal("设备离线：1# 螺杆空压机：Socket not connected", AlarmMessageBuilder.DeviceOffline(NewDevice(), "Socket not connected"));
        Assert.Contains("1# 螺杆空压机", AlarmMessageBuilder.DeviceOffline(NewDevice(), null));
    }

    [Fact]
    public void Long_Message_Is_Trimmed_To_Column_Length()
    {
        var text = AlarmMessageBuilder.Build(NewTag(), NewDevice(), Rule(AlarmRuleType.High, 95, new string('长', 600)), 101.4);
        Assert.Equal(AlarmMessageBuilder.MaxLength, text.Length);
    }
}
