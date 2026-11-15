using System.Diagnostics;
using Scada.Core.Enums;

namespace Scada.Core.Scripting;

public sealed record ScriptInput(double Value, bool Good, long Timestamp, string Quality = "Good", string? Text = null);
public sealed record ScriptSample(double Value, string Quality, long Timestamp, bool Good);
public sealed record ScriptAlarm(long Id, string Level, string Message, bool Acknowledged, long StartedAt);
public sealed record ScriptSerialUse(string Profile, bool Ok, double ElapsedMs, string? Error = null);

// 行协议版本。请求、握手帧都带版本号：升级时若只替换了 Server 或 ScriptWorker 其中一侧，
// 必须得到"版本不匹配"的明确错误，而不是一句 "Invalid script worker response"。
// v3：通道从 stdout 文本行改为独立匿名管道 + 长度前缀帧。
public static class ScriptProtocol
{
    public const int Version = 3;

    // 错误分类，供界面区分"脚本自己抛的异常"与"宿主/协议层问题"。
    public const string ErrorScriptThrew = "script_threw";
    public const string ErrorCanceled = "canceled";
    public const string ErrorCompile = "compile";
    public const string ErrorLimits = "limits";
    public const string ErrorProtocol = "protocol";
}

// Worker 启动后主动发出的第一帧。Host 在 Start 阶段必须读到它才认为通道可用。
public sealed record CSharpScriptHello(int ProtocolVersion, string RuntimeVersion, string RoslynVersion);

// 出错位置：脚本自身异常时 File/Line/Column 指向 #line 映射回的用户源码。
public sealed record ScriptError(string Code, string Message, string? File = null, int? Line = null, int? Column = null);

// 串口由 Worker 宿主持有并跨脚本执行复用；脚本只引用配置档名称，不接触 System.IO.Ports。
// 用 record 是为了让"配置是否变化"可以直接按值比较，不必每次序列化一遍做签名。
public sealed record SerialProfile
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
    Dictionary<string, SerialProfile>? SerialProfiles = null, int ProtocolVersion = 0);
public sealed record ScriptDiagnostic(string Message, string File, int StartLine, int StartColumn, int EndLine, int EndColumn);
public sealed record CSharpScriptReply(string Id, bool Success, string[] OutputNames,
    Dictionary<string, double> Outputs, Dictionary<int, double> Writes, string[] Diagnostics,
    string? Error = null, double ElapsedMs = 0, string[]? Logs = null, string[]? Trace = null, ScriptDiagnostic[]? Spans = null,
    ScriptSerialUse[]? SerialUses = null, ScriptError? ErrorDetail = null);

public interface IScadaScript
{
    Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken);
}

// 执行结果快照。脚本可能通过 Task.WhenAll 并发调用 SerialAsync，因此结果集在读取时必须
// 在锁内复制一份，不能把内部集合直接交给宿主序列化。
public sealed record ScriptResultSnapshot(
    IReadOnlyDictionary<string, double> Outputs, IReadOnlyDictionary<int, double> Writes,
    string[] Logs, string[] Trace, ScriptSerialUse[]? SerialUses);

public sealed class ScriptContext
{
    private readonly IReadOnlyDictionary<int, ScriptInput> _inputs;
    private readonly Dictionary<string, int> _names;
    private readonly HashSet<int> _writable;
    private readonly HashSet<string> _outputNames;
    // 输出、写点、日志、轨迹与串口遥测都可能被并发任务写入；统一用一把锁，读写都走它。
    private readonly object _sync = new();
    private readonly IReadOnlyDictionary<int, ScriptSample[]>? _history;
    private readonly IReadOnlyList<ScriptAlarm>? _alarms;
    private readonly Func<string, string, int, CancellationToken, Task<string>>? _serialExchange;
    // 本次执行的取消令牌：脚本超时由 Worker 宿主取消，串口等待必须能观察到它。
    private readonly CancellationToken _cancellation;
    public Dictionary<string, double> Outputs { get; } = new();
    public Dictionary<int, double> Writes { get; } = new();
    public List<string> Logs { get; } = new();
    public List<string> Trace { get; } = new();
    public List<ScriptSerialUse> SerialUses { get; } = new();
    public DateTimeOffset Now { get; }
    public double DeltaSeconds { get; }
    public bool HasArgument { get; }
    public double Argument { get; }

