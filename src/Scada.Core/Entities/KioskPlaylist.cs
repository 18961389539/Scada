using System.Text.Json.Serialization;

namespace Scada.Core.Entities;

/// <summary>可保存的 Kiosk 播放列表：画面顺序、轮播间隔、是否监视模式。随工程包迁移。</summary>
public sealed class KioskPlaylist : IAuditedEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>已发布画面编号的 JSON 数组，例如 [12,15,9]。</summary>
    public string ScreenIdsJson { get; set; } = "[]";
    public int IntervalSeconds { get; set; } = 20;
    public bool MonitorOnly { get; set; }
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = "";

    [JsonIgnore] public string AuditTargetType => "KioskPlaylist";
    [JsonIgnore] public string AuditLabel => "Kiosk 播放列表";
    [JsonIgnore] public string AuditName => Name;
}
