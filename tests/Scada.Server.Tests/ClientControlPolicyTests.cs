using Scada.Server.Auth;
using Xunit;

namespace Scada.Server.Tests;

public sealed class ClientControlPolicyTests
{
    [Theory]
    [InlineData("/api/commands")]
    [InlineData("/api/screens/12/actions/start")]
    [InlineData("/api/control/requests/11111111-1111-1111-1111-111111111111/approve")]
    [InlineData("/api/control/sequences/3/start")]
    [InlineData("/api/control/runs/9/abort")]
    [InlineData("/api/control/lockouts")]
    [InlineData("/api/control/lockouts/4/remove")]
    [InlineData("/api/control/modes/8")]
    [InlineData("/api/recipes/5/download")]
    [InlineData("/api/recipes/downloads/6/abort")]
    [InlineData("/api/annunciators/2/silence")]
    [InlineData("/api/annunciators/2/clear-silence")]
    [InlineData("/api/annunciators/2/test")]
    [InlineData("/api/annunciators/silence-visible")]
    [InlineData("/api/traceability/jobs/7/scan")]
    public void ControlPath_Includes_Field_Changing_Routes(string path)
    {
        Assert.Matches(ClientControlPolicy.ControlPath(), path);
    }

    [Theory]
    [InlineData("/api/control/sequences/3/check-start")]
    [InlineData("/api/control/lockouts/4")]
    [InlineData("/api/annunciators")]
    [InlineData("/api/annunciators/2")]
    [InlineData("/api/devices/2/test")]
    [InlineData("/api/recipes/5")]
    [InlineData("/api/control/sequences")]
    public void ControlPath_Ignores_Configuration_Or_Read_Routes(string path)
    {
        Assert.DoesNotMatch(ClientControlPolicy.ControlPath(), path);
    }
}