    public ScriptContext(CSharpScriptRequest request, IEnumerable<string> outputNames,
        Func<string, string, int, CancellationToken, Task<string>>? serialExchange = null,
        CancellationToken cancellationToken = default)
    {
        _inputs = request.Inputs ?? new();
        _names = new(request.Names ?? [], StringComparer.Ordinal);
        _writable = (request.WritableTags ?? []).ToHashSet();
        _outputNames = outputNames.ToHashSet(StringComparer.Ordinal);
        _history = request.History;
        _alarms = request.Alarms;
        _serialExchange = serialExchange;
        _cancellation = cancellationToken;
        Now = DateTimeOffset.FromUnixTimeMilliseconds(request.Now);
        DeltaSeconds = request.DeltaSeconds;
        HasArgument = request.Argument is not null;
        Argument = request.Argument ?? 0;
    }

    // 兼容同步委托（测试与轻量宿主）：没有取消能力，等价于旧行为。
    public ScriptContext(CSharpScriptRequest request, IEnumerable<string> outputNames, Func<string, string, int, string> serialExchange)
        : this(request, outputNames, (profile, command, timeout, _) => Task.FromResult(serialExchange(profile, command, timeout)))
    {
    }

    // 轨迹是预览用的诊断信息：满了就不该再格式化字符串。调用点统一用 Tracing 守卫，
    // 否则脚本里上万次 Read 会先造出上万个字符串再被丢弃。
    private bool Tracing
    {
        get { lock (_sync) return Trace.Count < CSharpScriptLimits.MaxTrace; }
    }

    private void Note(string text)
    {
        lock (_sync)
        {
            if (Trace.Count < CSharpScriptLimits.MaxTrace) Trace.Add(text);
        }
    }

    public double Read(int tagId)
    {
        if (!_inputs.TryGetValue(tagId, out var input) || !input.Good || !double.IsFinite(input.Value))
            throw new InvalidOperationException($"Input #{tagId} is not permitted, missing, stale or not Good.");
        if (input.Text is not null)
            throw new InvalidOperationException($"Input #{tagId} is a string; use ReadText instead.");
        if (Tracing) Note($"Read #{tagId} = {input.Value}");
        return input.Value;
    }

    public double Read(string name) => Read(Require(name));

    public string ReadText(int tagId)
    {
        if (!_inputs.TryGetValue(tagId, out var input) || !input.Good || input.Text is null)
            throw new InvalidOperationException($"Input #{tagId} is not a permitted, Good string input.");
        if (Tracing) Note($"ReadText #{tagId} = {input.Text}");
        return input.Text;
    }

    public string ReadText(string name) => ReadText(Require(name));

    public ScriptSample ReadSample(int tagId)
    {
        if (!_inputs.TryGetValue(tagId, out var input))
            throw new InvalidOperationException($"Input #{tagId} is not permitted or missing.");
        if (Tracing) Note($"ReadSample #{tagId} {input.Quality}");
        return new ScriptSample(input.Value, input.Quality, input.Timestamp, input.Good && double.IsFinite(input.Value));
    }

    public IReadOnlyList<ScriptSample> History(int tagId)
    {
        if (_history is null) throw new InvalidOperationException("History snapshot was not declared for this task.");
        if (!_history.TryGetValue(tagId, out var points)) throw new InvalidOperationException($"History #{tagId} is not in the declared read list.");
        if (Tracing) Note($"History #{tagId} {points.Length}");
        return points;
    }

    public IReadOnlyList<ScriptSample> History(string name) => History(Require(name));

