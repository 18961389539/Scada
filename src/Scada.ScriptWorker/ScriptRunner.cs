using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Scada.Core.Scripting;

namespace Scada.ScriptWorker;

/// <summary>执行侧：装载程序集、跑脚本、把异常翻译成带位置的错误。</summary>
internal static class ScriptRunner
{
    public sealed class Execution
    {
        public ScriptContext? Context { get; set; }
    }

    public static async Task<CSharpScriptReply> RunAsync(CSharpScriptRequest request, CompiledScript compiled,
        Func<string, string, int, CancellationToken, Task<string>> serial, Execution execution)
    {
        var loader = new AssemblyLoadContext("script-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var image = new MemoryStream(compiled.Image);
            using var pdb = new MemoryStream(compiled.Pdb);
            var assembly = loader.LoadFromStream(image, pdb);
            var script = (IScadaScript)Activator.CreateInstance(assembly.GetType("Scada.Generated.Script", throwOnError: true)!)!;
            using var timeout = new CancellationTokenSource();
            if (request.TimeoutMs > 0) timeout.CancelAfter(request.TimeoutMs);
            // 串口等待也观察这个令牌：脚本超时不再依赖宿主杀进程，未完成的读会被取消并回滚端口。
            var context = new ScriptContext(request, compiled.Outputs, serial, timeout.Token);
            execution.Context = context;
            await script.ExecuteAsync(context, timeout.Token);
            var snapshot = context.Snapshot();
            if (snapshot.Outputs.Count > CSharpScriptLimits.MaxOutputs || snapshot.Writes.Count > CSharpScriptLimits.MaxWritableTags || snapshot.Logs.Length > CSharpScriptLimits.MaxLogs)
                throw new WorkerRequestException(ScriptProtocol.ErrorLimits, "Result limits exceeded.");
            return new(request.Id, true, compiled.Outputs,
                new Dictionary<string, double>(snapshot.Outputs), new Dictionary<int, double>(snapshot.Writes), [],
                Logs: snapshot.Logs, Trace: snapshot.Trace, SerialUses: snapshot.SerialUses);
        }
        finally { loader.Unload(); }
    }

    public static ScriptError Describe(Exception exception)
    {
        if (exception is WorkerRequestException requestError) return new(requestError.Code, Truncate(requestError.Message));
        var inner = exception is TargetInvocationException { InnerException: { } nested } ? nested : exception;
        foreach (var frame in new StackTrace(inner, fNeedFileInfo: true).GetFrames() ?? [])
        {
            var name = frame.GetFileName() is { } file ? Path.GetFileName(file) : null;
            if (name is not ("script.cs" or "library.cs")) continue;
            var line = frame.GetFileLineNumber();
            if (line is <= 0 or > 20_000) continue;
            var column = Math.Clamp(frame.GetFileColumnNumber(), 1, 20_000);
            return new ScriptError(ScriptProtocol.ErrorScriptThrew, $"{name}({line},{column}): {inner.GetType().Name}: {Truncate(inner.Message)}", name, line, column);
        }
        return new ScriptError(inner is OperationCanceledException ? ScriptProtocol.ErrorCanceled : ScriptProtocol.ErrorScriptThrew,
            $"{inner.GetType().Name}: {Truncate(inner.Message)}");
    }

    // 失败也回一个 span：前端点击诊断就能跳到出错行，和编译期诊断的体验一致。
    public static ScriptDiagnostic FailureSpan(ScriptError detail, int line)
    {
        var column = Math.Clamp(detail.Column ?? 1, 1, 20_000);
        return new ScriptDiagnostic(detail.Message, detail.File!, Math.Clamp(line, 1, 20_000), column, Math.Clamp(line, 1, 20_000), column + 1);
    }

    private static string Truncate(string text) => text.Length <= CSharpScriptLimits.MaxLogChars ? text : text[..CSharpScriptLimits.MaxLogChars];
}
