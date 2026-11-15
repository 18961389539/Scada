using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Scripting;

namespace Scada.Engine.Calculations;

public sealed class CSharpScriptOptions
{
    public bool Enabled { get; set; }
    public string WorkerPath { get; set; } = "";
    public string DotnetPath { get; set; } = "dotnet";
    public int WorkerCount { get; set; } = 2;
    // 冷启动（dotnet 启动 + Roslyn 加载 + JIT）通常在 300-800 ms；默认值必须留出余量，
    // 否则规则里的第一次执行会以"超时"失败而不是"脚本有问题"。
    public int TimeoutMs { get; set; } = 3000;
    public int CompileTimeoutMs { get; set; } = 15_000;
    public int MemoryLimitMb { get; set; } = 512;
    // 编译产物磁盘缓存目录；空值落到 %TEMP%/scada-script-cache。
    public string CacheDirectory { get; set; } = "";
    // 串口配置档由 Worker 宿主持有，脚本只按名称引用，见 context.Serial。
    public Dictionary<string, SerialProfile> SerialProfiles { get; set; } = new();
}

public sealed class CSharpScriptException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface ICSharpScriptRunner
{
    bool Enabled { get; }
    Task<CSharpScriptReply> CompileAsync(string source, string? librarySource = null, CancellationToken ct = default);
    Task<CSharpScriptReply> RunAsync(CSharpScriptRequest request, CancellationToken ct = default);
}

public sealed class CSharpScriptHost : ICSharpScriptRunner, IDisposable
{
    private sealed class Slot
    {
        public Process? Process;
        public IDisposable? Limits;
        public string? Directory;
        public string? Source;
        public string[] OutputNames = [];
        public int Executions;
        // 协议通道：宿主写请求、读回复，各占一条匿名管道；stdout 只用来排掉脚本杂散输出。
        public AnonymousPipeServerStream? ToWorker;
        public AnonymousPipeServerStream? FromWorker;
        // Worker 的 stderr 尾部：初始化失败（缺依赖、runtimeconfig 不匹配）时这是唯一线索。
        public readonly StringBuilder Stderr = new();
        public readonly object StderrSync = new();
    }

    private readonly CSharpScriptOptions _options;
    private readonly ILogger<CSharpScriptHost>? _logger;
    private readonly ISerialProfileCatalog? _catalog;
    private readonly SerialProfileTelemetry? _telemetry;
    private readonly Dictionary<string, SerialProfile> _serialProfiles;
    private readonly SemaphoreSlim _capacity;
    private readonly ConcurrentQueue<Slot> _available = new();
    private readonly Slot[] _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool _disposed;
    public bool Enabled => _options.Enabled;

    public CSharpScriptHost(IOptions<CSharpScriptOptions> options, ILogger<CSharpScriptHost>? logger = null,
        ISerialProfileCatalog? catalog = null, SerialProfileTelemetry? telemetry = null)
    {
        _options = options.Value;
        _logger = logger;
        _catalog = catalog;
        _telemetry = telemetry;
        _serialProfiles = new(_options.SerialProfiles ?? new(), StringComparer.OrdinalIgnoreCase);
        ValidateSerialProfiles();
        // 串口由单个 Worker 进程独占，多个 Worker 会抢同一个 COM 口。
        var requested = Math.Clamp(_options.WorkerCount, 1, 4);
        var count = _serialProfiles.Count > 0 ? 1 : requested;
        if (count != requested) logger?.LogWarning("C# script WorkerCount is clamped to 1 because serial profiles are configured.");
        _capacity = new(count, count);
        _slots = Enumerable.Range(0, count).Select(_ => new Slot()).ToArray();
        foreach (var slot in _slots) _available.Enqueue(slot);
        SweepWorkerLogs();
        SweepWorkerDirectories();
    }