    public IReadOnlyList<ScriptAlarm> Alarms()
    {
        if (_alarms is null) throw new InvalidOperationException("Alarm snapshot was not declared for this task.");
        if (Tracing) Note($"Alarms {_alarms.Count}");
        return _alarms;
    }

    public ScriptSample ReadSample(string name) => ReadSample(Require(name));

    // 串口由 Worker 宿主持有：脚本只传配置档名称和命令，等待回复或超时；不暴露 System.IO。
    // 同步版本保留给既有脚本；内部走异步实现，因此仍能被执行超时取消。
    public string Serial(string profile, string command, int timeoutMs = 2000) =>
        SerialAsync(profile, command, timeoutMs).GetAwaiter().GetResult();

    public async Task<string> SerialAsync(string profile, string command, int timeoutMs = 2000, CancellationToken cancellationToken = default)
    {
        if (_serialExchange is null) throw new InvalidOperationException("Serial is not available in this host.");
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 64) throw new InvalidOperationException("Serial profile name is invalid.");
        if (string.IsNullOrEmpty(command) || command.Length > CSharpScriptLimits.MaxSerialCommandChars)
            throw new InvalidOperationException($"Serial command must contain 1 to {CSharpScriptLimits.MaxSerialCommandChars} characters.");
        var token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellation;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var reply = await _serialExchange(profile, command, Math.Clamp(timeoutMs, 50, 60_000), token).ConfigureAwait(false);
            if (reply.Length > CSharpScriptLimits.MaxSerialReplyChars)
                throw new InvalidOperationException($"Serial reply exceeds {CSharpScriptLimits.MaxSerialReplyChars} characters.");
            if (Tracing) Note($"Serial {profile} {reply.Length} chars");
            RecordSerialUse(new(profile, true, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return reply;
        }
        catch (Exception ex)
        {
            RecordSerialUse(new(profile, false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message[..Math.Min(ex.Message.Length, CSharpScriptLimits.MaxLogChars)]));
            throw;
        }
    }

    public void Output(string name, double value)
    {
        if (!_outputNames.Contains(name) || !double.IsFinite(value))
            throw new InvalidOperationException("Output must be declared with a constant name and a finite value.");
        lock (_sync) Outputs[name] = value;
        if (Tracing) Note($"Output {name} = {value}");
    }

    public void Write(int tagId, double value)
    {
        if (!_writable.Contains(tagId) || !double.IsFinite(value))
            throw new InvalidOperationException($"Write #{tagId} is not permitted or its value is not finite.");
        lock (_sync) Writes[tagId] = value;
        if (Tracing) Note($"Write #{tagId} = {value}");
    }

    public void Write(string name, double value) => Write(Require(name), value);

    private int Require(string name)
    {
        if (string.IsNullOrEmpty(name) || !_names.TryGetValue(name, out var id))
            throw new InvalidOperationException($"Tag '{name}' is not in the script whitelist.");
        return id;
    }

    private void RecordSerialUse(ScriptSerialUse use)
    {
        lock (_sync)
        {
            if (SerialUses.Count < CSharpScriptLimits.MaxSerialUses) SerialUses.Add(use);
        }
    }

    // 宿主在脚本返回后取一份快照：脚本若留下了未 await 的任务，直接读内部集合会撞上并发写。
    public ScriptResultSnapshot Snapshot()
    {
        lock (_sync)
            return new ScriptResultSnapshot(
                new Dictionary<string, double>(Outputs), new Dictionary<int, double>(Writes),
                Logs.ToArray(), Trace.ToArray(),
                SerialUses.Count == 0 ? null : SerialUses.ToArray());
    }

    public void Log(string message)
    {
        lock (_sync)
        {
            if (Logs.Count >= CSharpScriptLimits.MaxLogs) throw new InvalidOperationException($"At most {CSharpScriptLimits.MaxLogs} log messages are allowed per execution.");
            Logs.Add((message ?? "")[..Math.Min(message?.Length ?? 0, CSharpScriptLimits.MaxLogChars)]);
        }
    }
}