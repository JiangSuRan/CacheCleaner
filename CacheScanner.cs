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
    Windows,
    UserProfile
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

    // 匹配缓存目录的名称（不区分大小写）
    private static readonly HashSet<string> CacheDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", "caches", "cached", "cache2",
        "temp", "tmp",
        "log", "logs",
        "gpucache", "code cache",
        "crashdumps", "minidump",
        "blob_storage", "session storage"
    };

    // 跳过的根目录（不扫描系统目录）
    private static readonly HashSet<string> SkipRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)",
        @"C:\ProgramData", @"C:\System Volume Information",
        @"C:\$Recycle.Bin", @"C:\$Windows.~WS", @"C:\$Windows.~BT",
        @"C:\Recovery", @"C:\Intel", @"C:\PerfLogs"
    };

    // 跳过的子目录名（大型非缓存目录）
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "node_modules", "venv", ".venv", "env", ".env",
        "bin", "obj", "build", "dist", "target", "out",
        "site-packages", "lib", ".vs", ".vscode", ".idea",
        "packages", ".nuget"
    };

    // 最小报告阈值：50 MB
    private const long MinReportSize = 50 * 1024 * 1024;
    // 最大扫描深度
    private const int MaxDepth = 8;

    // Conda 可能的安装路径
    private static readonly string[] CondaPkgsPaths =
    [
        @"C:\anaconda3\pkgs",
        @"C:\miniconda3\pkgs",
        @"C:\Miniconda3\pkgs",
        @"D:\conda\pkgs"
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

        // AI 编辑器
        ("Cursor 缓存", @"Cursor\Cache", "Cursor AI 编辑器缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("Cursor 日志", @"Cursor\logs", "Cursor AI 编辑器日志", RiskLevel.Safe, BaseFolder.AppData),
        ("Trae CN 缓存", @"Trae CN\Cache", "字节跳动 Trae IDE 缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("Trae CN 日志", @"Trae CN\logs", "字节跳动 Trae IDE 日志", RiskLevel.Safe, BaseFolder.AppData),
        ("Positron 缓存", @"Positron\Cache", "Positron R IDE 缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("Qoder 缓存", @"Qoder\Cache", "Qoder AI IDE 缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("CherryStudio 缓存", @"CherryStudio\Cache", "CherryStudio AI 客户端缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("Claude Desktop 缓存", @"Claude\Cache", "Claude Desktop 缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("Copilot 缓存", @"copilot", "GitHub Copilot 插件缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

        // 视频/娱乐
        ("bilibili 缓存", @"bilibili\Cache", "哔哩哔哩客户端缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("bilibili 日志", @"bilibili\logs", "哔哩哔哩客户端日志", RiskLevel.Safe, BaseFolder.AppData),

        // 文档/翻译
        ("Obsidian 缓存", @"obsidian\Cache", "Obsidian 笔记缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("百度翻译缓存", @"BdTranslateClient\Cache", "百度翻译客户端缓存", RiskLevel.Safe, BaseFolder.AppData),
        ("doc2x 缓存", @"doc2x\Cache", "doc2x 文档转换缓存", RiskLevel.Safe, BaseFolder.AppData),

        // 开发工具
        ("NuGet 包缓存", @".nuget\packages", ".NET NuGet 包缓存", RiskLevel.Safe, BaseFolder.UserProfile),
        ("NuGet HTTP 缓存", @"NuGet\v3-cache", "NuGet HTTP 缓存", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("R 编译缓存", @"R\cache", "R 包编译缓存", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("node-gyp 缓存", @"node-gyp\Cache", "Node.js 原生模块编译缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

        // 系统缓存
        ("D3D 着色器缓存", @"D3DSCache", "Direct3D 着色器缓存，清理后自动重建", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("崩溃转储", @"CrashDumps", "应用程序崩溃转储文件", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("PowerToys 更新缓存", @"Microsoft\PowerToys\Updates", "PowerToys 更新包", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("Electron 安装临时文件", @"SquirrelTemp", "Electron 应用安装临时文件", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("腾讯日志", @"Tencent\Logs", "腾讯软件日志文件", RiskLevel.Safe, BaseFolder.AppData),
        ("GitHub Desktop 更新包", @"GitHubDesktop\packages", "GitHub Desktop 更新包缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

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

        // Electron 应用更新缓存
        ScanUpdaterCaches(items, ctx, progress, ref current, ref total, cancellationToken);

        // GitHub Desktop 旧版本
        ScanGithubDesktopOldVersions(items, ctx, progress, ref current, ref total, cancellationToken);

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
            BaseFolder.UserProfile => Path.Combine(ctx.UserProfile, relativePath),
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
            case "GitHub Desktop 旧版本":
                freed = CleanGithubDesktopOldVersions(item.Path, cancellationToken);
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
            // 自动发现的多用户路径
            if (fullPath.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase))
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
    /// 扫描 Electron 应用更新缓存（*-updater 目录）
    /// </summary>
    private static void ScanUpdaterCaches(
        List<CacheItem> items,
        ScanContext ctx,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ctx.LocalAppData)) return;

        try
        {
            var updaterDirs = Directory.GetDirectories(ctx.LocalAppData, "*-updater");
            if (updaterDirs.Length == 0) return;

            total += updaterDirs.Length;

            foreach (var dir in updaterDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                var displayName = Path.GetFileName(dir).Replace("-updater", "");
                progress?.Report((current, total, displayName + " 更新缓存"));
                items.Add(new CacheItem
                {
                    Name = $"{displayName} 更新缓存",
                    Path = dir,
                    Desc = "应用更新包缓存，清理后不影响使用",
                    Risk = RiskLevel.Safe,
                    Exists = true,
                    SizeBytes = SumFiles(dir)
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"扫描更新缓存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 扫描 GitHub Desktop 旧版本（仅保留最新版本）
    /// </summary>
    private static void ScanGithubDesktopOldVersions(
        List<CacheItem> items,
        ScanContext ctx,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        var ghDir = Path.Combine(ctx.LocalAppData, "GitHubDesktop");
        if (!Directory.Exists(ghDir)) return;

        try
        {
            var versions = Directory.GetDirectories(ghDir, "app-*")
                .OrderByDescending(d => d)
                .ToList();

            if (versions.Count <= 1) return;

            total++;
            current++;
            progress?.Report((current, total, "GitHub Desktop 旧版本"));

            long totalSize = 0;
            for (int i = 1; i < versions.Count; i++)
                totalSize += SumFiles(versions[i]);

            items.Add(new CacheItem
            {
                Name = "GitHub Desktop 旧版本",
                Path = ghDir,
                Desc = "GitHub Desktop 旧版本文件，清理后不影响当前版本",
                Risk = RiskLevel.Safe,
                Exists = true,
                SizeBytes = totalSize
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"扫描 GitHub Desktop 旧版本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理 GitHub Desktop 旧版本（保留最新版本）
    /// </summary>
    private static long CleanGithubDesktopOldVersions(string path, CancellationToken cancellationToken)
    {
        try
        {
            var versions = Directory.GetDirectories(path, "app-*")
                .OrderByDescending(d => d)
                .ToList();

            if (versions.Count <= 1) return 0;

            long freed = 0;
            for (int i = 1; i < versions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                freed += SumFiles(versions[i]);
                try { Directory.Delete(versions[i], true); }
                catch (Exception ex) { Debug.WriteLine($"删除旧版本失败 {versions[i]}: {ex.Message}"); }
            }
            return freed;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Phase 2：自动发现 C 盘用户目录中的缓存目录
    /// </summary>
    public static List<CacheItem> ScanAutoDiscovered(
        List<CacheItem> knownItems,
        IProgress<(int dirsScanned, string currentPath)>? progress = null,
        CancellationToken ct = default)
    {
        var knownPaths = BuildKnownPathsSet(knownItems);
        var results = new List<CacheItem>();
        int dirsScanned = 0;

        // 扫描 C:\Users 下所有用户目录
        var usersDir = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + "Users";
        if (!Directory.Exists(usersDir)) return results;

        var scanRoots = new List<string>();
        try
        {
            foreach (var userDir in Directory.EnumerateDirectories(usersDir))
            {
                // 跳过 Public 等非用户目录的噪音
                var name = Path.GetFileName(userDir);
                if (name.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                    continue;
                scanRoots.Add(userDir);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"枚举用户目录失败: {ex.Message}");
            return results;
        }

        foreach (var root in scanRoots)
        {
            ct.ThrowIfCancellationRequested();
            WalkDirectory(root, 0, knownPaths, results, ref dirsScanned, progress, ct);
        }

        return results;
    }

    /// <summary>
    /// 递归遍历目录树，匹配缓存模式
    /// </summary>
    private static void WalkDirectory(
        string path,
        int depth,
        HashSet<string> knownPaths,
        List<CacheItem> results,
        ref int dirsScanned,
        IProgress<(int dirsScanned, string currentPath)>? progress,
        CancellationToken ct)
    {
        if (depth > MaxDepth) return;

        string[] subDirs;
        try
        {
            subDirs = Directory.GetDirectories(path);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (Exception ex)
        {
            Debug.WriteLine($"无法访问目录 {path}: {ex.Message}");
            return;
        }

        foreach (var dir in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            dirsScanned++;

            var dirName = Path.GetFileName(dir);

            // 跳过黑名单目录
            if (SkipDirNames.Contains(dirName)) continue;

            // 匹配缓存模式
            if (CacheDirNames.Contains(dirName))
            {
                // 去重：已知缓存路径不再报告
                try
                {
                    var normalized = Path.GetFullPath(dir).TrimEnd('\\');
                    if (knownPaths.Contains(normalized)) continue;
                }
                catch { continue; }

                // 计算大小
                long size = SumFiles(dir);
                if (size < MinReportSize) continue;

                // 路径安全校验
                if (!IsPathSafeForCleaning(dir)) continue;

                results.Add(new CacheItem
                {
                    Name = GenerateAutoName(dirName, dir),
                    Path = dir,
                    Desc = GenerateAutoDesc(dirName, dir),
                    Risk = AssessAutoRisk(dir, dirName),
                    Exists = true,
                    SizeBytes = size
                });

                // 匹配到缓存目录后不继续递归
                continue;
            }

            // 定期报告进度
            if (dirsScanned % 50 == 0)
                progress?.Report((dirsScanned, dir));

            // 递归
            WalkDirectory(dir, depth + 1, knownPaths, results, ref dirsScanned, progress, ct);
        }
    }

    private static HashSet<string> BuildKnownPathsSet(List<CacheItem> knownItems)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in knownItems)
        {
            if (!string.IsNullOrEmpty(item.Path) && item.Exists)
            {
                try { set.Add(Path.GetFullPath(item.Path).TrimEnd('\\')); }
                catch { }
            }
        }
        return set;
    }

    private static string GenerateAutoName(string dirName, string fullPath)
    {
        var parentName = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? "未知应用";
        var typeSuffix = dirName.ToLowerInvariant() switch
        {
            "cache" or "caches" or "cached" or "cache2" => "缓存",
            "temp" or "tmp" => "临时文件",
            "log" or "logs" => "日志",
            "gpucache" => "GPU 缓存",
            "code cache" => "代码缓存",
            "crashdumps" or "minidump" => "崩溃转储",
            _ => "缓存"
        };
        return $"{parentName} {typeSuffix}";
    }

    private static string GenerateAutoDesc(string dirName, string fullPath)
    {
        var parentName = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? "未知应用";
        var lower = dirName.ToLowerInvariant();
        if (lower is "temp" or "tmp")
            return $"自动发现的 {parentName} 临时文件目录";
        if (lower is "log" or "logs")
            return $"自动发现的 {parentName} 日志文件";
        return $"自动发现的 {parentName} 缓存目录";
    }

    private static RiskLevel AssessAutoRisk(string fullPath, string dirName)
    {
        var lower = fullPath.ToLowerInvariant();
        if (lower.Contains(@"\docker\") || lower.Contains(@"\wsl\"))
            return RiskLevel.Danger;
        if (dirName is "temp" or "tmp")
            return RiskLevel.Warn;
        return RiskLevel.Safe;
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
