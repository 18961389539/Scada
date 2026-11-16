using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Scada.Server.Tests;

/// <summary>
/// 端点登记是手写的 48 行清单：漏一行不会报编译错，只会在运行时变成 404 / OpenAPI 里没有。
/// 这条用例把所有 MapXxxEndpoints 定义与清单对齐，新增领域时漏登记会直接变红。
/// </summary>
public sealed class EndpointRegistrationTests
{
    [Fact]
    public void EveryMapperIsWiredIntoEndpointRegistration()
    {
        var root = FindRoot();
        var server = Path.Combine(root, "src", "Scada.Server");
        var registration = File.ReadAllText(Path.Combine(server, "Endpoints", "EndpointRegistration.cs"));
        var missing = new SortedSet<string>();
        var count = 0;
        foreach (var file in Directory.GetFiles(server, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"public static void (Map\w+Endpoints)\(this IEndpointRouteBuilder"))
            {
                var name = m.Groups[1].Value;
                if (name == "MapScadaEndpoints") continue; // 清单方法自身
                count++;
                if (!Regex.IsMatch(registration, $@"\b{name}\("))
                    missing.Add($"{name} ({Path.GetRelativePath(root, file)})");
            }
        }
        Assert.True(count > 40, $"扫描到的端点映射方法只有 {count} 个，正则或目录结构可能已经失效");
        Assert.True(missing.Count == 0,
            "以下端点映射方法已定义但没有在 EndpointRegistration.cs 中登记：\n" + string.Join("\n", missing));
    }

    private static string FindRoot([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? "."); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Scada.sln"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
