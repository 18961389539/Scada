namespace Scada.Core.Configuration;

/// <summary>组态写入时修订号不匹配。接口应返回 409，而不是当成普通校验失败。</summary>
public sealed class ConfigRevisionConflictException : InvalidOperationException
{
    public ConfigRevisionConflictException() : base(ConfigRevision.ConflictMessage) { }

    public ConfigRevisionConflictException(string message) : base(message) { }
}
