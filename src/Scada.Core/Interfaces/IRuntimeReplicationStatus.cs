namespace Scada.Core.Interfaces;

/// <summary>
/// 运行库复制是否已追平。升主窗口里监视会话重载必须看到这个信号，
/// 不能只凭“读表成功”就把本任期标成已对齐。
/// </summary>
public interface IRuntimeReplicationStatus
{
    /// <summary>尚未同步的运行库行数；单机或已追平时为 0。</summary>
    long RuntimeRowsBehind { get; }

    /// <summary>
    /// 本任期可以信赖运行库中的监视会话。运行库仍落后，或升主时带着未同步尾段时为 false。
    /// </summary>
    bool RuntimeSessionsTrusted { get; }
}

public sealed class TrustedRuntimeReplication : IRuntimeReplicationStatus
{
    public static readonly TrustedRuntimeReplication Instance = new();
    public long RuntimeRowsBehind => 0;
    public bool RuntimeSessionsTrusted => true;
}
