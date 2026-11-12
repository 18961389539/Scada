using System.Diagnostics;
using Scada.Core.Enums;

namespace Scada.Core.Scripting;

public sealed record ScriptInput(double Value, bool Good, long Timestamp, string Quality = "Good", string? Text = null);
public sealed record ScriptSample(double Value, string Quality, long Timestamp, bool Good);
public sealed record ScriptAlarm(long Id, string Level, string Message, bool Acknowledged, long StartedAt);
public sealed record ScriptSerialUse(string Profile, bool Ok, double ElapsedMs, string? Error = null);

// 串口由 Worker 宿主持有并跨脚本执行复用；脚本只引用配置档名称，不接触 System.IO.Ports。
public sealed class SerialProfile
{
    public string Port { get; set; } = "";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; }
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;
    public string Encoding { get; set; } = "utf-8";
    public string NewLine { get; set; } = "\r\n";
    public int ReadTimeoutMs { get; set; } = 2000;
    public int MaxReplyChars { get; set; } = 4096;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
}

public sealed record CSharpScriptRequest(string Id, string Operation, string Source,
    Dictionary<int, ScriptInput>? Inputs = null, int[]? WritableTags = null, long Now = 0, double DeltaSeconds = 0,
    Dictionary<string, int>? Names = null, Dictionary<int, ScriptSample[]>? History = null, ScriptAlarm[]? Alarms = null, double? Argument = null, string? LibrarySource = null, int TimeoutMs = 0,
    Dictionary<string, SerialProfile>? SerialProfiles = null);
public sealed record ScriptDiagnostic(string Message, string File, int StartLine, int StartColumn, int EndLine, int EndColumn);
public sealed record CSharpScriptReply(string Id, bool Success, string[] OutputNames,
    Dictionary<string, double> Outputs, Dictionary<int, double> Writes, string[] Diagnostics,
    string? Error = null, double ElapsedMs = 0, string[]? Logs = null, string[]? Trace = null, ScriptDiagnostic[]? Spans = null,
    ScriptSerialUse[]? SerialUses = null);

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
    private readonly Func<string, string, int, string>? _serialExchange;
    public Dictionary<string, double> Outputs { get; } = new();
    public Dictionary<int, double> Writes { get; } = new();
    public List<string> Logs { get; } = new();
    public List<string> Trace { get; } = new();
    public List<ScriptSerialUse> SerialUses { get; } = new();
    public DateTimeOffset Now { get; }
    public double DeltaSeconds { get; }
    public bool HasArgument { get; }
    public double Argument { get; }

    public ScriptContext(CSharpScriptRequest request, IEnumerable<string> outputNames, Func<string, string, int, string>? serialExchange = null)
    {
        _inputs = request.Inputs ?? new();
        _names = new(request.Names ?? [], StringComparer.Ordinal);
        _writable = (request.WritableTags ?? []).ToHashSet();
        _outputNames = outputNames.ToHashSet(StringComparer.Ordinal);
        _history = request.History;
        _alarms = request.Alarms;
        _serialExchange = serialExchange;
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
        if (input.Text is not null)
            throw new InvalidOperationException($"Input #{tagId} is a string; use ReadText instead.");
        Note($"Read #{tagId} = {input.Value}");
        return input.Value;
    }

    public double Read(string name) => Read(Require(name));

    public string ReadText(int tagId)
    {
        if (!_inputs.TryGetValue(tagId, out var input) || !input.Good || input.Text is null)
            throw new InvalidOperationException($"Input #{tagId} is not a permitted, Good string input.");
        Note($"ReadText #{tagId} = {input.Text}");
        return input.Text;
    }

    public string ReadText(string name) => ReadText(Require(name));

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

    // 串口由 Worker 宿主持有：脚本只传配置档名称和命令，等待回复或超时；不暴露 System.IO。
    public string Serial(string profile, string command, int timeoutMs = 2000)
    {
        if (_serialExchange is null) throw new InvalidOperationException("Serial is not available in this host.");
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 64) throw new InvalidOperationException("Serial profile name is invalid.");
        if (string.IsNullOrEmpty(command) || command.Length > 512) throw new InvalidOperationException("Serial command must contain 1 to 512 characters.");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var reply = _serialExchange(profile, command, Math.Clamp(timeoutMs, 50, 60_000));
            if (reply.Length > 4096) throw new InvalidOperationException("Serial reply exceeds 4096 characters.");
            Note($"Serial {profile} {reply.Length} chars");
            if (SerialUses.Count < 32) SerialUses.Add(new(profile, true, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return reply;
        }
        catch (Exception ex)
        {
            if (SerialUses.Count < 32)
                SerialUses.Add(new(profile, false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message[..Math.Min(ex.Message.Length, 1024)]));
            throw;
        }
    }

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