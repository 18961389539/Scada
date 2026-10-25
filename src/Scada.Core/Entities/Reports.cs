namespace Scada.Core.Entities;

public enum ReportPeriod { Shift = 0, Day = 1, Month = 2 }

public enum ReportColumnKind
{
    TagAvg = 0, TagMax = 1, TagMin = 2,
    // 区间末值 − 区间初值（适合累计表/计数器读数）。
    TagDiff = 3,
    // 时间累计：Σ(小时均值 × 小时数) × 系数（适合流量、功率等速率量）。
    TagSum = 4,
    // 计算任务复位快照中的某个输出（按快照周期起点落入行区间求和）。
    CalcSnapshot = 5,
    // 行区间内开始的报警数（模板区域内、不低于最低等级）。
    AlarmCount = 6,
    // 公式：用 $列键 引用同一行的其他列，语法同计算测点表达式。
    Formula = 7,
    // 报警 KPI（ISA-18.2 / EEMUA 191）：Output 为指标键（见 ReportTemplates.AlarmKpiMetrics），模板区域内统计；汇总行按整期重算。
    AlarmKpi = 8,
    // OEE KPI：Output 为指标键（见 ReportTemplates.OeeKpiMetrics），按模板区域下的产线统计；汇总行按整期重算。
    OeeKpi = 9
}

public sealed class ReportColumn
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ReportColumnKind Kind { get; set; }
    public int? TagId { get; set; }
    public int? JobId { get; set; }
    public string? Output { get; set; }
    public double Factor { get; set; } = 1;
    public int? MinLevel { get; set; }
    public string? Formula { get; set; }
    public int Decimals { get; set; } = 2;
    public string? Unit { get; set; }
}

// 报表模板：周期、列定义、定时生成与邮件投递设置。
public class ReportTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ReportPeriod Period { get; set; } = ReportPeriod.Day;
    public int AreaId { get; set; } = Area.DefaultId;
    public string ShiftStarts { get; set; } = "08:00,20:00";
    public string ColumnsJson { get; set; } = "[]";
    public bool ScheduleEnabled { get; set; }
    // 周期结束后延迟若干分钟再生成，等待历史聚合与补传落库。
    public int ScheduleDelayMin { get; set; } = ReportTemplate.DefaultScheduleDelayMin;
    public const int DefaultScheduleDelayMin = 10;
    // 定时生成的起算时刻（开启定时时记录）：自动回补不早于此时刻结束的周期，避免开启定时就回补历史。
    public long? ScheduleSinceTs { get; set; }
    public int? EmailChannelId { get; set; }
    public string EmailRecipients { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
}

// 已生成报表：只追加、不修改；同一周期重新生成得到新版本，旧版本保留。
public class ReportRun
{
    public long Id { get; set; }
    public int TemplateId { get; set; }
    public long TemplateRevision { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public int AreaId { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public long FromTs { get; set; }
    public long ToTs { get; set; }
    public int Version { get; set; }
    public string Trigger { get; set; } = "manual";
    public string GeneratedBy { get; set; } = string.Empty;
    public long GeneratedTs { get; set; }
    // 生成时的模板定义与表格数据（JSON），用于审阅与复核。
    public string TemplateJson { get; set; } = "{}";
    public string DataJson { get; set; } = "{}";
    public string XlsxPath { get; set; } = string.Empty;
    public string XlsxSha256 { get; set; } = string.Empty;
    public string PdfPath { get; set; } = string.Empty;
    public string PdfSha256 { get; set; } = string.Empty;
    // 归档文件按保留期删除的时刻；记录（元数据、数据快照、哈希）保留，文件不再可下载。
    public long? PurgedTs { get; set; }
    // 归档时的数字签名（PKCS#7/CMS 分离签名，Base64 DER），与生成同一次写入，之后不修改。为空表示未签名（功能启用前生成或签名失败）。
    public string? XlsxSignature { get; set; }
    public string? PdfSignature { get; set; }
    public string? SignerThumbprint { get; set; }
    public string? SignerSubject { get; set; }
    public long? SignedTs { get; set; }
    public string? SignatureError { get; set; }
}

// 报表电子签名审批（审核/批准），只追加。每条记录对“报表文件哈希 + 步骤 + 结论 + 签名人 + 理由 + 时间”的清单做服务器证书签名，
// 签名人须重新输入密码确认身份。
public class ReportApproval
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Step { get; set; } = "review";       // review | approve
    public string Decision { get; set; } = "approved"; // approved | rejected
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Reason { get; set; } = "";
    public long Ts { get; set; }
    public string ManifestJson { get; set; } = "{}";
    public string Signature { get; set; } = "";
    public string SignerThumbprint { get; set; } = "";
}
