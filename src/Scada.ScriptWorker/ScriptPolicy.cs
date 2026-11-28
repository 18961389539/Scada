using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Scada.ScriptWorker;

/// <summary>
/// 脚本编译期策略表。这是"防误用"加上一条硬限制：文件与网络 API 默认禁止，
/// 避免已授权的脚本读写配置库或外送数据。它仍不是完整沙箱；Worker 与服务器同账户运行，
/// Windows 上的 Job Object 只限制内存和子进程，安装形态下不便再改运行账户。
/// </summary>
internal static class ScriptPolicy
{
    public static void Analyze(SyntaxNode root, SemanticModel model, ScriptDiagnosticBag bag)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is UnsafeStatementSyntax or PointerTypeSyntax or StackAllocArrayCreationExpressionSyntax)
                bag.Add("unsafe code is not allowed in scripts.", node);
            if (node is AttributeSyntax attribute && attribute.Name.ToString().Contains("DllImport", StringComparison.Ordinal))
                bag.Add("DllImport is not allowed in scripts.", node);
            if (bag.Full) return;
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol is not null && Forbidden(symbol))
                bag.Add("This API is outside the script sandbox.", node);
        }
    }

    // 按"成员 + 类型 + 命名空间"三级判断：Console 是类型而不是命名空间，
    // 只查命名空间会漏掉它（System 命名空间显然不能整体禁掉）。
    public static bool Forbidden(ISymbol symbol)
    {
        // Environment 只禁会终止进程或改环境的三项，读取环境变量仍可用于诊断
        // （Worker 的环境本身已被宿主清空，见 CSharpScriptHost 的 start.Environment）。
        if (symbol is IMethodSymbol method && method.ContainingType.ToDisplayString() == "System.Environment"
            && method.Name is "Exit" or "FailFast" or "SetEnvironmentVariable") return true;
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamespaceSymbol { IsGlobalNamespace: false } space && ForbiddenNamespace(space.ToDisplayString())) return true;
            if (current is INamedTypeSymbol type && ForbiddenType(type.ToDisplayString())) return true;
        }
        return false;
    }

    private static bool ForbiddenNamespace(string name) =>
        name is "System.Diagnostics" or "System.Security" or "System.Reflection" or "Microsoft.Win32"
            or "System.Runtime.CompilerServices" or "System.Runtime.InteropServices" or "System.Runtime.Loader"
            or "System.Net" or "System.IO.Ports" or "System.IO.Pipes" or "System.IO.MemoryMappedFiles"
            or "System.IO.IsolatedStorage"
        || name.StartsWith("System.Diagnostics.", StringComparison.Ordinal)
        || name.StartsWith("System.Reflection.", StringComparison.Ordinal)
        || name.StartsWith("System.Security.", StringComparison.Ordinal)
        || name.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal)
        || name.StartsWith("System.Runtime.Loader", StringComparison.Ordinal)
        || name.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)
        || name.StartsWith("System.Net.", StringComparison.Ordinal)
        || name.StartsWith("System.IO.Ports.", StringComparison.Ordinal)
        || name.StartsWith("System.IO.Pipes.", StringComparison.Ordinal)
        || name.StartsWith("System.IO.MemoryMappedFiles.", StringComparison.Ordinal)
        || name.StartsWith("System.IO.IsolatedStorage.", StringComparison.Ordinal);

    // Console 能拿到原始标准流：即使协议已改走独立管道，也没必要让脚本阻塞在无人读取的 stdout 上。
    // System.IO 仍允许 MemoryStream / Path 等内存与路径字符串 API；禁止的是会碰磁盘的类型。
    private static bool ForbiddenType(string name) => name is "System.Console"
        or "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo" or "System.IO.DirectoryInfo"
        or "System.IO.FileStream" or "System.IO.FileSystemWatcher" or "System.IO.DriveInfo"
        or "System.IO.RandomAccess" or "System.IO.FileSystemAclExtensions";
}
