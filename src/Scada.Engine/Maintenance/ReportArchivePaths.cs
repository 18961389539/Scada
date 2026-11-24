namespace Scada.Engine.Maintenance;

/// <summary>
/// 报表归档路径：库内改存相对 ReportsRoot 的路径，读取与还原时再解析到当前根目录。
/// 兼容还原前写入的绝对路径。
/// </summary>
public static class ReportArchivePaths
{
    public static string Relativize(string reportsRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return fullPath;
        var root = Path.GetFullPath(reportsRoot);
        var full = Path.GetFullPath(fullPath);
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(root, full).Replace('\\', '/');
        return fullPath.Replace('\\', '/');
    }

    public static string Resolve(string reportsRoot, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return stored;
        var root = Path.GetFullPath(reportsRoot);
        var normalized = stored.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(normalized))
            return Path.GetFullPath(Path.Combine(root, normalized));

        var full = Path.GetFullPath(normalized);
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return full;

        // 还原后新根下已有同名归档时优先用新根，避免源目录仍在时把路径写回旧的绝对位置。
        var fileName = Path.GetFileName(full);
        var parent = Path.GetFileName(Path.GetDirectoryName(full));
        if (!string.IsNullOrEmpty(parent))
        {
            var nested = Path.Combine(root, parent, fileName);
            if (File.Exists(nested)) return nested;
        }

        var atRoot = Path.Combine(root, fileName);
        if (File.Exists(atRoot)) return atRoot;
        if (File.Exists(full)) return full;
        return atRoot;
    }
}
