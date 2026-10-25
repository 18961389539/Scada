namespace Scada.Core.Entities;

// 用户图库项：符号（SVG）或参数化模板（面板，含占位符）。内置 ISA-101 符号在前端绘制，不入库。
public sealed class HmiLibraryItem
{
    public int Id { get; set; }
    // 自然键：画面按键引用（模板实例 templateKey、符号 user:键），跨环境迁移不依赖编号。
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "symbol"; // symbol | template
    public string? Svg { get; set; }
    public string? DocumentJson { get; set; }
    public string PlaceholdersJson { get; set; } = "[]";
    public double Width { get; set; } = 140;
    public double Height { get; set; } = 112;
    public int Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = "";
}
