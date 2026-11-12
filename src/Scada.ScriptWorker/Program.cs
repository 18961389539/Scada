using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Scada.Core.Scripting;
using Scada.ScriptWorker;

var parentArgument = args.FirstOrDefault(argument => argument.StartsWith("--parent=", StringComparison.Ordinal));
if (parentArgument is not null && int.TryParse(parentArgument[9..], out var parentId))
{
    try { _ = ExitWithParentAsync(Process.GetProcessById(parentId)); }
    catch (ArgumentException) { return; }
}
var protocol = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
AppDomain.CurrentDomain.ProcessExit += static (_, _) => SerialGateway.DisposeAll();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator)
    .Append(typeof(ScriptContext).Assembly.Location).Where(File.Exists).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var cache = new Dictionary<string, (byte[] Image, string[] Outputs)>();
while (await Console.In.ReadLineAsync() is { } line)
{
    if (line.Length > 131_072) break;
    CSharpScriptRequest? request = null;
    ScriptContext? lastContext = null;
    CSharpScriptReply reply;
    var started = Stopwatch.GetTimestamp();
    try
    {
        request = JsonSerializer.Deserialize<CSharpScriptRequest>(line, json) ?? throw new InvalidOperationException("Missing request.");
        if (request.Source is null || request.Source.Length is 0 or > 16_384 || request.LibrarySource?.Length > 16_384 || request.Inputs?.Count > 64 || request.WritableTags?.Length > 16)
            throw new InvalidOperationException("Script or input limits exceeded.");
        if (request.Operation is not ("compile" or "run")) throw new InvalidOperationException("Unknown operation.");
        var cacheKey = string.IsNullOrEmpty(request.LibrarySource) ? request.Source : request.LibrarySource + "\0" + request.Source;
        if (!cache.TryGetValue(cacheKey, out var compiled))
        {
            var body = string.IsNullOrEmpty(request.LibrarySource)
                ? "#line 1 \"script.cs\"\n" + request.Source
                : "#line 1 \"library.cs\"\n" + request.LibrarySource + "\n#line 1 \"script.cs\"\n" + request.Source;
            var source = """
                using System;
                using System.Linq;
                using System.Collections.Generic;
                using System.Globalization;
                using System.Text;
                using System.Text.Json;
                using System.Threading;
                using System.Threading.Tasks;
                using Scada.Core.Scripting;
                namespace Scada.Generated;
                public sealed class Script : IScadaScript
                {
                    public async Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken)
                    {
                """ + "\n" + body + "\n#line default\n}}";
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
            var compilation = CSharpCompilation.Create("Script_" + Guid.NewGuid().ToString("N"), [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            var diagnostics = new List<string>();
            var spans = new List<ScriptDiagnostic>();
            CollectDiagnostics(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error), diagnostics, spans);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                    || method.ContainingType.ToDisplayString() != typeof(ScriptContext).FullName || method.Name != nameof(ScriptContext.Output)) continue;
                var name = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                var constant = name is null ? default : model.GetConstantValue(name);
                if (constant.Value is not string output || output.Length is 0 or > 32 || !char.IsAsciiLetter(output[0])
                    || !output.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                    AddDiagnostic(diagnostics, spans, "Output names must be constant strings, start with a letter, and contain at most 32 letters, digits or underscores.", name ?? invocation);
                else names.Add(output);
            }
            if (names.Count > 32) diagnostics.Add("At most 32 outputs are allowed.");
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is UnsafeStatementSyntax or PointerTypeSyntax or StackAllocArrayCreationExpressionSyntax)
                    AddDiagnostic(diagnostics, spans, "unsafe code is not allowed in scripts.", node);
                if (node is AttributeSyntax attribute && attribute.Name.ToString().Contains("DllImport", StringComparison.Ordinal))
                    AddDiagnostic(diagnostics, spans, "DllImport is not allowed in scripts.", node);
                var symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol is not null && Forbidden(symbol))
                    AddDiagnostic(diagnostics, spans, "This API is outside the script sandbox.", node);
            }
            using var image = new MemoryStream();
            if (diagnostics.Count == 0)
            {
                var emitted = compilation.Emit(image);
                CollectDiagnostics(emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error), diagnostics, spans);
            }
            if (diagnostics.Count > 0)
            {
                await protocol.WriteLineAsync(JsonSerializer.Serialize(new CSharpScriptReply(request.Id, false, [], new(), new(), diagnostics.Take(20).ToArray(), Spans: spans.Take(20).ToArray()), json));
                continue;
            }
            compiled = (image.ToArray(), names.ToArray());
            if (cache.Count >= 8) cache.Clear();
            cache[cacheKey] = compiled;
        }
        if (request.Operation == "compile") reply = new(request.Id, true, compiled.Outputs, new(), new(), []);
        else
        {
            var loader = new AssemblyLoadContext("script-" + request.Id, isCollectible: true);
            try
            {
                using var image = new MemoryStream(compiled.Image);
                var assembly = loader.LoadFromStream(image);
                var script = (IScadaScript)Activator.CreateInstance(assembly.GetType("Scada.Generated.Script", throwOnError: true)!)!;
                var context = new ScriptContext(request, compiled.Outputs,
                    (profile, command, timeout) => SerialGateway.Exchange(request.SerialProfiles, profile, command, timeout));
                lastContext = context;
                using var timeout = new CancellationTokenSource();
                if (request.TimeoutMs > 0) timeout.CancelAfter(request.TimeoutMs);
                await script.ExecuteAsync(context, timeout.Token);
                if (context.Outputs.Count > 32 || context.Writes.Count > 16 || context.Logs.Count > 16)
                    throw new InvalidOperationException("Result limits exceeded.");
                reply = new(request.Id, true, compiled.Outputs, context.Outputs, context.Writes, [], Logs: context.Logs.ToArray(), Trace: context.Trace.ToArray(),
                    SerialUses: context.SerialUses.Count == 0 ? null : context.SerialUses.ToArray());
            }
            finally { loader.Unload(); }
        }
    }
    catch (Exception exception)
    {
        var message = (exception is TargetInvocationException ? exception.InnerException ?? exception : exception).Message;
        reply = new(request?.Id ?? "", false, [], new(), new(), [], message[..Math.Min(message.Length, 1024)],
            SerialUses: lastContext is null || lastContext.SerialUses.Count == 0 ? null : lastContext.SerialUses.ToArray());
    }
    reply = reply with { ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    await protocol.WriteLineAsync(JsonSerializer.Serialize(reply, json));
}

