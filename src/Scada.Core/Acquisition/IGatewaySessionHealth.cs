using Scada.Core.Entities;

namespace Scada.Core.Acquisition;

/// <summary>网关租约/会话是否仍有效。主键走网关时，回切探活用它，而不是由主机对 PLC 做 TCP 探活。</summary>
public interface IGatewaySessionHealth
{
    bool IsHealthy(int gatewayId);
}
