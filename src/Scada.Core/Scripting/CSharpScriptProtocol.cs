namespace Scada.Core.Scripting;

public sealed record ScriptInput(double Value, bool Good, long Timestamp);
public sealed record CSharpScriptRequest(string Id, string Operation, string Source,
    Dictionary<int, ScriptInput>? Inputs = null, int[]? WritableTags = null, long Now = 0, double DeltaSeconds = 0);
public sealed record CSharpScriptReply(string Id, bool Success, string[] OutputNames,
    Dictionary<string, double> Outputs, Dictionary<int, double> Writes, string[] Diagnostics,
    string? Error = null, double ElapsedMs = 0, string[]? Logs = null);

public interface IScadaScript
{
    Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken);
}

public sealed class ScriptContext
{
    private readonly IReadOnlyDictionary<int, ScriptInput> _inputs;
    private readonly HashSet<int> _writable;
    private readonly HashSet<string> _outputNames;
    public Dictionary<string, double> Outputs { get; } = new();
    public Dictionary<int, double> Writes { get; } = new();
    public List<string> Logs { get; } = new();
    public DateTimeOffset Now { get; }
    public double DeltaSeconds { get; }

    public ScriptContext(CSharpScriptRequest request, IEnumerable<string> outputNames)
    {
        _inputs = request.Inputs ?? new();
        _writable = (request.WritableTags ?? []).ToHashSet();
        _outputNames = outputNames.ToHashSet(StringComparer.Ordinal);
        Now = DateTimeOffset.FromUnixTimeMilliseconds(request.Now);
        DeltaSeconds = request.DeltaSeconds;
    }

    public double Read(int tagId)
    {
        if (!_inputs.TryGetValue(tagId, out var input) || !input.Good || !double.IsFinite(input.Value))
            throw new InvalidOperationException($"Input #{tagId} is not permitted, missing, stale or not Good.");
        return input.Value;
    }

    public void Output(string name, double value)
    {
        if (!_outputNames.Contains(name) || !double.IsFinite(value))
            throw new InvalidOperationException("Output must be declared with a constant name and a finite value.");
        Outputs[name] = value;
    }

    public void Write(int tagId, double value)
    {
        if (!_writable.Contains(tagId) || !double.IsFinite(value))
            throw new InvalidOperationException($"Write #{tagId} is not permitted or its value is not finite.");
        Writes[tagId] = value;
    }

    public void Log(string message)
    {
        if (Logs.Count >= 16) throw new InvalidOperationException("At most 16 log messages are allowed per execution.");
        Logs.Add((message ?? "")[..Math.Min(message?.Length ?? 0, 1024)]);
    }
}