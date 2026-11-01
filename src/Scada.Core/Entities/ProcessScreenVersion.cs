namespace Scada.Core.Entities;

public sealed class ProcessScreenVersion
{
    public int Id { get; set; }
    public int ScreenId { get; set; }
    public int Revision { get; set; }
    public string Name { get; set; } = "";
    public string DocumentJson { get; set; } = "{}";
    public long PublishedTs { get; set; }
    public string PublishedBy { get; set; } = "";
    public string Note { get; set; } = "";
    public int? RestoredFromRevision { get; set; }
    public string Kind { get; set; } = "publish";
}
