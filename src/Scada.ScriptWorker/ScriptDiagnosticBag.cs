using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Scada.Core.Scripting;

namespace Scada.ScriptWorker;

/// <summary>编译期诊断的收集器：消息给人看，spans 给编辑器跳转，两者共用同一份上限。</summary>
internal sealed class ScriptDiagnosticBag
{
    public List<string> Messages { get; } = new();
    public List<ScriptDiagnostic> Spans { get; } = new();
    public bool Full => Messages.Count >= CSharpScriptLimits.MaxDiagnostics;

    public void Add(Diagnostic diagnostic)
    {
        if (Full) return;
        Messages.Add(diagnostic.ToString());
        var message = diagnostic.GetMessage();
        if (message.Length == 0) message = diagnostic.Id.ToString();
        if (message.Length > CSharpScriptLimits.MaxDiagnosticChars) message = message[..CSharpScriptLimits.MaxDiagnosticChars];
        AddSpan(message, diagnostic.Location.GetMappedLineSpan());
    }

    public void Add(string message, SyntaxNode? node = null)
    {
        if (Full) return;
        Messages.Add(message);
        if (node is not null) AddSpan(message, node.GetLocation().GetMappedLineSpan());
    }

    private void AddSpan(string message, FileLinePositionSpan mapped)
    {
        if (Spans.Count >= CSharpScriptLimits.MaxDiagnostics) return;
        var file = Path.GetFileName(mapped.Path);
        if (file is not ("script.cs" or "library.cs") || mapped.StartLinePosition.Line < 0) return;
        var startLine = mapped.StartLinePosition.Line + 1;
        var startColumn = mapped.StartLinePosition.Character + 1;
        var endLine = mapped.EndLinePosition.Line + 1;
        var endColumn = mapped.EndLinePosition.Character + 1;
        if (endLine == startLine && endColumn <= startColumn) endColumn = startColumn + 1;
        Spans.Add(new ScriptDiagnostic(message, file, startLine, startColumn, endLine, endColumn));
    }
}
