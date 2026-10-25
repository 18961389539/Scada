using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Engine.Alarms;

// 声光仲裁的纯函数：最高活动未确认等级驱动灯色；消音压制喇叭；锁存/跟随决定灯是否保持。
public sealed record AnnunciatorAlarmSample(
    AlarmLevel Level,
    int? AreaId,
    bool Active,
    bool Unacked,
    bool Shelved,
    bool Suppressed,
    bool Chattering);

public sealed record AnnunciatorDesired(
    AlarmLevel? DrivingLevel,
    bool Horn,
    bool Beacon,
    bool TowerRed,
    bool TowerYellow,
    bool TowerBlue,
    bool TowerGreen,
    bool Flashing,
    bool Silenced,
    string? SoundKey)
{
    public static AnnunciatorDesired Idle(bool silenced = false) =>
        new(null, false, false, false, false, false, false, false, silenced, null);
}

public static class AnnunciatorLogic
{
    // 可发声光的报警：活动、未确认、未暂挂/抑制/抖动。
    public static bool IsAnnunciable(AnnunciatorAlarmSample a) =>
        a.Active && a.Unacked && !a.Shelved && !a.Suppressed && !a.Chattering;

    public static bool MatchesPanel(AnnunciatorAlarmSample a, int? panelAreaId) =>
        panelAreaId is null || (a.AreaId is int area && area == panelAreaId);

    public static AnnunciatorDesired Compute(
        IEnumerable<AnnunciatorAlarmSample> alarms,
        AnnunciatorPanel panel,
        IReadOnlyList<AnnunciatorLevelMap> maps,
        long nowMs,
        long? silenceUntilMs,
        bool hadDrivingBefore = false)
    {
        var silenced = silenceUntilMs is long until && until > nowMs;
        var candidates = alarms.Where(a => IsAnnunciable(a) && MatchesPanel(a, panel.AreaId)).ToList();
        if (candidates.Count == 0)
        {
            if (panel.LatchMode == AnnunciatorLatchMode.Latch && hadDrivingBefore && !silenced)
            {
                // 锁存：无驱动时仍保持上次灯态由调用方自行缓存；这里返回空灯 + 无喇叭，调用方用 latched 缓存覆盖灯通道。
                return AnnunciatorDesired.Idle(silenced) with { Horn = false };
            }
            return AnnunciatorDesired.Idle(silenced);
        }

        var level = candidates.Max(a => a.Level);
        var map = maps.FirstOrDefault(m => m.Level == level)
                  ?? DefaultMap(level);
        var horn = map.Horn && !silenced;
        var sound = silenced || !panel.BrowserSound ? null : (string.IsNullOrWhiteSpace(map.SoundKey) ? level.ToString().ToLowerInvariant() : map.SoundKey);
        return new AnnunciatorDesired(
            level,
            horn,
            map.Beacon,
            map.TowerRed,
            map.TowerYellow,
            map.TowerBlue,
            map.TowerGreen,
            map.Flash,
            silenced,
            sound);
    }

    // 锁存模式下：消音或显式复位后清除灯；跟随模式始终以 Compute 为准。
    public static AnnunciatorDesired ApplyLatch(
        AnnunciatorDesired computed,
        AnnunciatorDesired? latchedLights,
        AnnunciatorLatchMode mode,
        bool clearLatch)
    {
        if (mode != AnnunciatorLatchMode.Latch || clearLatch || computed.DrivingLevel is not null || latchedLights is null)
            return computed;
        // 无驱动且未清除锁存：保留灯，喇叭仍跟随 computed（已关）。
        return computed with
        {
            Beacon = latchedLights.Beacon,
            TowerRed = latchedLights.TowerRed,
            TowerYellow = latchedLights.TowerYellow,
            TowerBlue = latchedLights.TowerBlue,
            TowerGreen = latchedLights.TowerGreen,
            Flashing = latchedLights.Flashing,
            DrivingLevel = latchedLights.DrivingLevel
        };
    }

    public static double ChannelValue(AnnunciatorDesired desired, AnnunciatorChannelKind kind, bool flashPhaseOn)
    {
        var on = kind switch
        {
            AnnunciatorChannelKind.Horn => desired.Horn,
            AnnunciatorChannelKind.Beacon => desired.Beacon,
            AnnunciatorChannelKind.TowerRed => desired.TowerRed,
            AnnunciatorChannelKind.TowerYellow => desired.TowerYellow,
            AnnunciatorChannelKind.TowerBlue => desired.TowerBlue,
            AnnunciatorChannelKind.TowerGreen => desired.TowerGreen,
            _ => false
        };
        if (!on) return 0; // caller maps to InactiveValue
        if (desired.Flashing && kind != AnnunciatorChannelKind.Horn)
            return flashPhaseOn ? 1 : 0;
        return 1;
    }

    public static AnnunciatorLevelMap DefaultMap(AlarmLevel level) => level switch
    {
        AlarmLevel.Critical => new() { Level = level, Horn = true, Beacon = true, TowerRed = true, Flash = true, SoundKey = "critical" },
        AlarmLevel.Warning => new() { Level = level, Horn = true, Beacon = true, TowerYellow = true, Flash = true, SoundKey = "warning" },
        _ => new() { Level = level, Horn = false, Beacon = true, TowerBlue = true, Flash = false, SoundKey = "info" }
    };

    public static IReadOnlyList<AnnunciatorLevelMap> DefaultMaps(int panelId)
    {
        AnnunciatorLevelMap Copy(AlarmLevel level) { var m = DefaultMap(level); m.PanelId = panelId; return m; }
        return [Copy(AlarmLevel.Critical), Copy(AlarmLevel.Warning), Copy(AlarmLevel.Info)];
    }
}
