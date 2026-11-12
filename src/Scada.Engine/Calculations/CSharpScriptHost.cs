using System.Collections.Concurrent;
using System.Diagnostics;
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
    public int TimeoutMs { get; set; } = 1000;
    public int CompileTimeoutMs { get; set; } = 15_000;
    public int MemoryLimitMb { get; set; } = 512;
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
    }

    private void ValidateSerialProfiles()
    {
        foreach (var (name, profile) in _serialProfiles)
            if (SerialProfileRules.Validate(name, profile) is { } error) throw new CSharpScriptException("config", error);
        if (_serialProfiles.Count > 0 && _options.TimeoutMs <= _serialProfiles.Values.Max(profile => profile.ReadTimeoutMs) + 250)
            throw new CSharpScriptException("config", "Scada:CSharpScripts:TimeoutMs must exceed every serial profile ReadTimeoutMs by at least 250 ms.");
    }

    public Task<CSharpScriptReply> CompileAsync(string source, string? librarySource = null, CancellationToken ct = default) =>
        ExecuteAsync(new("", "compile", source, LibrarySource: librarySource), ct);

    public Task<CSharpScriptReply> RunAsync(CSharpScriptRequest request, CancellationToken ct = default) =>
        ExecuteAsync(request with { Operation = "run" }, ct);

    private async Task<CSharpScriptReply> ExecuteAsync(CSharpScriptRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enabled) throw new CSharpScriptException("disabled", "C# scripts are disabled. Configure Scada:CSharpScripts:Enabled after reviewing worker isolation.");
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 16_384 || request.Inputs?.Count > 64 || request.WritableTags?.Length > 16)
            throw new CSharpScriptException("limits", "Script or input limits exceeded.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        if (!await _capacity.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token))
            throw new CSharpScriptException("busy", "All C# script workers are busy.");
        if (!_available.TryDequeue(out var slot)) { _capacity.Release(); throw new CSharpScriptException("busy", "No script worker is available."); }
        try
        {
            if (slot.Process is null || slot.Process.HasExited || slot.Executions >= 50) { Stop(slot); Start(slot); }
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

    private void Start(Slot slot)
    {
        var path = string.IsNullOrWhiteSpace(_options.WorkerPath)
            ? Path.Combine(AppContext.BaseDirectory, "ScriptWorker", "Scada.ScriptWorker.dll") : Path.GetFullPath(_options.WorkerPath);
        if (!File.Exists(path)) throw new CSharpScriptException("unavailable", "C# script worker is missing. Build or publish the complete server package.");
        var directory = Path.Combine(Path.GetTempPath(), "scada-script-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        slot.Directory = directory;
        var start = new ProcessStartInfo(_options.DotnetPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = directory, StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("--parent=" + Environment.ProcessId);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["TEMP"] = directory; start.Environment["TMP"] = directory; start.Environment["HOME"] = directory;
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["DOTNET_GCHeapHardLimit"] = ((long)Math.Clamp(_options.MemoryLimitMb, 128, 2048) * 1024 * 1024).ToString("x");
        slot.Process = Process.Start(start) ?? throw new CSharpScriptException("unavailable", "Cannot start script worker.");
        if (OperatingSystem.IsWindows()) slot.Limits = ScriptWorkerJob.Attach(slot.Process, Math.Clamp(_options.MemoryLimitMb, 128, 2048));
        _ = slot.Process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }

    private async Task<CSharpScriptReply> ExchangeAsync(Slot slot, CSharpScriptRequest request, int timeoutMs, CancellationToken ct)
    {
        var process = slot.Process!;
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
            TimeoutMs = Math.Clamp(timeoutMs, 50, 60_000),
            SerialProfiles = profiles is { Count: > 0 } ? new Dictionary<string, SerialProfile>(profiles, StringComparer.OrdinalIgnoreCase) : null
        };
        try
        {
            var payload = JsonSerializer.Serialize(request, Json);
            if (payload.Length > 131_072) throw new CSharpScriptException("limits", "Script request is too large.");
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            var text = new StringBuilder();
            var buffer = new char[1];
            while (await process.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token) != 0)
            {
                if (buffer[0] == '\n') break;
                if (text.Length >= 131_072) throw new CSharpScriptException("protocol", "Script response is too large.");
                text.Append(buffer[0]);
            }
            var reply = JsonSerializer.Deserialize<CSharpScriptReply>(text.ToString(), Json);
            if (reply is null || reply.Id != id || reply.OutputNames is null || reply.OutputNames.Length > 32
                || reply.Outputs is null || reply.Outputs.Count > 32 || reply.Writes is null || reply.Writes.Count > 16
                || reply.Diagnostics is null || reply.Diagnostics.Length > 20 || !ValidSpans(reply.Spans) || reply.Logs?.Length > 16
                || !double.IsFinite(reply.ElapsedMs) || reply.ElapsedMs < 0 || reply.Logs?.Any(message => message is null || message.Length > 1024) == true
                || !reply.Success && (reply.Outputs.Count > 0 || reply.Writes.Count > 0)
                || reply.Outputs.Any(item => !reply.OutputNames.Contains(item.Key, StringComparer.Ordinal) || !double.IsFinite(item.Value))
                || reply.Writes.Any(item => !(request.WritableTags ?? []).Contains(item.Key) || !double.IsFinite(item.Value))
                || reply.SerialUses?.Length > 32
                || reply.SerialUses?.Any(use => use is null || string.IsNullOrWhiteSpace(use.Profile) || use.Profile.Length > 64
                    || !double.IsFinite(use.ElapsedMs) || use.ElapsedMs < 0 || use.Error is { Length: > 1024 }) == true)
                throw new CSharpScriptException("protocol", "Invalid script worker response.");
            _telemetry?.Record(reply.SerialUses);
            return reply;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CSharpScriptException("timeout", "C# script exceeded its execution deadline; its worker was terminated."); }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        { throw new CSharpScriptException("worker_failed", "C# script worker exited or returned an invalid response."); }
    }

    private static bool ValidSpans(ScriptDiagnostic[]? spans) => spans is null || spans.Length <= 20 && spans.All(span =>
        span.Message is { Length: > 0 and <= 4000 } && span.File is "script.cs" or "library.cs"
        && span.StartLine is >= 1 and <= 20000 && span.EndLine is >= 1 and <= 20000 && span.EndLine >= span.StartLine
        && span.StartColumn is >= 1 and <= 20000 && span.EndColumn is >= 1 and <= 20000
        && (span.EndLine > span.StartLine || span.EndColumn > span.StartColumn));

    private static void Stop(Slot slot)
    {
        if (slot.Process is { } process)
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose(); slot.Process = null;
        }
        slot.Limits?.Dispose(); slot.Limits = null; slot.Source = null; slot.OutputNames = []; slot.Executions = 0;
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