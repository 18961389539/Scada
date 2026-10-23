namespace Scada.Core.Entities;

public sealed class DeviceObjectTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DraftJson { get; set; } = "{}";
    public int Revision { get; set; } = 1;
    public int LatestVersion { get; set; }
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class DeviceObjectTemplateVersion
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Version { get; set; }
    public string DocumentJson { get; set; } = "{}";
    public long CreatedTs { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class DeviceObjectInstance
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int AppliedVersion { get; set; }
    public int DeviceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int BaseAddress { get; set; }
    public int ScreenId { get; set; }
    public int Revision { get; set; } = 1;
    public string BaselineJson { get; set; } = "{}";
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}
