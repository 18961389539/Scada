namespace Scada.Desktop.Configuration;

/// <summary>
/// 桌面客户端与 Web 前端之间的会话契约。键名来自 web/src/api/http.ts 与 hmiLayouts.ts；
/// 集中在这里，前端若调整存储方式只需改一处并补集成冒烟。
/// </summary>
internal static class FrontendContract
{
    public const string TokenKey = "scada_token";
    public const string UserKey = "scada_user";
    public const string ExpiresKey = "scada_token_expires";
    public const string WorkstationKey = "scada_workstation";
    public const string WorkstationIdKey = "scada_workstation_id";
    public const string MonitorOnlyKey = "scada_monitor_only";
    public const string LocaleKey = "scada_locale";
    public const string DesktopBootKey = "__scada_desktop_boot";
}
