using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Scada.Collector;
using Scada.Core.Enums;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

/// <summary>
/// 协议曾经有三份靠人手同步的清单：ProtocolType 枚举、DeviceDriverFactory.BuiltInProtocols、
/// 以及工厂构造函数里的 _builtIns 字典（ProtocolCatalog 的 spec 是第四份）。
/// 加一个协议漏掉其中任意一份都不会报编译错，只会静默变成"该协议无驱动"或"无表单"。
/// 这条用例把它们钉在一起，新增枚举成员而没补驱动时立即变红。
/// </summary>
public sealed class ProtocolRegistryConsistencyTests
{
    [Fact]
    public void BuiltInProtocolSetMatchesTheActualDriverDictionary()
    {
        var source = File.ReadAllText(SourcePath());
        // 只匹配 _builtIns 里的字典项（HashSet 初始化器没有 "[ProtocolType.X] = new(" 这种形态）。
        var drivers = Regex.Matches(source, @"\[ProtocolType\.(\w+)\]\s*=\s*new\(")
            .Select(m => Enum.Parse<ProtocolType>(m.Groups[1].Value))
            .ToHashSet();
        Assert.True(drivers.Count >= 15, $"只扫到 {drivers.Count} 个内置驱动，正则或文件结构可能已失效");
        Assert.Empty(DeviceDriverFactory.BuiltInProtocols.Except(drivers));
        Assert.Empty(drivers.Except(DeviceDriverFactory.BuiltInProtocols));
    }

    [Fact]
    public void EveryProtocolHasEitherADriverOrAnExplicitPluginOnlySpec()
    {
        var builtIn = DeviceDriverFactory.BuiltInProtocols;
        var specs = ProtocolCatalog.All.Select(x => x.Protocol).ToHashSet();
        var all = Enum.GetValues<ProtocolType>().ToHashSet();

        Assert.Empty(builtIn.Except(all));
        Assert.Empty(specs.Except(all));
        // 新增枚举成员必须至少有一处实现：内置驱动，或一份能阻止设备启用的协议 spec。
        Assert.Empty(all.Except(builtIn.Union(specs)));
        // 除了 IEC 61850（GPLv3 组件不能随产品分发，只由插件提供），
        // 每份 spec 都应该有对应的内置驱动，否则前端能选、后端采不了。
        Assert.Equal(new[] { ProtocolType.Iec61850 }, specs.Except(builtIn).Order().ToArray());
        Assert.False(builtIn.Contains(ProtocolType.Iec61850));
    }

    [Fact]
    public void EverySpecHasAFormAndAStableProtocol()
    {
        Assert.Equal(ProtocolCatalog.All.Count, ProtocolCatalog.All.Select(x => x.Protocol).Distinct().Count());
        foreach (var spec in ProtocolCatalog.All)
        {
            Assert.NotNull(spec.Form);
            Assert.True(ProtocolCatalog.TryGet(spec.Protocol, out var found) && ReferenceEquals(found, spec),
                $"{spec.Protocol} 的 spec 没有进 TryGet 索引");
        }
    }

    private static string SourcePath([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? "."); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Scada.Collector", "DeviceDriverFactory.cs");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("DeviceDriverFactory.cs not found");
    }
}