static bool Forbidden(ISymbol symbol)
{
    for (var current = symbol; current is not null; current = current.ContainingSymbol)
    {
        if (current is not INamespaceSymbol ns || ns.IsGlobalNamespace) continue;
        var name = ns.ToDisplayString();
        if (name is "System.Diagnostics" or "System.Security" or "Microsoft.Win32"
            || name.StartsWith("System.Diagnostics.", StringComparison.Ordinal)
            || name.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal) || name.StartsWith("System.Runtime.Loader", StringComparison.Ordinal)
            || name.StartsWith("System.Security.", StringComparison.Ordinal) || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal))
            return true;
    }
    return false;
}

static void CollectDiagnostics(IEnumerable<Diagnostic> items, List<string> diagnostics, List<ScriptDiagnostic> spans)
{
    foreach (var diagnostic in items)
    {
        if (diagnostics.Count >= 20) return;
        diagnostics.Add(diagnostic.ToString());
        var message = diagnostic.GetMessage();
        if (message.Length == 0) message = diagnostic.Id.ToString();
        if (message.Length > 4000) message = message[..4000];
        AddSpan(spans, message, diagnostic.Location.GetMappedLineSpan());
    }
}

static void AddDiagnostic(List<string> diagnostics, List<ScriptDiagnostic> spans, string message, SyntaxNode node)
{
    if (diagnostics.Count >= 20) return;
    diagnostics.Add(message);
    AddSpan(spans, message, node.GetLocation().GetMappedLineSpan());
}

static void AddSpan(List<ScriptDiagnostic> spans, string message, FileLinePositionSpan mapped)
{
    if (spans.Count >= 20) return;
    var file = Path.GetFileName(mapped.Path);
    if (file is not ("script.cs" or "library.cs") || mapped.StartLinePosition.Line < 0) return;
    var startLine = mapped.StartLinePosition.Line + 1;
    var startColumn = mapped.StartLinePosition.Character + 1;
    var endLine = mapped.EndLinePosition.Line + 1;
    var endColumn = mapped.EndLinePosition.Character + 1;
    if (endLine == startLine && endColumn <= startColumn) endColumn = startColumn + 1;
    spans.Add(new ScriptDiagnostic(message, file, startLine, startColumn, endLine, endColumn));
}

static async Task ExitWithParentAsync(Process parent)
{
    using (parent) await parent.WaitForExitAsync();
    Environment.Exit(0);
}