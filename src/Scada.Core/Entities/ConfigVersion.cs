namespace Scada.Core.Entities;

// 配置版本快照：整个工程（区域、角色、设备、测点、计算、报警规则、报表模板、对象模板、画面）的导出包，只追加不修改。
public sealed class ConfigVersion
{
    public int Id { get; set; }
    // manual=手动；import=导入发布后；pre-import=导入前；rollback=回滚后；pre-rollback=回滚前；screen-publish=画面发布后
    public string Kind { get; set; } = "manual";
    public string Note { get; set; } = string.Empty;
    public long CreatedTs { get; set; }
    public int? CreatedByUserId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string PackageJson { get; set; } = "{}";
    // 内容哈希（不含导出时间/导出人），用于判断两个版本是否相同及并发检测。
    public string ContentSha256 { get; set; } = string.Empty;
    public string SummaryJson { get; set; } = "{}";
}
