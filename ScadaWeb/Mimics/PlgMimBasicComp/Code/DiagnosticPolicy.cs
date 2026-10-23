using System.Text.Json;
using Scada.Data.Models;

namespace Scada.Web.Plugins.PlgMimBasicComp.Code;

/// <summary>Reset bindings are trusted server configuration, never supplied by a mimic or POST body.</summary>
public sealed class DiagnosticPolicy
{
    public string Id { get; set; } = "";
    public int FaultChannel { get; set; }
    public List<int> ResetAllowedCodes { get; set; } = [];
    public ParameterValue Reset { get; set; } = new();
    public List<PermitCondition> Conditions { get; set; } = [];
    public static List<DiagnosticPolicy> Load(string storageDir)
    {
        string path = Path.Combine(storageDir, "industrial-diagnostics.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<DiagnosticPolicy>>(File.ReadAllText(path), new JsonSerializerOptions {PropertyNameCaseInsensitive = true}) ?? [] : [];
    }
    public bool Permitted(CnlData fault, CnlData[] conditions) =>
        OperationsEngine.Good(fault.Stat) && double.IsFinite(fault.Val) && fault.Val == Math.Truncate(fault.Val) &&
        fault.Val > 0 && fault.Val <= int.MaxValue && ResetAllowedCodes.Contains((int)fault.Val) && conditions.Length == Conditions.Count &&
        Conditions.Select((c, i) => c.Matches(conditions[i])).All(v => v);
}
public sealed class PermitCondition
{
    public int Channel { get; set; }
    public double Expected { get; set; } = 1;
    public bool Matches(CnlData data) => OperationsEngine.Good(data.Stat) && double.IsFinite(data.Val) && double.IsFinite(Expected) && Math.Abs(data.Val - Expected) < 1e-6;
}
