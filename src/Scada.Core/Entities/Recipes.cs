namespace Scada.Core.Entities;

// 配方管理：模板定义参数（绑定可写测点、单位、上下限），配方是模板的一组参数值，每次修改生成新版本（版本不可变）。
// 下发经过现场控制闭环的同一路径（ControlCommandService），先整体预检查，全部通过才开始逐个写入。

public sealed class RecipeParameter
{
    // 参数键：模板内唯一，配方值按键对应（改测点不影响已有配方）。
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int TagId { get; set; }
    public string? Unit { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? DefaultValue { get; set; }
}

public class RecipeTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int AreaId { get; set; } = Area.DefaultId;
    public string? Description { get; set; }
    // 整批下发需双人复核（参数测点自身要求复核时，下发也一律走复核）。
    public bool DualApproval { get; set; }
    public string ParametersJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public class Recipe
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int CurrentVersion { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class RecipeValue
{
    public string Key { get; set; } = "";
    public double Value { get; set; }
}

public class RecipeVersion
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public int Version { get; set; }
    public string ValuesJson { get; set; } = "[]"; // RecipeValue 数组
    public string? Note { get; set; }
    public string Source { get; set; } = "manual"; // manual | upload | project
    public long CreatedTs { get; set; }
    public string CreatedBy { get; set; } = "";
}

public enum RecipeDownloadStatus { Pending = 0, Running = 1, Completed = 2, Failed = 3, PartiallyApplied = 4, Aborted = 5, Rejected = 6 }

// 一次配方下发的完整记录（逐参数结果）。
public class RecipeDownload
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public string RecipeName { get; set; } = "";
    public int Version { get; set; }
    public int TemplateId { get; set; }
    public int AreaId { get; set; }
    public RecipeDownloadStatus Status { get; set; }
    public int? RequestedByUserId { get; set; }
    public string RequestedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public Guid? ApprovalId { get; set; }
    public long CreatedTs { get; set; }
    public long? StartedTs { get; set; }
    public long? EndedTs { get; set; }
    public int Written { get; set; }
    public string ResultsJson { get; set; } = "[]"; // RecipeParameterResult 数组
    public string? Message { get; set; }
}

public sealed class RecipeParameterResult
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int TagId { get; set; }
    public double Target { get; set; }
    public double? Before { get; set; }
    public double? ReadBack { get; set; }
    // pending | done | failed | unknown | skipped
    public string Status { get; set; } = "pending";
    public string? Message { get; set; }
}