    // 进程被强杀时 Stop 来不及删工作目录，临时目录会慢慢堆积；启动时清理一天前的孤儿。
    // 只认 scada-script-<32 hex>，别把日志目录和编译缓存目录一起删了。
    private static void SweepWorkerDirectories()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var directory in System.IO.Directory.EnumerateDirectories(Path.GetTempPath(), "scada-script-*"))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith("scada-script-", StringComparison.Ordinal)) continue;
                if (!Guid.TryParseExact(name["scada-script-".Length..], "N", out _)) continue;
                var age = now - System.IO.Directory.GetLastWriteTimeUtc(directory);
                // 一天内的目录可能属于另一个实例正在跑的 Worker，别碰。
                if (age < TimeSpan.FromDays(1)) continue;
                // pid 复用可能让真正的孤儿看起来"活着"，七天后无条件清理。
                if (age < TimeSpan.FromDays(7) && OwnerAlive(directory)) continue;
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool OwnerAlive(string directory)
    {
        try
        {
            var marker = Path.Combine(directory, "worker.pid");
            if (!System.IO.File.Exists(marker)) return false;
            if (!int.TryParse(System.IO.File.ReadAllText(marker).Trim(), out var pid) || pid <= 0) return false;
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Worker 日志落在临时目录，进程回收不会带走它；只保留最近 7 天，避免无人清理的堆积。
    private static void SweepWorkerLogs()
    {
        try
        {
            var root = WorkerLogDirectory;
            if (!System.IO.Directory.Exists(root)) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "worker-*.log"))
                if (System.IO.File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) System.IO.File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal static string WorkerLogDirectory => Path.Combine(Path.GetTempPath(), "scada-script-logs");

    private string CacheDirectory => string.IsNullOrWhiteSpace(_options.CacheDirectory)
        ? Path.Combine(Path.GetTempPath(), "scada-script-cache")
        : Path.GetFullPath(_options.CacheDirectory);

    private void ValidateSerialProfiles()
    {
        // 与启动期校验共用同一份规则：直接构造 Host（测试、工具）时仍然在这里快速失败。
        var errors = CSharpScriptOptionsValidation.Validate(_options);
        if (errors.Count > 0) throw new CSharpScriptException("config", errors[0]);
    }

    public Task<CSharpScriptReply> CompileAsync(string source, string? librarySource = null, CancellationToken ct = default) =>
        ExecuteAsync(new("", "compile", source, LibrarySource: librarySource), ct);

    public Task<CSharpScriptReply> RunAsync(CSharpScriptRequest request, CancellationToken ct = default) =>
        ExecuteAsync(request with { Operation = "run" }, ct);

    private async Task<CSharpScriptReply> ExecuteAsync(CSharpScriptRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enabled) throw new CSharpScriptException("disabled", "C# scripts are disabled. Configure Scada:CSharpScripts:Enabled after reviewing worker isolation.");
        // 与 Worker 使用同一组常量，避免"宿主少校验一项"这类漂移。
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > CSharpScriptLimits.MaxSourceChars
            || request.LibrarySource?.Length > CSharpScriptLimits.MaxLibraryChars
            || request.Inputs?.Count > CSharpScriptLimits.MaxInputs || request.WritableTags?.Length > CSharpScriptLimits.MaxWritableTags)
            throw new CSharpScriptException("limits", "Script or input limits exceeded.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        if (!await _capacity.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token))
            throw new CSharpScriptException("busy", "All C# script workers are busy.");
        if (!_available.TryDequeue(out var slot)) { _capacity.Release(); throw new CSharpScriptException("busy", "No script worker is available."); }
        try
        {
            if (slot.Process is null || slot.Process.HasExited || slot.Executions >= 200) { Stop(slot); await StartAsync(slot, lifetime.Token); }
            var cacheKey = string.IsNullOrEmpty(request.LibrarySource) ? request.Source : request.LibrarySource + "\0" + request.Source;
            if (request.Operation == "run" && slot.Source != cacheKey)
            {
                var compiled = await ExchangeAsync(slot, new("", "compile", request.Source, LibrarySource: request.LibrarySource), _options.CompileTimeoutMs, lifetime.Token);
                if (!compiled.Success) return compiled;
                slot.Source = cacheKey; slot.OutputNames = compiled.OutputNames;
            }
            var reply = await ExchangeAsync(slot, request, request.Operation == "compile" ? _options.CompileTimeoutMs : _options.TimeoutMs, lifetime.Token);
            if (request.Operation == "compile" && reply.Success) { slot.Source = cacheKey; slot.OutputNames = reply.OutputNames; }
            if (request.Operation == "run" && reply.Success && (!slot.OutputNames.ToHashSet(StringComparer.Ordinal).SetEquals(reply.OutputNames)
                || reply.Outputs.Keys.Any(name => !slot.OutputNames.Contains(name, StringComparer.Ordinal))))
                throw new CSharpScriptException("protocol", "Script output does not match the compiled manifest.");
            slot.Executions++;
            return reply;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { Stop(slot); throw new CSharpScriptException("unavailable", "Cannot start or isolate the C# worker. Review executable permissions and operating system resource limits."); }
        catch { Stop(slot); throw; }
        finally { _available.Enqueue(slot); _capacity.Release(); }
    }

    private async Task StartAsync(Slot slot, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(_options.WorkerPath)
            ? Path.Combine(AppContext.BaseDirectory, "ScriptWorker", "Scada.ScriptWorker.dll") : Path.GetFullPath(_options.WorkerPath);
        if (!File.Exists(path)) throw new CSharpScriptException("unavailable", "C# script worker is missing. Build or publish the complete server package.");
        var directory = Path.Combine(Path.GetTempPath(), "scada-script-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        slot.Directory = directory;
        // 协议走专用管道，stdout 不参与：脚本里任何写标准输出的行为都污染不了通道。
        var toWorker = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromWorker = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(_options.DotnetPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = directory, StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("--parent=" + Environment.ProcessId);
        start.ArgumentList.Add("--in=" + toWorker.GetClientHandleAsString());
        start.ArgumentList.Add("--out=" + fromWorker.GetClientHandleAsString());
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["TEMP"] = directory; start.Environment["TMP"] = directory; start.Environment["HOME"] = directory;
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["DOTNET_GCHeapHardLimit"] = ((long)Math.Clamp(_options.MemoryLimitMb, 128, 2048) * 1024 * 1024).ToString("x");
        start.Environment["SCADA_SCRIPT_LOG_DIR"] = WorkerLogDirectory;
        start.Environment["SCADA_SCRIPT_CACHE_DIR"] = CacheDirectory;
        try
        {
            slot.Process = Process.Start(start) ?? throw new CSharpScriptException("unavailable", "Cannot start script worker.");
            try { System.IO.File.WriteAllText(Path.Combine(directory, "worker.pid"), slot.Process.Id.ToString()); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        catch
        {
            toWorker.Dispose(); fromWorker.Dispose();
            throw;
        }
        // 关掉本进程里的客户端句柄副本，否则 Worker 退出时宿主永远读不到 EOF。
        toWorker.DisposeLocalCopyOfClientHandle();
        fromWorker.DisposeLocalCopyOfClientHandle();
        slot.ToWorker = toWorker; slot.FromWorker = fromWorker;
        if (OperatingSystem.IsWindows()) slot.Limits = ScriptWorkerJob.Attach(slot.Process, Math.Clamp(_options.MemoryLimitMb, 128, 2048));
        // 这两个后台泵随 Worker 全程运行，启动时不能 await；异常若不观察会变成"进程静默少一个 Worker"。
        _ = ObserveAsync(DrainAsync(slot.Process.StandardOutput), "stdout");
        _ = ObserveAsync(CaptureStderrAsync(slot, slot.Process), "stderr");
        // Worker 在发握手前已经做完 Roslyn 预热，这里读到 hello 就说明它真的可用，
        // 首个业务请求不必再承担冷启动（以前预热在请求路径里，最坏会多等十几秒）。
        await ReadHelloAsync(slot, ct);
    }

    // 后台泵抛出的非预期异常必须落日志：否则只会看到 Worker 莫名退出/卡住，没有可排查的线索。
    private async Task ObserveAsync(Task pump, string stream)
    {
        try { await pump; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _logger?.LogWarning(exception, "C# 脚本 Worker {Stream} 读取循环异常", stream); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer.AsMemory()) != 0) { } }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    // Worker 启动即发握手帧。先确认版本再发第一个业务请求：升级只替换一半文件时，
    // 这里会给出"协议不匹配"，而不是等到超时后报 "invalid response"。
    private static async Task ReadHelloAsync(Slot slot, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Worker 在发握手前要做完 Roslyn 预热：并行启动多个 Worker 时冷启动会到 2-3 秒，
        // 这里留足 30 秒只影响启动，不占用脚本自己的执行预算。
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        byte[]? frame;
        try
        {
            frame = await ScriptFrames.ReadAsync(slot.FromWorker!, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new CSharpScriptException("unavailable", "Script worker did not report its protocol version within 30 seconds." + Describe(slot));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
        {
            throw new CSharpScriptException("unavailable", "Script worker channel failed during the handshake." + Describe(slot));
        }
        if (frame is null)
            throw new CSharpScriptException("unavailable", "Script worker exited before the protocol handshake." + Describe(slot));
        CSharpScriptHello? hello = null;
        try { hello = JsonSerializer.Deserialize<CSharpScriptHello>(frame, Json); }
        catch (JsonException) { }
        if (ScriptHandshake.Validate(hello, ScriptProtocol.Version) is { } handshake)
            throw new CSharpScriptException("protocol", handshake + Describe(slot));
    }

    private static async Task CaptureStderrAsync(Slot slot, Process process)
    {
        var buffer = new char[512];
        try
        {
            while (true)
            {
                var read = await process.StandardError.ReadAsync(buffer.AsMemory());
                if (read == 0) return;
                lock (slot.StderrSync)
                {
                    slot.Stderr.Append(buffer, 0, read);
                    if (slot.Stderr.Length > 2048) slot.Stderr.Remove(0, slot.Stderr.Length - 2048);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static string Describe(Slot slot)
    {
        lock (slot.StderrSync)
        {
            if (slot.Stderr.Length == 0) return "";
            var text = slot.Stderr.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 512) text = text[^512..];
            return " Worker stderr: " + text;
        }
    }

    private async Task<CSharpScriptReply> ExchangeAsync(Slot slot, CSharpScriptRequest request, int timeoutMs, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Math.Clamp(timeoutMs, 50, 60_000));
        var id = Guid.NewGuid().ToString("N");
        IReadOnlyDictionary<string, SerialProfile>? profiles = _serialProfiles.Count == 0 ? null : _serialProfiles;
        if (_catalog is not null) profiles = (await _catalog.GetAsync(ct)).Profiles;
        if (profiles is { Count: > 0 } && _slots.Length > 1)
            throw new CSharpScriptException("config", "Serial profiles require Scada:CSharpScripts:WorkerCount=1; change the setting and restart.");
        request = request with
        {
            Id = id,
            ProtocolVersion = ScriptProtocol.Version,
            TimeoutMs = Math.Clamp(timeoutMs, 50, 60_000),
            SerialProfiles = profiles is { Count: > 0 } ? new Dictionary<string, SerialProfile>(profiles, StringComparer.OrdinalIgnoreCase) : null
        };
        try
        {
            if (slot.ToWorker is null || slot.FromWorker is null)
                throw new CSharpScriptException("unavailable", "Script worker channel is not available.");
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            if (payload.Length > CSharpScriptLimits.MaxFrameBytes) throw new CSharpScriptException("limits", "Script request is too large.");
            await ScriptFrames.WriteAsync(slot.ToWorker, payload, deadline.Token);
            var replyBytes = await ScriptFrames.ReadAsync(slot.FromWorker, deadline.Token);
            if (replyBytes is null) throw new IOException("Script worker closed the reply channel.");
            var reply = JsonSerializer.Deserialize<CSharpScriptReply>(replyBytes, Json);
            if (reply is null || reply.Id != id || reply.OutputNames is null || reply.OutputNames.Length > CSharpScriptLimits.MaxOutputs
                || !ValidOutputNames(reply.OutputNames)
                || reply.Trace?.Length > CSharpScriptLimits.MaxTrace
                || reply.Trace?.Any(item => item is null || item.Length > CSharpScriptLimits.MaxLogChars) == true
                || reply.Outputs is null || reply.Outputs.Count > CSharpScriptLimits.MaxOutputs || reply.Writes is null || reply.Writes.Count > CSharpScriptLimits.MaxWritableTags
                || reply.Diagnostics is null || reply.Diagnostics.Length > CSharpScriptLimits.MaxDiagnostics || !ValidSpans(reply.Spans) || reply.Logs?.Length > CSharpScriptLimits.MaxLogs
                || !double.IsFinite(reply.ElapsedMs) || reply.ElapsedMs < 0 || reply.Logs?.Any(message => message is null || message.Length > CSharpScriptLimits.MaxLogChars) == true
                || !reply.Success && (reply.Outputs.Count > 0 || reply.Writes.Count > 0)
                || reply.Outputs.Any(item => !reply.OutputNames.Contains(item.Key, StringComparer.Ordinal) || !double.IsFinite(item.Value))
                || reply.Writes.Any(item => !(request.WritableTags ?? []).Contains(item.Key) || !double.IsFinite(item.Value))
                || reply.SerialUses?.Length > CSharpScriptLimits.MaxSerialUses
                || reply.SerialUses?.Any(use => use is null || string.IsNullOrWhiteSpace(use.Profile) || use.Profile.Length > 64
                    || !double.IsFinite(use.ElapsedMs) || use.ElapsedMs < 0 || use.Error is { Length: > CSharpScriptLimits.MaxLogChars }) == true
                || reply.Error is { Length: > CSharpScriptLimits.MaxLogChars } || !ValidDetail(reply.ErrorDetail))
                throw new CSharpScriptException("protocol", "Invalid script worker response.");
            _telemetry?.Record(reply.SerialUses);
            return reply;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CSharpScriptException("timeout", "C# script exceeded its execution deadline; its worker was terminated." + Describe(slot)); }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or InvalidDataException or ObjectDisposedException)
        { throw new CSharpScriptException("worker_failed", "C# script worker exited or returned an invalid response." + Describe(slot)); }
    }

    // 输出名同时约束长度、首字符与字符集：这些名字会直接进前端展示与响应 JSON，
    // 不能只信 Worker 侧编译期的校验（磁盘缓存文件被改动时那条路径可绕过）。
    private static bool ValidOutputNames(string[] names) =>
        names.Distinct(StringComparer.Ordinal).Count() == names.Length
        && names.All(name => name is { Length: > 0 and <= 32 } && char.IsAsciiLetter(name[0])
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));

    private static bool ValidDetail(ScriptError? detail) => detail is null
        || (detail.Code is ScriptProtocol.ErrorScriptThrew or ScriptProtocol.ErrorCanceled or ScriptProtocol.ErrorCompile or ScriptProtocol.ErrorLimits or ScriptProtocol.ErrorProtocol
            && detail.Message is { Length: > 0 and <= 1024 }
            && detail.File is null or "script.cs" or "library.cs"
            && detail.Line is null or (>= 1 and <= 20_000)
            && detail.Column is null or (>= 1 and <= 20_000)
            && (detail.Line is not null || detail.Column is null));

    private static bool ValidSpans(ScriptDiagnostic[]? spans) => spans is null || spans.Length <= 20 && spans.All(span =>
        span.Message is { Length: > 0 and <= 4000 } && span.File is "script.cs" or "library.cs"
        && span.StartLine is >= 1 and <= 20000 && span.EndLine is >= 1 and <= 20000 && span.EndLine >= span.StartLine
        && span.StartColumn is >= 1 and <= 20000 && span.EndColumn is >= 1 and <= 20000
        && (span.EndLine > span.StartLine || span.EndColumn > span.StartColumn));

    private static void Stop(Slot slot)
    {
        slot.ToWorker?.Dispose(); slot.ToWorker = null;
        slot.FromWorker?.Dispose(); slot.FromWorker = null;
        if (slot.Process is { } process)
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose(); slot.Process = null;
        }
        slot.Limits?.Dispose(); slot.Limits = null; slot.Source = null; slot.OutputNames = []; slot.Executions = 0;
        lock (slot.StderrSync) slot.Stderr.Clear();
        if (slot.Directory is { } directory)
        { try { System.IO.Directory.Delete(directory, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } slot.Directory = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _shutdown.Cancel();
        foreach (var slot in _slots) Stop(slot);
    }
}