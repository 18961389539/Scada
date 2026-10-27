using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
}

public sealed class CSharpScriptException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface ICSharpScriptRunner
{
    bool Enabled { get; }
    Task<CSharpScriptReply> CompileAsync(string source, CancellationToken ct = default);
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
    private readonly SemaphoreSlim _capacity;
    private readonly ConcurrentQueue<Slot> _available = new();
    private readonly Slot[] _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool _disposed;
    public bool Enabled => _options.Enabled;

    public CSharpScriptHost(IOptions<CSharpScriptOptions> options)
    {
        _options = options.Value;
        var count = Math.Clamp(_options.WorkerCount, 1, 4);
        _capacity = new(count, count);
        _slots = Enumerable.Range(0, count).Select(_ => new Slot()).ToArray();
        foreach (var slot in _slots) _available.Enqueue(slot);
    }

    public Task<CSharpScriptReply> CompileAsync(string source, CancellationToken ct = default) =>
        ExecuteAsync(new("", "compile", source), ct);

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
            if (request.Operation == "run" && slot.Source != request.Source)
            {
                var compiled = await ExchangeAsync(slot, new("", "compile", request.Source), _options.CompileTimeoutMs, lifetime.Token);
                if (!compiled.Success) return compiled;
                slot.Source = request.Source; slot.OutputNames = compiled.OutputNames;
            }
            var reply = await ExchangeAsync(slot, request, request.Operation == "compile" ? _options.CompileTimeoutMs : _options.TimeoutMs, lifetime.Token);
            if (request.Operation == "compile" && reply.Success) { slot.Source = request.Source; slot.OutputNames = reply.OutputNames; }
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
        request = request with { Id = id };
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
                || reply.Diagnostics is null || reply.Diagnostics.Length > 20 || reply.Logs?.Length > 16
                || !double.IsFinite(reply.ElapsedMs) || reply.ElapsedMs < 0 || reply.Logs?.Any(message => message is null || message.Length > 1024) == true
                || !reply.Success && (reply.Outputs.Count > 0 || reply.Writes.Count > 0)
                || reply.Outputs.Any(item => !reply.OutputNames.Contains(item.Key, StringComparer.Ordinal) || !double.IsFinite(item.Value))
                || reply.Writes.Any(item => !(request.WritableTags ?? []).Contains(item.Key) || !double.IsFinite(item.Value)))
                throw new CSharpScriptException("protocol", "Invalid script worker response.");
            return reply;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CSharpScriptException("timeout", "C# script exceeded its execution deadline; its worker was terminated."); }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        { throw new CSharpScriptException("worker_failed", "C# script worker exited or returned an invalid response."); }
    }

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