using Scada.Core.Options;
using Scada.Core.Protocols;

namespace Scada.Core.Tests;

public sealed class NotificationOutboundPolicyTests
{
    [Fact]
    public void RejectsLinkLocalAndAllowsHostnamesAndPrivate()
    {
        Assert.Contains("链路本地", NotificationOutboundPolicy.ValidateHost("169.254.1.1", null));
        Assert.Contains("链路本地", NotificationOutboundPolicy.ValidateHttpUrl("http://169.254.10.2/hook", null));
        Assert.Null(NotificationOutboundPolicy.ValidateHost("smtp.office365.com", null));
        Assert.Null(NotificationOutboundPolicy.ValidateHost("10.0.0.8", null));
        Assert.Null(NotificationOutboundPolicy.ValidateHost("127.0.0.1", null));
        Assert.Null(NotificationOutboundPolicy.ValidateHost("smtp", null));
        Assert.Null(NotificationOutboundPolicy.ValidateHttpUrl("http://x/h", null));
        Assert.Contains("公网 IP", NotificationOutboundPolicy.ValidateHost("8.8.8.8", null));
        var settings = new ScadaSettings { NotificationTargetAllowlist = ["8.8.8.8"] };
        Assert.Null(NotificationOutboundPolicy.ValidateHost("8.8.8.8", settings));
    }
}