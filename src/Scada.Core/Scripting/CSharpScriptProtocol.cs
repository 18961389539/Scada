namespace Scada.Core.Scripting;

public sealed record ScriptInput(double Value, bool Good, long Timestamp, string Quality = "Good");
public sealed record ScriptSample(double Value, string Quality, long Timestamp, bool Good);
public sealed record ScriptAlarm(long Id, string Level, string Message, bool Acknowledged, long StartedAt);
public sealed record CSharpScriptRequest(string Id, string Operation, string Source,
    Dictionary<int, ScriptInput>? Inputs = null, int[]? WritableTags = null, long Now = 0, double DeltaSeconds = 0,
    Dictionary<string, int>? Names = null, Dictionary<int, ScriptSample[]>? History = null, ScriptAlarm[]? Alarms = null, double? Argument = null, string? LibrarySource = null);
public sealed record ScriptDiagnostic(string Message, string File, int StartLine, int StartColumn, int EndLine, int EndColumn);
public sealed record CSharpScriptReply(string Id, bool Success, string[] OutputNames,
    Dictionary<string, double> Outputs, Dictionary<int, double> Writes, string[] Diagnostics,
    string? Error = null, double ElapsedMs = 0, string[]? Logs = null, string[]? Trace = null, ScriptDiagnostic[]? Spans = null);

public interface IScadaScript
{
    Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken);
}

public sealed class ScriptContext
{
    private readonly IReadOnlyDictionary<int, ScriptInput> _inputs;
    private readonly Dictionary<string, int> _names;
    private readonly HashSet<int> _writable;
    private readonly HashSet<string> _outputNames;
    private readonly IReadOnlyDictionary<int, ScriptSample[]>? _history;
    private readonly IReadOnlyList<ScriptAlarm>? _alarms;
    public Dictionary<string, double> Outputs { get; } = new();
    public Dictionary<int, double> Writes { get; } = new();
    public List<string> Logs { get; } = new();
    public List<string> Trace { get; } = new();
    public DateTimeOffset Now { get; }
    public double DeltaSeconds { get; }
    public bool HasArgument { get; }
    public double Argument { get; }

    public ScriptContext(CSharpScriptRequest request, IEnumerable<string> outputNames)
    {
        _inputs = request.Inputs ?? new();
        _names = new(request.Names ?? [], StringComparer.Ordinal);
        _writable = (request.WritableTags ?? []).ToHashSet();
        _outputNames = outputNames.ToHashSet(StringComparer.Ordinal);
        _history = request.History;
        _alarms = request.Alarms;
        Now = DateTimeOffset.FromUnixTimeMilliseconds(request.Now);
        DeltaSeconds = request.DeltaSeconds;
        HasArgument = request.Argument is not null;
        Argument = request.Argument ?? 0;
    }

    private void Note(string text)
    {
        if (Trace.Count < 64) Trace.Add(text);
    }

    public double Read(int tagId)
    {
        if (!_inputs.TryGetValue(tagId, out var input) || !input.Good || !double.IsFinite(input.Value))
            throw new InvalidOperationException($"Input #{tagId} is not permitted, missing, stale or not Good.");
        Note($"Read #{tagId} = {input.Value}");
        return input.Value;
    }

    public double Read(string name) => Read(Require(name));

    public ScriptSample ReadSample(int tagId)
    {
        if (!_inputs.TryGetValue(tagId, out var input))
            throw new InvalidOperationException($"Input #{tagId} is not permitted or missing.");
        Note($"ReadSample #{tagId} {input.Quality}");
        return new ScriptSample(input.Value, input.Quality, input.Timestamp, input.Good && double.IsFinite(input.Value));
    }

    public IReadOnlyList<ScriptSample> History(int tagId)
    {
        if (_history is null) throw new InvalidOperationException("History snapshot was not declared for this task.");
        if (!_history.TryGetValue(tagId, out var points)) throw new InvalidOperationException($"History #{tagId} is not in the declared read list.");
        Note($"History #{tagId} {points.Length}");
        return points;
    }

    public IReadOnlyList<ScriptSample> History(string name) => History(Require(name));

    public IReadOnlyList<ScriptAlarm> Alarms()
    {
        if (_alarms is null) throw new InvalidOperationException("Alarm snapshot was not declared for this task.");
        Note($"Alarms {_alarms.Count}");
        return _alarms;
    }

    public ScriptSample ReadSample(string name) => ReadSample(Require(name));

    public void Output(string name, double value)
    {
        if (!_outputNames.Contains(name) || !double.IsFinite(value))
            throw new InvalidOperationException("Output must be declared with a constant name and a finite value.");
        Outputs[name] = value;
        Note($"Output {name} = {value}");
    }

    public void Write(int tagId, double value)
    {
        if (!_writable.Contains(tagId) || !double.IsFinite(value))
            throw new InvalidOperationException($"Write #{tagId} is not permitted or its value is not finite.");
        Writes[tagId] = value;
        Note($"Write #{tagId} = {value}");
    }

    public void Write(string name, double value) => Write(Require(name), value);

    private int Require(string name)
    {
        if (string.IsNullOrEmpty(name) || !_names.TryGetValue(name, out var id))
            throw new InvalidOperationException($"Tag '{name}' is not in the script whitelist.");
        return id;
    }

    public void Log(string message)
    {
        if (Logs.Count >= 16) throw new InvalidOperationException("At most 16 log messages are allowed per execution.");
        Logs.Add((message ?? "")[..Math.Min(message?.Length ?? 0, 1024)]);
    }
}