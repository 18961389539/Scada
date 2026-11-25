using Scada.Server.Ha;

namespace Scada.Server.Tests;

public sealed class HaPeerTransportTests
{
    [Fact]
    public void WarnsOnlyForEnabledNonLoopbackHttpPeer()
    {
        Assert.Null(HaPeerTransport.InsecureWarning(new HaOptions { Enabled = false, PeerUrl = "http://10.0.0.12:5080/" }));
        Assert.Null(HaPeerTransport.InsecureWarning(new HaOptions { Enabled = true, PeerUrl = "https://10.0.0.12:5080/" }));
        Assert.Null(HaPeerTransport.InsecureWarning(new HaOptions { Enabled = true, PeerUrl = "http://127.0.0.1:5080/" }));
        Assert.Null(HaPeerTransport.InsecureWarning(new HaOptions { Enabled = true, PeerUrl = "http://localhost:5080/" }));
        Assert.False(HaPeerTransport.IsInsecurePeer("not-a-url"));

        var warning = HaPeerTransport.InsecureWarning(new HaOptions { Enabled = true, PeerUrl = "http://10.0.0.12:5080/" });
        Assert.NotNull(warning);
        Assert.Equal(HaPeerTransport.InsecureWarningCode, warning!.Code);
        Assert.Equal(HaPeerTransport.InsecureWarningMessage, warning.Message);
    }
}
