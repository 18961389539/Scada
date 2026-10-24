namespace Scada.Core.Entities;

// 区域/车间：设备归属到区域，测点继承设备区域；授权按区域限定。
public class Area
{
    public const int DefaultId = 1;
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
