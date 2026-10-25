namespace Scada.Core.Entities;

// 运行布局：多显示器、每屏若干区域（总览画面、报警横幅、报警列表、详情画面等）。
public sealed class HmiLayout
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DocumentJson { get; set; } = "{}";
    public int Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = "";
}

// 操作员工作站：浏览器在本机登记工作站名称后，自动打开分配给它的运行布局。属于现场环境，不随工程包迁移。
public sealed class HmiWorkstation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int? LayoutId { get; set; }
    // 该工作站是否允许控制（例如走廊大屏只监视）；与用户权限同时生效，服务端强制。
    public bool AllowControl { get; set; } = true;
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = "";
}
