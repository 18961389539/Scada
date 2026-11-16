using System.Reflection;

namespace Scada.Core.Tests;

/// <summary>
/// 定位仓库根。
///
/// 测试用 <c>dotnet test --artifacts-path /tmp/...</c> 时 <see cref="AppContext.BaseDirectory"/>
/// 落在系统临时目录，而测试宿主还会把 cwd 也设成输出目录——运行时两个锚点向上都找不到
/// <c>Scada.sln</c>，架构测试与夹具比对会误报"找不到解决方案根目录"，看起来像代码坏了。
///
/// 因此正常路径改为读 csproj 在编译期写入的 <see cref="AssemblyMetadataAttribute"/>
/// （`$(MSBuildThisFileDirectory)../..`），与运行方式无关；向上查找只作为兜底保留。
/// </summary>
internal static class Repo
{
    public static string Root()
    {
        var declared = typeof(Repo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == "RepoRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            var declaredRoot = Path.GetFullPath(declared);
            if (File.Exists(Path.Combine(declaredRoot, "Scada.sln"))) return declaredRoot;
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scada.sln")))
                directory = directory.Parent;
            if (directory is not null) return directory.FullName;
        }

        throw new InvalidOperationException("找不到解决方案根目录（Scada.sln）。请从仓库目录内运行测试。");
    }
}
