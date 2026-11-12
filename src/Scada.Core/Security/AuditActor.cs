namespace Scada.Core.Security;

/// <summary>
/// 一次操作的身份：谁改的、从哪来。审计行不再依赖 HttpContext，
/// 后台任务、MCP、命令行工具都能给出明确身份，而不是落成空用户名。
/// </summary>
public sealed record AuditActor(int? UserId, string UserName, string Source)
{
    public static readonly AuditActor System = new(null, "system", "system");
    public static readonly AuditActor Anonymous = new(null, "anonymous", "http");
}

/// <summary>
/// 当前异步流的审计身份与抑制开关。用 AsyncLocal 而不是 DI：
/// 拦截器是在 <c>AddDbContextFactory</c> 里构造的单例（跟着 options 走），拿不到请求作用域；
/// 而身份本来就随请求流动，AsyncLocal 正好是它的生命周期。
/// </summary>
public static class AuditContext
{
    private static readonly AsyncLocal<AuditActor?> Actor = new();
    private static readonly AsyncLocal<int> Depth = new();

    public static AuditActor Current => Actor.Value ?? AuditActor.System;

    /// <summary>工程导入这类批量操作会一次性改动上千行，逐行写审计会淹没有效信息，也拖慢事务。</summary>
    public static bool Suppressed => Depth.Value > 0;

    public static IDisposable Begin(AuditActor actor)
    {
        var previous = Actor.Value;
        Actor.Value = actor;
        return new Restore(() => Actor.Value = previous);
    }

    public static IDisposable Suppress()
    {
        Depth.Value++;
        return new Restore(() => Depth.Value--);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
