using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Scada.Core.Scripting;
using Scada.ScriptWorker;

var parentArgument = args.FirstOrDefault(argument => argument.StartsWith("--parent=", StringComparison.Ordinal));
if (parentArgument is not null && int.TryParse(parentArgument[9..], out var parentId))
{
    try { _ = ExitWithParentAsync(Process.GetProcessById(parentId)); }
    catch (ArgumentException) { return; }
}
WorkerLog.Open();
// stdout/stderr 都不承载协议：脚本里的 Console 输出（以及反射绕过的写入）只会流进宿主丢弃的排水口，
// 污染不了通道；诊断信息走 WorkerLog 文件和 stderr 尾部捕获。
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
AppDomain.CurrentDomain.ProcessExit += static (_, _) => SerialGateway.DisposeAll();
AppDomain.CurrentDomain.UnhandledException += static (_, e) => WorkerLog.Write("fatal", e.ExceptionObject as Exception);
var inputHandle = Argument(args, "--in=");
var outputHandle = Argument(args, "--out=");
if (inputHandle is null || outputHandle is null)
    Fatal("Scada.ScriptWorker must be started by the server: --in/--out pipe handles are missing.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var roslynVersion = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown";
var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
if (string.IsNullOrEmpty(trustedAssemblies))
    Fatal("ScriptWorker requires a framework-dependent deployment: TRUSTED_PLATFORM_ASSEMBLIES is unavailable (single-file or trimmed publish is unsupported).");
var references = trustedAssemblies!.Split(Path.PathSeparator)
    .Append(typeof(ScriptContext).Assembly.Location).Where(File.Exists).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var cache = new CompileCache(ResolveCacheDirectory());
var compiler = new ScriptCompiler(references, cache);
WorkerLog.Write("info", $"start pid={Environment.ProcessId} protocol={ScriptProtocol.Version} runtime={Environment.Version} roslyn={roslynVersion} references={references.Length} cache={cache.Directory}");
// 预热放在握手之前：宿主读握手时顺带把 Roslyn 的装配与 JIT 等掉，第一个业务请求不必再付这笔钱。
WarmupCompilation();
using var input = new AnonymousPipeClientStream(PipeDirection.In, inputHandle!);
using var output = new AnonymousPipeClientStream(PipeDirection.Out, outputHandle!);
// 第一帧必须是握手：宿主据此判断通道可用、版本是否匹配，而不是等到第一个请求超时。
await ScriptFrames.WriteAsync(output,
    JsonSerializer.SerializeToUtf8Bytes(new CSharpScriptHello(ScriptProtocol.Version, Environment.Version.ToString(), roslynVersion), json),
    CancellationToken.None);
while (true)
{
    byte[]? frame;
    try { frame = await ScriptFrames.ReadAsync(input, CancellationToken.None); }
    catch (Exception exception) when (exception is InvalidDataException or IOException or ObjectDisposedException)
    {
        // 帧长度非法说明字节流已经错位，继续读只会放大问题：记录后退出，由宿主重建 Worker。
        WorkerLog.Write("warn", $"input channel failed: {exception.Message}");
        break;
    }
    if (frame is null) break;
    var started = Stopwatch.GetTimestamp();
    var reply = await HandleAsync(frame);
    reply = reply with { ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    WorkerLog.Write(reply.Success ? "info" : "warn", $"id={reply.Id} ok={reply.Success} ms={reply.ElapsedMs:F1} code={reply.ErrorDetail?.Code ?? "-"}");
    try { await ScriptFrames.WriteAsync(output, JsonSerializer.SerializeToUtf8Bytes(reply, json), CancellationToken.None); }
    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
    {
        WorkerLog.Write("warn", $"reply channel closed: {exception.Message}");
        break;
    }
}
WorkerLog.Write("info", "channel closed, worker exits");

async Task<CSharpScriptReply> HandleAsync(byte[] frame)
{
    CSharpScriptRequest? request = null;
    var execution = new ScriptRunner.Execution();
    try
    {
        request = JsonSerializer.Deserialize<CSharpScriptRequest>(frame, json) ?? throw new WorkerRequestException(ScriptProtocol.ErrorProtocol, "Missing request.");
        if (request.ProtocolVersion != ScriptProtocol.Version)
            throw new WorkerRequestException(ScriptProtocol.ErrorProtocol,
                $"Script worker speaks protocol v{ScriptProtocol.Version}, the request carried v{request.ProtocolVersion}; redeploy server and ScriptWorker from the same package.");
        if (request.Operation == "warmup") return Warmup(request);
        if (request.Source is null || request.Source.Length is 0 or > CSharpScriptLimits.MaxSourceChars
            || request.LibrarySource?.Length > CSharpScriptLimits.MaxLibraryChars
            || request.Inputs?.Count > CSharpScriptLimits.MaxInputs || request.WritableTags?.Length > CSharpScriptLimits.MaxWritableTags)
            throw new WorkerRequestException(ScriptProtocol.ErrorLimits, "Script or input limits exceeded.");
        if (request.Operation is not ("compile" or "run"))
            throw new WorkerRequestException(ScriptProtocol.ErrorProtocol, "Unknown operation.");
        var compiled = compiler.Compile(request.Source, request.LibrarySource);
        if (compiled.Script is null)
            return new(request.Id, false, [], new(), new(), compiled.Diagnostics.Messages.ToArray(),
                Spans: compiled.Diagnostics.Spans.ToArray(), ErrorDetail: new ScriptError(ScriptProtocol.ErrorCompile, "Compilation failed."));
        if (request.Operation == "compile")
            return new(request.Id, true, compiled.Script.Outputs, new(), new(), []);
        return await ScriptRunner.RunAsync(request, compiled.Script,
            (profile, command, timeoutMs, token) => SerialGateway.ExchangeAsync(request!.SerialProfiles, profile, command, timeoutMs, token),
            execution);
    }
    catch (Exception exception)
    {
        var detail = ScriptRunner.Describe(exception);
        WorkerLog.Write("warn", $"request failed: {detail.Code} {detail.Message}");
        return new(request?.Id ?? "", false, [], new(), new(), [], detail.Message,
            Spans: detail.File is not null && detail.Line is { } errorLine ? new[] { ScriptRunner.FailureSpan(detail, errorLine) } : null,
            SerialUses: execution.Context?.Snapshot().SerialUses,
            ErrorDetail: detail);
    }
}

// 预热：把 Roslyn 的装配、解析、发射与 JIT 提前到 Worker 启动阶段，
// 避免规则里的第一次真实执行既撞冷启动又撞执行超时。
CSharpScriptReply Warmup(CSharpScriptRequest request)
{
    WarmupCompilation();
    return new(request.Id, true, [], new(), new(), []);
}

// 幂等：重复调用只是再走一遍已经热起来的编译管线，成本可以忽略。
void WarmupCompilation()
{
    var tree = CSharpSyntaxTree.ParseText("using System;\ninternal static class Warm { public static int Run() => 1; }", new CSharpParseOptions(LanguageVersion.CSharp14));
    var compilation = CSharpCompilation.Create("warmup", [tree], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
    using var sink = new MemoryStream();
    compilation.Emit(sink);
}

static string ResolveCacheDirectory()
{
    var configured = Environment.GetEnvironmentVariable("SCADA_SCRIPT_CACHE_DIR");
    return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "scada-script-cache") : configured;
}

static string? Argument(string[] values, string prefix)
{
    var value = values.FirstOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal));
    return value?[prefix.Length..];
}

static void Fatal(string message)
{
    WorkerLog.Write("fatal", message);
    try
    {
        using var stderr = Console.OpenStandardError();
        var bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);
        stderr.Write(bytes, 0, bytes.Length);
        stderr.Flush();
    }
    catch (IOException) { }
    Environment.Exit(2);
}

static async Task ExitWithParentAsync(Process parent)
{
    using (parent) await parent.WaitForExitAsync();
    Environment.Exit(0);
}
