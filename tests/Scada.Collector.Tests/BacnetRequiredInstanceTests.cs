using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;
namespace Scada.Collector.Tests;

public sealed class BacnetRequiredInstanceTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"deviceInstance\":-1}", false)]
    [InlineData("{\"deviceInstance\":0}", true)]
    [InlineData("{\"deviceInstance\":4194302}", true)]
    [InlineData("{\"deviceInstance\":4194303}", false)]
    public void MissingIsNotZero(string options, bool valid)
    {
        var device = new Device { Protocol = ProtocolType.BacnetIp, ProtocolOptions = options };
        Assert.Equal(valid, new BacnetSpec().ValidateDevice(device) is null);
    }

    [Fact]
    public void FormRequiresExplicitInstance()
    {
        var field = Assert.Single(ProtocolForms.BacnetIp.Fields, x => x.Key == "deviceInstance");
        Assert.Equal(-1, field.Default);
        Assert.Equal(0d, field.Min);
    }
}
