namespace Scada.Core.Scripting;

/// <summary>
/// 握手校验：独立成纯函数，才能在没有真实 Worker 进程的情况下覆盖"版本不匹配"路径。
/// </summary>
public static class ScriptHandshake
{
    public static string? Validate(CSharpScriptHello? hello, int expectedVersion)
    {
        if (hello is null) return "Script worker did not announce its protocol version.";
        if (hello.ProtocolVersion != expectedVersion)
            return $"Script worker protocol v{hello.ProtocolVersion} does not match server v{expectedVersion}; redeploy the server and its ScriptWorker directory together.";
        return null;
    }
}
