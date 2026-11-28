namespace Scada.Core.Entities;

// 生产批次执行与批记录（第一期）：必须绑定配方，可选顺控。
// 名称躲开网关 Inbox 的 GatewayInboxBatch，不把顺控改造成 ISA-88 单元程序。

public enum ProductionBatchStatus
{
    Starting = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Aborted = 4,
    NeedsConfirmation = 5
}

public class ProductionBatch
{
    public int Id { get; set; }
    public string BatchNumber { get; set; } = "";
    public int AreaId { get; set; }
    public int RecipeId { get; set; }
    public string RecipeName { get; set; } = "";
    public int RecipeVersion { get; set; }
    public int? SequenceId { get; set; }
    public string? SequenceName { get; set; }
    public int? RecipeDownloadId { get; set; }
    public int? ControlSequenceRunId { get; set; }
    public Guid? SequenceRequestId { get; set; }
    public ProductionBatchStatus Status { get; set; }
    public long StartedTs { get; set; }
    public long? EndedTs { get; set; }
    public int? StartedByUserId { get; set; }
    public string StartedBy { get; set; } = "";
    public string? Message { get; set; }
}
