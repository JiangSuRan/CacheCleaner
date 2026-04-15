using System.Diagnostics;

namespace CacheCleaner;

/// <summary>
/// 缓存清理风险等级
/// </summary>
public enum RiskLevel
{
    Safe,
    Warn,
    Danger
}

/// <summary>
/// 路径基准目录类型
/// </summary>
internal enum BaseFolder
{
    LocalAppData,
    AppData,
    Windows
}

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
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;
}

/// <summary>
/// 系统路径上下文，避免重复获取和传递
/// </summary>
internal sealed class ScanContext
{
    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string AppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string UserProfile { get; } = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
    public string WindowsDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}

/// <summary>
/// 自动扫描 C 盘缓存
/// </summary>
public static class CacheScanner
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    // Conda 可能的安装路径
    private static readonly string[] CondaPkgsPaths =
    [
        @"C:\anaconda3\pkgs",
        @"C:\miniconda3\pkgs",
        @"C:\Miniconda3\pkgs"
    ];

    // 合法的 pip 缓存路径特征
    private static readonly string[] ValidPipCacheKeywords = ["pip", "cache", "pypa"];

    /// <summary>
    /// 已知缓存定义：名称、相对路径、说明、风险等级、基准目录
    /// pip 的相对路径留空，扫描时通过命令获取实际路径
    /// </summary>
    private static readonly (string Name, string RelativePath, string Desc, RiskLevel Risk, BaseFolder Base)[] KnownCaches =
    [
        // Python 相关
        ("pip 缓存", "", "Python 包管理器缓存，清理后不影响已安装的包", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("uv 缓存", @"uv\cache", "Python uv 包管理器缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

        // Node.js 相关
        ("npm 缓存", @"npm-cache", "Node.js 包管理器缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

        // IDE / 编辑器
        ("VS Code 缓存", @"Code\Cache", "VS Code 编辑器缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("VS Code 日志", @"Code\logs", "VS Code 日志文件", RiskLevel.Safe, BaseFolder.AppData),
        ("RStudio 缓存", @"RStudio\cache", "RStudio 缓存（仅 cache 子目录）", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("RStudio 日志", @"RStudio\log", "RStudio 日志文件", RiskLevel.Safe, BaseFolder.LocalAppData),

        // 下载工具
        ("迅雷缓存", @"Thunder Network", "迅雷下载缓存", RiskLevel.Warn, BaseFolder.LocalAppData),

        // AI 工具
        ("豆包缓存", @"Doubao", "豆包 AI 缓存（需先关闭豆包）", RiskLevel.Warn, BaseFolder.LocalAppData),

        // 视频/图片工具
        ("剪映缓存", @"JianyingPro\User Data\Cache", "剪映视频编辑缓存", RiskLevel.Warn, BaseFolder.LocalAppData),

        // 浏览器缓存
        ("Edge 缓存", @"Microsoft\Edge\User Data\Default\Cache", "Edge 浏览器缓存", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("Chrome 缓存", @"Google\Chrome\User Data\Default\Cache", "Chrome 浏览器缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

        // Windows 系统缓存
        ("Windows 临时文件", @"Temp", "Windows 临时文件（仅清理超过7天的）", RiskLevel.Warn, BaseFolder.LocalAppData),
        ("Windows 缩略图缓存", @"Microsoft\Windows\Explorer", "Windows 资源管理器缩略图缓存", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("Windows 预读取", @"Prefetch", "Windows 程序预读取缓存", RiskLevel.Safe, BaseFolder.Windows),

        // Docker（高风险）
        ("Docker 镜像/容器", @"Docker", "Docker Desktop 所有数据（镜像+容器+卷）", RiskLevel.Danger, BaseFolder.LocalAppData),
    ];

    /// <summary>
    /// 扫描所有缓存，返回列表
    /// </summary>
    public static List<CacheItem> ScanAll(
        IProgress<(int current, int total, string name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CacheItem>();
        var ctx = new ScanContext();
        int total = KnownCaches.Length + CondaPkgsPaths.Length;
        int current = 0;

        foreach (var (name, relativePath, desc, risk, baseFolder) in KnownCaches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            progress?.Report((current, total, name));

            var item = new CacheItem
            {
                Name = name,
                Desc = desc,
                Risk = risk
            };

            try
            {
                // pip 特殊处理：通过命令获取缓存路径
                if (name == "pip 缓存")
                {
                    var pipPath = ResolvePipCache();
                    if (pipPath != null)
                    {
                        item.Path = pipPath;
                        item.Exists = Directory.Exists(pipPath);
                        if (item.Exists) item.SizeBytes = SumFiles(pipPath);
                    }
                    else
                    {
                        item.Path = "";
                    }
                    items.Add(item);
                    continue;
                }

                // 通用路径解析
                var resolved = ResolvePath(relativePath, baseFolder, ctx);
                if (resolved != null)
                {
                    item.Path = resolved;
                    item.Exists = Directory.Exists(resolved);
                    if (item.Exists)
                    {
                        // 缩略图缓存仅计算 thumbcache_* 文件（避免先全扫再覆盖的双扫问题）
                        item.SizeBytes = name == "Windows 缩略图缓存"
                            ? SumFiles(resolved, "thumbcache_*", SearchOption.TopDirectoryOnly)
                            : SumFiles(resolved);
                    }
                }
                else
                {
                    item.Path = "";
                }

                if (name == "Windows 缩略图缓存" && item.Exists && item.SizeBytes == 0)
                    item.Exists = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描 {name} 失败: {ex.Message}");
                item.Path = "";
            }

            items.Add(item);
        }

        // Conda pkgs 单独扫描
        ScanCondaPkgs(items, progress, ref current, ref total, cancellationToken);

        return items;
    }

    /// <summary>
    /// 通用路径解析：根据基准目录类型拼接完整路径
    /// </summary>
    private static string? ResolvePath(string relativePath, BaseFolder baseFolder, ScanContext ctx)
    {
        return baseFolder switch
        {
            BaseFolder.LocalAppData => Path.Combine(ctx.LocalAppData, relativePath),
            BaseFolder.AppData => Path.Combine(ctx.AppData, relativePath),
            BaseFolder.Windows =>
                Directory.Exists(Path.Combine(ctx.WindowsDir, relativePath))
                    ? Path.Combine(ctx.WindowsDir, relativePath)
                    : null,
            _ => null
        };
    }

    /// <summary>
    /// 获取 pip 缓存路径（带安全校验）
    /// </summary>
    private static string? ResolvePipCache()
    {
        try
        {
            using var proc = StartPipProcess("cache dir");
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(output))
                return null;

            if (!Directory.Exists(output))
                return null;

            // 安全校验：返回路径应包含 pip/cache 相关关键词
            var normalized = output.Replace('/', '\\').ToLowerInvariant();
            bool isValid = ValidPipCacheKeywords.Any(k => normalized.Contains(k));
            return isValid ? output : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"获取 pip 缓存路径失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 扫描 Conda pkgs 目录
    /// </summary>
    private static void ScanCondaPkgs(
        List<CacheItem> items,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        foreach (var condaPath in CondaPkgsPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            if (Directory.Exists(condaPath))
            {
                progress?.Report((current, total, "Conda 缓存"));
                items.Add(new CacheItem
                {
                    Name = "Conda 包缓存",
                    Path = condaPath,
                    Desc = "Anaconda/Miniconda 包缓存目录",
                    Risk = RiskLevel.Safe,
                    Exists = true,
                    SizeBytes = SumFiles(condaPath)
                });
                break; // 找到一个就够了
            }
        }
    }

    /// <summary>
    /// 计算目录下匹配文件的总大小（统一替代原 GetDirectorySize + GetThumbCacheSize）
    /// </summary>
    public static long SumFiles(string path, string pattern = "*", SearchOption searchOption = SearchOption.AllDirectories)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            var dir = new DirectoryInfo(path);
            return dir.EnumerateFiles(pattern, searchOption)
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"计算目录大小失败 {path}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 格式化文件大小显示
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < SizeUnits.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:F2} {SizeUnits[unitIndex]}";
    }

    /// <summary>
    /// 清理指定缓存项，返回实际释放的字节数
    /// </summary>
    public static long CleanItem(CacheItem item, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!item.Exists || string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
            return 0;

        // 路径安全校验
        if (!IsPathSafeForCleaning(item.Path))
        {
            Debug.WriteLine($"路径安全校验失败，跳过清理: {item.Path}");
            return 0;
        }

        long freed;

        switch (item.Name)
        {
            case "pip 缓存":
                freed = CleanPipCache(item, progress);
                break;
            case "Windows 临时文件":
                freed = CleanTempFiles(item.Path, cancellationToken);
                break;
            case "Windows 缩略图缓存":
                freed = CleanFiles(item.Path, "thumbcache_*");
                break;
            default:
                freed = CleanDirectory(item.Path, cancellationToken);
                break;
        }

        if (freed > 0)
        {
            item.SizeBytes = Math.Max(0, item.SizeBytes - freed);
            if (item.SizeBytes == 0) item.Exists = false;
        }

        return freed;
    }

    /// <summary>
    /// 校验路径是否属于用户目录范围（防止路径遍历攻击）
    /// </summary>
    private static bool IsPathSafeForCleaning(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            if (fullPath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                return true;
            if (fullPath.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                return true;
            if (fullPath.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
                return true;
            if (fullPath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
                return true;
            // Conda 安装在 C:\ 根目录
            if (fullPath.Contains("conda", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 清理 pip 缓存
    /// </summary>
    private static long CleanPipCache(CacheItem item, IProgress<string>? progress)
    {
        long sizeBefore = item.SizeBytes;
        try
        {
            progress?.Report("正在执行 pip cache purge...");
            using var proc = StartPipProcess("cache purge");
            proc?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"pip cache purge 失败: {ex.Message}");
        }

        long sizeAfter = SumFiles(item.Path);
        return Math.Max(0, sizeBefore - sizeAfter);
    }

    /// <summary>
    /// 清理临时文件（仅删除超过7天的）
    /// </summary>
    private static long CleanTempFiles(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return 0;
        long freed = 0;
        var cutoff = DateTime.Now.AddDays(-7);

        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (file.LastWriteTime < cutoff)
                    {
                        freed += file.Length;
                        file.Delete();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除临时文件失败 {file.FullName}: {ex.Message}");
                }
            }

            // 从深到浅清理空目录
            foreach (var subDir in dir.EnumerateDirectories("*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.FullName.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!subDir.EnumerateFileSystemInfos().Any())
                        subDir.Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除空目录失败 {subDir.FullName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"清理临时文件失败: {ex.Message}");
        }

        return freed;
    }

    /// <summary>
    /// 删除匹配的文件并累计释放字节数（统一替代重复的删除循环）
    /// </summary>
    private static long CleanFiles(string path, string pattern)
    {
        if (!Directory.Exists(path)) return 0;
        long freed = 0;

        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles(pattern))
            {
                try
                {
                    freed += file.Length;
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除文件失败 {file.FullName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"清理 {path} 失败: {ex.Message}");
        }

        return freed;
    }

    /// <summary>
    /// 清理整个目录内容，保留目录本身
    /// 单次遍历累计释放字节数（替代原来的三次目录扫描）
    /// </summary>
    private static long CleanDirectory(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return 0;
        long freed = 0;

        try
        {
            var dir = new DirectoryInfo(path);

            // 删除文件并累计释放空间
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    freed += file.Length;
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除文件失败 {file.FullName}: {ex.Message}");
                }
            }

            // 从深到浅删除子目录
            foreach (var subDir in dir.EnumerateDirectories("*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.FullName.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    subDir.Delete(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除目录失败 {subDir.FullName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"清理目录失败 {path}: {ex.Message}");
        }

        return freed;
    }

    /// <summary>
    /// 创建 pip 进程（统一入口，避免重复 ProcessStartInfo 配置）
    /// </summary>
    private static Process? StartPipProcess(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pip",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动 pip 命令失败: {ex.Message}");
            return null;
        }
    }
}
