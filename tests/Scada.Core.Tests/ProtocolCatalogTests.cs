using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;

namespace Scada.Core.Tests;

public class ProtocolCatalogTests
{
    private sealed class FakeSpec(ProtocolType protocol) : IProtocolSpec
    {
        public ProtocolType Protocol => protocol;
        public bool UsesHostPort => false;
        public bool UsesCredentials => false;
        public ProtocolFormSpec Form { get; } = new(0, [], "");
        public string? ValidateDevice(Device device) => null;
        public string? ValidateTag(Tag tag, Device? device) => null;
    }

    [Fact]
    public void DuplicateRegistrationRequiresExplicitReplace()
    {
        // 覆盖内置规格必须显式声明，和驱动覆盖的 SHA-256 允许列表保持同一强度。
        Assert.Throws<InvalidOperationException>(() => ProtocolCatalog.Register(ProtocolCatalog.All[0]));
    }

    [Fact]
    public void NullSpecIsRejected() => Assert.Throws<ArgumentNullException>(() => ProtocolCatalog.Register(null!));

    [Fact]
    public void ExternalSpecIsDiscoverableAfterRegistration()
    {
        // 用一个未占用的枚举值代表插件新增协议。注册是进程级的，这里注册后不再撤销，
        // 其余用例不得假设 ProtocolCatalog.All 的长度固定。
        const ProtocolType external = (ProtocolType)99;
        var spec = new FakeSpec(external);

        ProtocolCatalog.Register(spec);

        Assert.True(ProtocolCatalog.TryGet(external, out var found));
        Assert.Same(spec, found);
        Assert.Contains(ProtocolCatalog.All, x => x.Protocol == external);
        Assert.True(ProtocolCatalog.IsTextAddress(external));
    }
}
