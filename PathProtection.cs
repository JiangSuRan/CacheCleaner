namespace CacheCleaner;

internal static class PathProtection
{
    private static readonly Lazy<string[]> ProtectedRoots = new(() => ProtectedPaths()
        .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)).ToArray());
    internal static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ProtectedPaths()
    {
        var ctx = new ScanContext();
        foreach (var rule in CleaningRules.All.Where(r => r.Clean == "reportOnly" && r.Kind != "special"))
        {
            var root = rule.Base switch
            {
                BaseFolder.LocalAppData => ctx.LocalAppData,
                BaseFolder.AppData => ctx.AppData,
                BaseFolder.UserProfile => ctx.UserProfile,
                BaseFolder.Windows => ctx.WindowsDir,
                BaseFolder.ProgramData => ctx.ProgramData,
                _ => CacheScanner.SysRoot
            };
            yield return Path.Combine(root, rule.Path);
        }
        foreach (var agent in AgentCatalog.All)
            if (agent.HomeFullPath is { } home)
                foreach (var relative in agent.NeverTouch)
                    yield return Path.Combine(home, relative);
    }

    internal static bool IsProtected(string path)
    {
        // 编辑器本地历史可能包含尚未提交或备份的内容。
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Split(Path.DirectorySeparatorChar)
            .Any(p => p.Equals("LocalHistory", StringComparison.OrdinalIgnoreCase))) return true;
        return ProtectedRoots.Value.Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasLinkedAncestor(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
