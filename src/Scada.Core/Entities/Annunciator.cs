using Scada.Core.Enums;

namespace Scada.Core.Entities;

// 报警声光联动（喇叭 / 灯塔 / 蜂鸣）：面板 + 输出测点 + 等级映射。运行态静音单独表。

public enum AnnunciatorChannelKind
{
    Horn = 0,
    Beacon = 1,
    TowerRed = 2,
    TowerYellow = 3,
    TowerBlue = 4,
    TowerGreen = 5
}

// 灯的保持方式：跟随 = 无驱动报警即灭；锁存 = 有过驱动后保持到消音或全部清除。
public enum AnnunciatorLatchMode
{
    Follow = 0,
    Latch = 1
}

public class AnnunciatorPanel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    // null = 全局（看全部区域）；否则只看该区域报警。
    public int? AreaId { get; set; }
    public bool Enabled { get; set; } = true;
    public AnnunciatorLatchMode LatchMode { get; set; } = AnnunciatorLatchMode.Follow;
    // 操作员消音默认时长（分钟），1～120。
    public int SilenceMinutes { get; set; } = 5;
    // 普通闪烁：亮/灭毫秒；危急可单独更短。
    public int FlashOnMs { get; set; } = 500;
    public int FlashOffMs { get; set; } = 500;
    public int CriticalFlashOnMs { get; set; } = 250;
    public int CriticalFlashOffMs { get; set; } = 250;
    // 浏览器提示音：按最高未确认等级循环播放，直到消音/静音/无未确认。
    public bool BrowserSound { get; set; } = true;
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public class AnnunciatorOutput
{
    public int Id { get; set; }
    public int PanelId { get; set; }
    public AnnunciatorChannelKind Kind { get; set; }
    // 输出必须是可写物理测点（含 MQTT/Sparkplug/Modbus 等驱动映射的写点）。
    public int TagId { get; set; }
    public double ActiveValue { get; set; } = 1;
    public double InactiveValue { get; set; } = 0;
    public bool Enabled { get; set; } = true;
}

// 每个面板每个等级一条：决定该等级驱动哪些通道、是否闪烁、浏览器音色。
public class AnnunciatorLevelMap
{
    public int Id { get; set; }
    public int PanelId { get; set; }
    public AlarmLevel Level { get; set; }
    public bool Horn { get; set; }
    public bool Beacon { get; set; }
    public bool TowerRed { get; set; }
    public bool TowerYellow { get; set; }
    public bool TowerBlue { get; set; }
    public bool TowerGreen { get; set; }
    public bool Flash { get; set; }
    // critical / warning / info；空则按等级默认。
    public string? SoundKey { get; set; }
}

// 面板消音状态（运行态，不进工程包）。
public class AnnunciatorSilence
{
    public int PanelId { get; set; }
    public long UntilTs { get; set; }
    public long StartedTs { get; set; }
    public string ByUser { get; set; } = "";
    public string? Reason { get; set; }
}
