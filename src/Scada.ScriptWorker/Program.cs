using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Scada.Core.Scripting;

var parentArgument = args.FirstOrDefault(argument => argument.StartsWith("--parent=", StringComparison.Ordinal));
if (parentArgument is not null && int.TryParse(parentArgument[9..], out var parentId))
{
    try { _ = ExitWithParentAsync(Process.GetProcessById(parentId)); }
    catch (ArgumentException) { return; }
}
var protocol = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator)
    .Append(typeof(ScriptContext).Assembly.Location).Where(File.Exists).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var cache = new Dictionary<string, (byte[] Image, string[] Outputs)>();
while (await Console.In.ReadLineAsync() is { } line)
{
    if (line.Length > 131_072) break;
    CSharpScriptRequest? request = null;
    CSharpScriptReply reply;
    var started = Stopwatch.GetTimestamp();
    try
    {
        request = JsonSerializer.Deserialize<CSharpScriptRequest>(line, json) ?? throw new InvalidOperationException("Missing request.");
        if (request.Source is null || request.Source.Length is 0 or > 16_384 || request.Inputs?.Count > 64 || request.WritableTags?.Length > 16)
            throw new InvalidOperationException("Script or input limits exceeded.");
        if (request.Operation is not ("compile" or "run")) throw new InvalidOperationException("Unknown operation.");
        if (!cache.TryGetValue(request.Source, out var compiled))
        {
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
                #line 1 "script.cs"
                """ + "\n" + request.Source + "\n#line default\n}}";
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
            var compilation = CSharpCompilation.Create("Script_" + Guid.NewGuid().ToString("N"), [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            var diagnostics = compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).Take(20).Select(item => item.ToString()).ToList();
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
                    diagnostics.Add("Output names must be constant strings, start with a letter, and contain at most 32 letters, digits or underscores.");
                else names.Add(output);
            }
            if (names.Count > 32) diagnostics.Add("At most 32 outputs are allowed.");
            using var image = new MemoryStream();
            if (diagnostics.Count == 0)
            {
                var emitted = compilation.Emit(image);
                diagnostics.AddRange(emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).Take(20).Select(item => item.ToString()));
            }
            if (diagnostics.Count > 0)
            {
                await protocol.WriteLineAsync(JsonSerializer.Serialize(new CSharpScriptReply(request.Id, false, [], new(), new(), diagnostics.Take(20).ToArray()), json));
                continue;
            }
            compiled = (image.ToArray(), names.ToArray());
            if (cache.Count >= 8) cache.Clear();
            cache[request.Source] = compiled;
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
                var context = new ScriptContext(request, compiled.Outputs);
                await script.ExecuteAsync(context, CancellationToken.None);
                if (context.Outputs.Count > 32 || context.Writes.Count > 16 || context.Logs.Count > 16)
                    throw new InvalidOperationException("Result limits exceeded.");
                reply = new(request.Id, true, compiled.Outputs, context.Outputs, context.Writes, [], Logs: context.Logs.ToArray());
            }
            finally { loader.Unload(); }
        }
    }
    catch (Exception exception)
    {
        var message = (exception is TargetInvocationException ? exception.InnerException ?? exception : exception).Message;
        reply = new(request?.Id ?? "", false, [], new(), new(), [], message[..Math.Min(message.Length, 1024)]);
    }
    reply = reply with { ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    await protocol.WriteLineAsync(JsonSerializer.Serialize(reply, json));
}

static async Task ExitWithParentAsync(Process parent)
{
    using (parent) await parent.WaitForExitAsync();
    Environment.Exit(0);
}