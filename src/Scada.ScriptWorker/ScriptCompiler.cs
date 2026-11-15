using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Scada.Core.Scripting;

namespace Scada.ScriptWorker;

internal sealed record ScriptCompileResult(CompiledScript? Script, ScriptDiagnosticBag Diagnostics, bool FromCache);

/// <summary>
/// 把用户方法体包进固定模板、编译、跑策略检查并写入缓存。
/// 模板一变就必须提升 <see cref="CompileCache"/> 的 TemplateVersion，否则会命中用旧模板编出的产物。
/// </summary>
internal sealed class ScriptCompiler(IReadOnlyList<MetadataReference> references, CompileCache cache)
{
    public ScriptCompileResult Compile(string source, string? librarySource)
    {
        var key = CompileCache.Key(source, librarySource);
        if (cache.TryGet(key, out var cached)) return new(cached, new ScriptDiagnosticBag(), true);
        var body = string.IsNullOrEmpty(librarySource)
            ? "#line 1 \"script.cs\"\n" + source
            : "#line 1 \"library.cs\"\n" + librarySource + "\n#line 1 \"script.cs\"\n" + source;
        var text = """
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
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14));
        var compilation = CSharpCompilation.Create("Script_" + Guid.NewGuid().ToString("N"), [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var bag = new ScriptDiagnosticBag();
        foreach (var diagnostic in compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error)) bag.Add(diagnostic);
        var model = compilation.GetSemanticModel(tree);
        var names = DiscoverOutputs(tree.GetRoot(), model, bag);
        // 策略检查不因编译错误而跳过：语法错误与"用了不该用的 API"常常同时出现，
        // 只报编译器消息会让人以为改语法就能过。
        ScriptPolicy.Analyze(tree.GetRoot(), model, bag);
        using var image = new MemoryStream();
        using var pdb = new MemoryStream();
        if (bag.Messages.Count == 0)
        {
            // 必须产出可移植 PDB：#line 映射只有带序列点时才作用于运行时堆栈，
            // 否则脚本抛异常只能拿到一句消息，无法定位到用户代码行。
            var emitted = compilation.Emit(image, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            foreach (var diagnostic in emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)) bag.Add(diagnostic);
        }
        if (bag.Messages.Count > 0) return new(null, bag, false);
        var compiled = new CompiledScript(image.ToArray(), pdb.ToArray(), names);
        cache.Put(key, compiled.Image, compiled.Pdb, compiled.Outputs);
        return new(compiled, bag, false);
    }

    // 输出名必须是编译期常量：运行时才拼出来的名字无法在预览时展示，也无法校验结果集。
    private static string[] DiscoverOutputs(SyntaxNode root, SemanticModel model, ScriptDiagnosticBag bag)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || method.ContainingType.ToDisplayString() != typeof(ScriptContext).FullName || method.Name != nameof(ScriptContext.Output)) continue;
            var name = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            var constant = name is null ? default : model.GetConstantValue(name);
            if (constant.Value is not string output || output.Length is 0 or > 32 || !char.IsAsciiLetter(output[0])
                || !output.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                bag.Add("Output names must be constant strings, start with a letter, and contain at most 32 letters, digits or underscores.", name ?? invocation);
            else names.Add(output);
            if (bag.Full) break;
        }
        if (names.Count > CSharpScriptLimits.MaxOutputs) bag.Add($"At most {CSharpScriptLimits.MaxOutputs} outputs are allowed.");
        return names.ToArray();
    }
}
