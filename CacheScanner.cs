using System.Diagnostics;

namespace CacheCleaner;

/// <summary>
/// 缓存扫描项
/// </summary>
public class CacheItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Desc { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool Exists { get; set; }
    public bool Checked { get; set; }
    public string Risk { get; set; } = "safe"; // safe, warn, danger
}

/// <summary>
/// 自动扫描 C 盘缓存
/// </summary>
public static class CacheScanner
{
    /// <summary>
    /// 定义所有已知的缓存位置（自动检测是否存在）
    /// </summary>
    private static readonly (string Name, string Path, string Desc, string Risk)[] KnownCaches =
    [
        // Python 相关
        ("pip 缓存", @"pip", "Python 包管理器缓存，清理后不影响已安装的包", "safe"),
        ("uv 缓存", @"uv\cache", "Python uv 包管理器缓存", "safe"),
        ("Conda 缓存", @"pkgs", "Anaconda/Miniconda 包缓存（仅清理 pkgs 目录）", "safe"),

        // Node.js 相关
        ("npm 缓存", @"npm-cache", "Node.js 包管理器缓存", "safe"),

        // IDE / 编辑器
        ("VS Code 缓存", @"Code\Cache", "VS Code 编辑器缓存", "safe"),
        ("VS Code 日志", @"Code\logs", "VS Code 日志文件", "safe"),
        ("RStudio 缓存", @"RStudio\cache", "RStudio 缓存（仅 cache 子目录）", "safe"),
        ("RStudio 日志", @"RStudio\log", "RStudio 日志文件", "safe"),

        // 下载工具
        ("迅雷缓存", @"Thunder Network", "迅雷下载缓存", "warn"),

        // AI 工具
        ("豆包缓存", @"Doubao", "豆包 AI 缓存（需先关闭豆包）", "warn"),

        // 视频/图片工具
        ("剪映缓存", @"JianyingPro\User Data\Cache", "剪映视频编辑缓存", "warn"),

        // 浏览器缓存（仅 LocalAppData 下的）
        ("Edge 缓存", @"Microsoft\Edge\User Data\Default\Cache", "Edge 浏览器缓存", "safe"),
        ("Chrome 缓存", @"Google\Chrome\User Data\Default\Cache", "Chrome 浏览器缓存", "safe"),

        // Windows 系统缓存
        ("Windows 临时文件", @"Temp", "Windows 临时文件（仅清理超过7天的）", "warn"),
        ("Windows 缩略图缓存", @"Microsoft\Windows\Explorer", "Windows 资源管理器缩略图缓存", "safe"),
        ("Windows 预读取", @"Prefetch", "Windows 程序预读取缓存", "safe"),

        // Docker（高风险，需二次确认）
        ("Docker 镜像/容器", @"Docker", "Docker Desktop 所有数据（镜像+容器+卷）", "danger"),
    ];

    // Conda 特殊路径
    private static readonly string CondaPkgsPrefix = @"anaconda3\pkgs";
    private static readonly string CondaPkgsPrefix2 = @"miniconda3\pkgs";
    private static readonly string CondaPkgsPrefix3 = @"Miniconda3\pkgs";

    // Windows 缩略图缓存的实际路径
    private static readonly string ThumbCachePath = @"Microsoft\Windows\Explorer";

    /// <summary>
    /// 扫描所有缓存，返回列表
    /// </summary>
    public static List<CacheItem> ScanAll(IProgress<(int current, int total, string name)>? progress = null)
    {
        var items = new List<CacheItem>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        int total = KnownCaches.Length;
        int current = 0;

        foreach (var (name, path, desc, risk) in KnownCaches)
        {
            current++;
            progress?.Report((current, total, name));

            var item = new CacheItem
            {
                Name = name,
                Desc = desc,
                Risk = risk,
                Exists = false,
                SizeBytes = 0
            };

            // 根据类型解析实际路径
            string? resolvedPath = ResolvePath(name, path, localAppData, appData, userProfile, windowsDir);
            if (resolvedPath != null)
            {
                item.Path = resolvedPath;
                item.Exists = Directory.Exists(resolvedPath);
                if (item.Exists)
                {
                    item.SizeBytes = GetDirectorySize(resolvedPath);
                }
            }
            else
            {
                item.Path = "";
                item.Exists = false;
            }

            // 缩略图缓存需要特殊过滤：只计算 thumbcache_* 文件
            if (name == "Windows 缩略图缓存" && item.Exists)
            {
                item.SizeBytes = GetThumbCacheSize(item.Path);
                if (item.SizeBytes == 0) item.Exists = false;
            }

            items.Add(item);
        }

        // 额外扫描：Conda pkgs
        ScanCondaPkgs(items, userProfile, progress, ref current, ref total);

        return items;
    }

