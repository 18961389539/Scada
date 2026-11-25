namespace Scada.Core.Entities;

/// <summary>
/// 厂级运营组态（单行）。只放会影响按日切分或历史分桶、且应随配置库复制/备份的项。
/// 不要把 JWT、Ha:SharedKey、监听地址等部署项写进本表。
/// </summary>
public sealed class SiteOperationsSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string? ShiftTimeZone { get; set; }
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}
