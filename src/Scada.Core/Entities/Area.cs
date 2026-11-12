using System.Text.Json.Serialization;
namespace Scada.Core.Entities;

// 区域/车间：设备归属到区域，测点继承设备区域；授权按区域限定。
public class Area : IAuditedEntity
{
    public const int DefaultId = 1;
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // 审计拦截器用的派生属性；[JsonIgnore] 防止它们进工程包与接口契约。
    [JsonIgnore] public string AuditTargetType => "Area";
    [JsonIgnore] public string AuditLabel => "区域";
    [JsonIgnore] public string AuditName => Name;
}
