using System.Xml.Linq;

namespace Scada.Core.Tests;

/// <summary>
/// Core 的零依赖是它最重要的资产：外部驱动插件只引用 <c>Scada.Core</c>，
/// 因此这个程序集一旦引入 NuGet 包（或反向引用宿主项目），插件就会被拖上整条依赖链。
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void CoreHasNoPackageOrProjectReferences()
    {
        var project = Path.Combine(Repo.Root(), "src", "Scada.Core", "Scada.Core.csproj");
        var document = XDocument.Load(project);

        Assert.Empty(document.Descendants("PackageReference"));
        Assert.Empty(document.Descendants("ProjectReference"));
    }
}
