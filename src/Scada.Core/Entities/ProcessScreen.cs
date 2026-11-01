namespace Scada.Core.Entities;

public sealed class ProcessScreen
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DraftJson { get; set; } = "{}";
    public string? PublishedName { get; set; }
    public string? PublishedJson { get; set; }
    public int Revision { get; set; } = 1;
    public int? PublishedRevision { get; set; }
    public bool DraftDirty { get; set; } = true;
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}