    /// <summary>
    /// 解析缓存的实际路径
    /// </summary>
    private static string? ResolvePath(string name, string path, string localAppData, string appData, string userProfile, string windowsDir)
    {
        // pip 特殊处理
        if (name == "pip 缓存")
        {
            return ResolvePipCache();
        }

        // uv 缓存在 LocalAppData 下
        if (name == "uv 缓存")
        {
            return Path.Combine(localAppData, path);
        }

        // VS Code 在 AppData\Roaming 下
        if (name.StartsWith("VS Code"))
        {
            return Path.Combine(appData, path);
        }

        // RStudio 在 LocalAppData 下
        if (name.StartsWith("RStudio"))
        {
            return Path.Combine(localAppData, path);
        }

        // Windows 临时文件
        if (name == "Windows 临时文件")
        {
            return Path.Combine(localAppData, path);
        }

        // Windows 缩略图缓存
        if (name == "Windows 缩略图缓存")
        {
            return Path.Combine(localAppData, ThumbCachePath);
        }

        // Prefetch 在 Windows 目录下
        if (name == "Windows 预读取")
        {
            var prefetchPath = Path.Combine(windowsDir, "Prefetch");
            return Directory.Exists(prefetchPath) ? prefetchPath : null;
        }

        // Docker 在 LocalAppData 下
        if (name == "Docker 镜像/容器")
        {
            return Path.Combine(localAppData, "Docker");
        }

        // 其余默认在 LocalAppData 下
        return Path.Combine(localAppData, path);
    }

    /// <summary>
    /// 获取 pip 缓存路径
    /// </summary>
    private static string? ResolvePipCache()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pip",
                Arguments = "cache dir",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output) && Directory.Exists(output))
            {
                return output;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 扫描 Conda pkgs 目录
    /// </summary>
    private static void ScanCondaPkgs(List<CacheItem> items, string userProfile,
        IProgress<(int current, int total, string name)>? progress, ref int current, ref int total)
    {
        total += 3;
        string[] condaPaths = [
            Path.Combine("C:\\", CondaPkgsPrefix),
            Path.Combine("C:\\", CondaPkgsPrefix2),
            Path.Combine("C:\\", CondaPkgsPrefix3),
        ];

        foreach (var condaPath in condaPaths)
        {
            current++;
            if (Directory.Exists(condaPath))
            {
                progress?.Report((current, total, "Conda 缓存"));
                items.Add(new CacheItem
                {
                    Name = "Conda 包缓存",
                    Path = condaPath,
                    Desc = "Anaconda/Miniconda 包缓存目录",
                    Risk = "safe",
                    Exists = true,
                    SizeBytes = GetDirectorySize(condaPath)
                });
                // 找到一个就够了
                return;
            }
        }
    }

    /// <summary>
    /// 计算目录大小
    /// </summary>
    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    /// <summary>
    /// 计算缩略图缓存大小（仅 thumbcache_* 文件）
    /// </summary>
    private static long GetThumbCacheSize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "thumbcache_*")
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    /// <summary>
    /// 格式化文件大小显示
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:F2} {units[unitIndex]}";
    }

    /// <summary>
    /// 清理指定缓存项，返回实际释放的字节数
    /// </summary>
    public static long CleanItem(CacheItem item, IProgress<string>? progress = null)
    {
        if (!item.Exists || string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
            return 0;

        long freed;

        switch (item.Name)
        {
            case "pip 缓存":
                freed = CleanPipCache(item, progress);
                break;

            case "Windows 临时文件":
                freed = CleanTempFiles(item.Path, progress);
                break;

            case "Windows 缩略图缓存":
                freed = CleanThumbCache(item.Path, progress);
                break;

            default:
                freed = CleanDirectory(item.Path, progress);
                break;
        }

        // 更新状态
        if (freed > 0)
        {
            item.SizeBytes = Math.Max(0, item.SizeBytes - freed);
            if (item.SizeBytes == 0) item.Exists = false;
        }

        return freed;
    }

    /// <summary>
    /// 清理 pip 缓存（使用 pip cache purge 命令）
    /// </summary>
    private static long CleanPipCache(CacheItem item, IProgress<string>? progress)
    {
        long sizeBefore = item.SizeBytes;
        try
        {
            progress?.Report("正在执行 pip cache purge...");
            var psi = new ProcessStartInfo
            {
                FileName = "pip",
                Arguments = "cache purge",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(10000);
            }
        }
        catch { }

        // 重新计算大小
        long sizeAfter = GetDirectorySize(item.Path);
        return Math.Max(0, sizeBefore - sizeAfter);
    }

    /// <summary>
    /// 清理临时文件（仅删除超过7天的文件）
    /// </summary>
    private static long CleanTempFiles(string path, IProgress<string>? progress)
    {
        if (!Directory.Exists(path)) return 0;
        long freed = 0;
        var cutoff = DateTime.Now.AddDays(-7);

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime < cutoff)
                    {
                        freed += info.Length;
                        File.Delete(file);
                    }
                }
                catch { }
            }

            // 清理空目录
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch { }
            }
        }
        catch { }

        return freed;
    }

    /// <summary>
    /// 清理缩略图缓存（仅删除 thumbcache_* 文件）
    /// </summary>
    private static long CleanThumbCache(string path, IProgress<string>? progress)
    {
        if (!Directory.Exists(path)) return 0;
        long freed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "thumbcache_*"))
            {
                try
                {
                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }

        return freed;
    }

    /// <summary>
    /// 清理整个目录内容（保留目录本身，只删除内容）
    /// </summary>
    private static long CleanDirectory(string path, IProgress<string>? progress)
    {
        if (!Directory.Exists(path)) return 0;
        long sizeBefore = GetDirectorySize(path);

        try
        {
            // 删除所有文件
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { }
            }

            // 删除所有子目录（从深到浅，避免父目录先删）
            var dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length);
            foreach (var dir in dirs)
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }

        long sizeAfter = GetDirectorySize(path);
        return Math.Max(0, sizeBefore - sizeAfter);
    }
}
