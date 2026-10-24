namespace Scada.Core.Interfaces;

// 节点角色：单机部署恒为主机；主备部署时只有主机采集、下发控制、发通知、跑定时任务。
public interface INodeRole
{
    bool IsActive { get; }
    // 角色每变化一次加一，供采集等组件发现切换后重建。
    long Generation { get; }
    // 主备切换后的第一个网关数据包允许序号跳跃（缺口由历史复制补齐），每个数据流只放行一次。
    bool ConsumeFailoverGap(int gatewayId, Guid streamId) => false;
    string StandbyMessage => "本节点为备机，不采集、不下发控制";
}

public sealed class StandaloneNodeRole : INodeRole
{
    public bool IsActive => true;
    public long Generation => 0;
}
