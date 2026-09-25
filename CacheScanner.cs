using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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
/// 清理结果：释放字节数 + 各类失败/待处理计数。
/// 取代原来只返回 long 的做法，让 UI 能向用户展示失败明细，而不是永远显示「清理完成」。
/// </summary>
public readonly record struct CleanResult(
    long FreedBytes,
    int DeletedCount,
    int LockedCount,            // IOException：文件被占用（浏览器/IDE 运行时常见）
    int PermissionDeniedCount,  // UnauthorizedAccessException：权限不足
    int OtherFailureCount,
    int PendingRebootCount)     // MoveFileEx 登记重启删除成功的文件数（P1-2）
{
    public int TotalFailures => LockedCount + PermissionDeniedCount + OtherFailureCount;

    public static CleanResult operator +(CleanResult a, CleanResult b) => new(
        a.FreedBytes + b.FreedBytes,
        a.DeletedCount + b.DeletedCount,
        a.LockedCount + b.LockedCount,
        a.PermissionDeniedCount + b.PermissionDeniedCount,
        a.OtherFailureCount + b.OtherFailureCount,
        a.PendingRebootCount + b.PendingRebootCount);
}

/// <summary>
/// 路径基准目录类型
/// </summary>
internal enum BaseFolder
{
    LocalAppData,
    AppData,
    Windows,
    UserProfile,
    ProgramData,
    DriveRoot   // C 盘根目录（回收站等系统级位置）
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
    public string ProgramData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
}

/// <summary>
/// 自动扫描 C 盘缓存
/// </summary>
public static class CacheScanner
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    // 系统盘根（如 C:\）：运行时从系统目录推导，Windows 装在非 C 盘的机器上同样可用。
    // 注意静态初始化顺序：本字段必须先于 SkipRoots/AllowedSystemPaths 声明。
    internal static readonly string SysRoot = InitSysRoot();

    // 系统盘盘符（如 'C'），用于 vssadmin 等命令行参数
    internal static readonly char SysDriveLetter = SysRoot[0];

    /// <summary>
    /// 预览（干跑）模式：只统计将释放的量，不删除任何文件、不执行任何命令、不改动服务状态
    /// </summary>
    public static bool DryRun { get; set; }

    private static string InitSysRoot()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        return string.IsNullOrEmpty(root) ? @"C:\" : root.ToUpperInvariant();
    }

    // 匹配缓存目录的名称（不区分大小写）
    private static readonly HashSet<string> CacheDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 绝不加入 Chromium 的 "Session Storage"（存站点会话/登录态）和 "blob_storage"
        // （未提交的 Blob 数据）：名字像缓存但属于用户数据，自动发现按目录名匹配，
        // 一旦命中并清理会直接丢登录态
        "cache", "caches", "cached", "cache2",
        "temp", "tmp",
        "log", "logs",
        "gpucache", "code cache",
        "crashdumps", "minidump"
    };

    // 跳过的根目录（不扫描系统目录）；盘符随系统盘运行时推导
    private static readonly HashSet<string> SkipRoots = BuildSkipRoots();

    private static HashSet<string> BuildSkipRoots()
    {
        var root = SysRoot.TrimEnd('\\');
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(root, "Windows"),
            Path.Combine(root, "Program Files"),
            Path.Combine(root, "Program Files (x86)"),
            Path.Combine(root, "ProgramData"),
            Path.Combine(root, "System Volume Information"),
            Path.Combine(root, "$Recycle.Bin"),
            Path.Combine(root, "$Windows.~WS"),
            Path.Combine(root, "$Windows.~BT"),
            Path.Combine(root, "Recovery"),
            Path.Combine(root, "Intel"),
            Path.Combine(root, "PerfLogs")
        };
    }

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

    // Conda 安装位置不再硬编码：运行时经 conda info --base 与常见位置动态发现（见 GetCondaPkgsCandidates）

    // 合法的 pip 缓存路径特征
    private static readonly string[] ValidPipCacheKeywords = ["pip", "cache", "pypa"];

    // 声明式清理规则已外置：默认规则见 rules.default.json（内嵌资源），
    // 支持 exe 同目录 / %LOCALAPPDATA%\CacheCleaner\rules.user.json 侧车增改，无需重新编译。
    // kind=special 的特殊项（pip/DNS/还原点/升级残留）在 ScanAll 与 CleanItem 中按名称分发。

    /// <summary>
    /// 扫描所有缓存，返回列表
    /// </summary>
    public static List<CacheItem> ScanAll(
        IProgress<(int current, int total, string name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CacheItem>();
        var ctx = new ScanContext();
        var condaCandidates = GetCondaPkgsCandidates();
        int total = CleaningRules.All.Count + condaCandidates.Count;
        int current = 0;

        foreach (var rule in CleaningRules.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            progress?.Report((current, total, rule.Name));

            var item = new CacheItem
            {
                Name = rule.Name,
                Desc = rule.Desc,
                Risk = rule.Risk
            };

            try
            {
                // 特殊项：无法用「基准目录+相对路径」表达（外部命令/动态解析），按名称分发
                if (rule.Kind == "special")
                {
                    switch (rule.Name)
                    {
                        case "pip 缓存":
                        {
                            var pipPath = ResolvePipCache();
                            if (pipPath != null)
                            {
                                item.Path = pipPath;
                                item.Exists = Directory.Exists(pipPath);
                                if (item.Exists) item.SizeBytes = SumFiles(pipPath);
                            }
                            break;
                        }
                        case "DNS 解析缓存":
                            item.Path = "（命令式清理）";
                            item.Exists = true;
                            break;
                        case "系统还原点（卷影副本）":
                        {
                            var (usedBytes, ok) = QueryShadowStorageUsed();
                            item.Path = "（命令式清理）";
                            item.Exists = ok;
                            item.SizeBytes = usedBytes;
                            break;
                        }
                        case "Windows 升级残留":
                        {
                            long remnantBytes = 0;
                            foreach (var remnant in new[]
                                     {
                                         Path.Combine(SysRoot, "Windows.old"),
                                         Path.Combine(SysRoot, "$GetCurrent"),
                                         Path.Combine(SysRoot, "ESD")
                                     })
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (Directory.Exists(remnant)) remnantBytes += SumFiles(remnant);
                            }
                            item.Path = "（命令式清理）";
                            item.Exists = remnantBytes > 0;
                            item.SizeBytes = remnantBytes;
                            break;
                        }
                    }

                    items.Add(item);
                    continue;
                }

                // 通用解析：目录（path）与单文件（file）两类。
                // file 类型不走 ResolvePath——其中的目录存在性预检会误杀文件路径。
                string? resolved;
                if (rule.Kind == "file")
                {
                    resolved = rule.Base switch
                    {
                        BaseFolder.Windows => Path.Combine(ctx.WindowsDir, rule.Path),
                        BaseFolder.LocalAppData => Path.Combine(ctx.LocalAppData, rule.Path),
                        BaseFolder.AppData => Path.Combine(ctx.AppData, rule.Path),
                        BaseFolder.UserProfile => Path.Combine(ctx.UserProfile, rule.Path),
                        BaseFolder.ProgramData => Path.Combine(ctx.ProgramData, rule.Path),
                        _ => Path.Combine(SysRoot, rule.Path)
                    };
                }
                else
                {
                    resolved = ResolvePath(rule.Path, rule.Base, ctx);
                }

                if (resolved != null)
                {
                    item.Path = resolved;
                    item.Exists = rule.Kind == "file" ? File.Exists(resolved) : Directory.Exists(resolved);
                    if (item.Exists)
                    {
                        if (rule.Kind == "file")
                        {
                            item.SizeBytes = new FileInfo(resolved).Length;
                        }
                        else if (rule.SkipSize)
                        {
                            // WinSxS 等位置：硬链接虚高且全量遍历极慢，跳过统计
                            item.SizeBytes = 0;
                        }
                        else if (rule.FilesPatterns is { Count: > 0 })
                        {
                            // 指定文件模式的项（缩略图/图标缓存、CBS 日志）：只统计匹配文件
                            foreach (var pattern in rule.FilesPatterns)
                                item.SizeBytes += SumFiles(resolved, pattern, SearchOption.TopDirectoryOnly);
                        }
                        else
                        {
                            item.SizeBytes = SumFiles(resolved);
                        }
                    }
                }

                // 文件/模式匹配项统计为 0 时隐藏，避免列表空行
                if ((rule.Kind == "file" || rule.FilesPatterns is { Count: > 0 }) && item.Exists && item.SizeBytes == 0)
                    item.Exists = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描 {rule.Name} 失败: {ex.Message}");
                item.Path = "";
            }

            items.Add(item);
        }

        // Conda pkgs 单独扫描（动态发现安装位置）
        ScanCondaPkgs(items, condaCandidates, progress, ref current, ref total, cancellationToken);

        // Electron 应用更新缓存
        ScanUpdaterCaches(items, ctx, progress, ref current, ref total, cancellationToken);

        // GitHub Desktop 旧版本
        ScanGithubDesktopOldVersions(items, ctx, progress, ref current, ref total, cancellationToken);

        // 微信4.0 缓存（xwechat_files\wxid_*\cache，动态多用户路径）
        ScanWeChatCache(items, ctx, progress, ref current, ref total, cancellationToken);

        // Adobe 全家桶自动恢复快照（* Settings\*\DataRecovery，AI 重度用户可达数 GB）
        ScanAdobeRecoveryCaches(items, ctx, progress, ref current, ref total, cancellationToken);

        // 浏览器多 Profile 缓存（Edge/Chrome 除 Default 外的 Profile N，多账号场景的常见盲区）
        ScanBrowserProfileCaches(items, ctx, progress, ref current, ref total, cancellationToken);

        // 豆包缓存（仅 cache 类子目录，替代旧的整目录删除，保住登录态和 IndexedDB）
        ScanDoubaoCaches(items, ctx, progress, ref current, ref total, cancellationToken);

        // Electron 系应用缓存子目录统一扫描（Cache/CachedData/Code Cache/GPUCache/扩展包缓存等）
        ScanElectronApps(items, ctx, progress, ref current, ref total, cancellationToken);

        // Firefox 各 Profile 磁盘缓存
        ScanFirefoxCaches(items, ctx, progress, ref current, ref total, cancellationToken);

        // UWP 应用包安全缓存族（AC\INetCache / AC\Temp / TempState）
        ScanUwpCacheFamilies(items, ctx, progress, ref current, ref total, cancellationToken);

        // AI CLI 自更新遗留的旧版本目录（保留最新）
        ScanCodexOldVersions(items, ctx, progress, ref current, ref total, cancellationToken);

        // 遗留性能追踪会话（WPR 录制/手动内核追踪），持续写盘的经典增长源
        ScanLeftoverTraces(items, progress, ref current, ref total, cancellationToken);

        // Docker/WSL：prune 可回收量 + vhdx 虚拟磁盘压缩（取代旧整删危险项）
        ScanDockerWsl(items, progress, ref current, ref total, cancellationToken);

        // 未收录 agent/工具目录探测：家目录下的大体积 dot-dir 报告（看得见比清得掉更重要）
        ScanUnknownAgentHomes(items, ctx, progress, ref current, ref total, cancellationToken);

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
            BaseFolder.ProgramData =>
                Directory.Exists(Path.Combine(ctx.ProgramData, relativePath))
                    ? Path.Combine(ctx.ProgramData, relativePath)
                    : null,
            BaseFolder.DriveRoot =>
                Directory.Exists(Path.Combine(SysRoot, relativePath))
                    ? Path.Combine(SysRoot, relativePath)
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
    /// 收集 Conda pkgs 的候选路径：PATH 中有 conda 时 info --base 给出真实安装根，
    /// 否则枚举用户级/系统级常见位置；盘符随系统盘推导，不再硬编码
    /// </summary>
    private static List<string> GetCondaPkgsCandidates()
    {
        var candidates = new List<string>();

        var (ok, output) = RunCommandOutput("conda", "info --base", 10_000);
        if (ok)
        {
            var baseDir = output.Trim().Trim('"');
            if (baseDir.Length > 0 && Directory.Exists(baseDir))
                candidates.Add(Path.Combine(baseDir, "pkgs"));
        }

        foreach (var baseDir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     SysRoot.TrimEnd('\\')
                 })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            foreach (var name in new[] { "anaconda3", "miniconda3", "Miniconda3", "Anaconda3" })
                candidates.Add(Path.Combine(baseDir, name, "pkgs"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 扫描 Conda pkgs 目录（动态发现安装位置，命中第一个存在的即止）
    /// </summary>
    private static void ScanCondaPkgs(
        List<CacheItem> items,
        List<string> candidates,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        foreach (var condaPath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            if (!Directory.Exists(condaPath)) continue;

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
    public static CleanResult CleanItem(CacheItem item, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        // 命令式清理项没有可校验的真实路径（Path 为「（命令式清理）」占位符），必须在
        // 存在性门卫之前分发，否则 File/Directory 检查永远失败，清理函数沦为死代码
        if (item.Name == "DNS 解析缓存")
            return FlushDnsCache();

        if (item.Name == "系统还原点（卷影副本）")
            return ShrinkShadowStorage(item, progress);

        if (item.Name == "Windows 升级残留")
            return CleanUpgradeRemnants(progress);

        // 遗留追踪会话：停止 WPR/手动内核追踪，终止持续写盘
        if (item.Name == "遗留性能追踪会话")
            return CleanLeftoverTraces();

        // 未收录工具目录：仅报告占用，语义确认前绝不清理
        if (item.Name.StartsWith("未收录工具目录"))
            return default;

        // Docker/WSL：prune 与 vhdx 压缩均为官方再生性操作，取代旧的整删危险项
        if (item.Name == "Docker 未使用数据")
            return CleanDockerPrune(progress);

        if (item.Name == "WSL/Docker 虚拟磁盘")
            return CleanWslVhdx(progress, cancellationToken);

        // UWP 缓存族项的 Path 是 Packages 根目录，实际按名称携带的子族逐包清理
        if (item.Name.StartsWith("UWP "))
            return CleanUwpCacheFamily(item, cancellationToken);

        // AI CLI 自更新遗留目录（保留最新版本）；GitHub Desktop 旧版本走下方精确匹配
        if (item.Name.StartsWith("Codex") && item.Name.EndsWith(" 旧版本"))
            return CleanKeepNewest(item.Path, cancellationToken);

        // 规则驱动检查：进程守卫（浏览器/微信等运行中其缓存必然被锁，直接跳过并留痕）
        var rule = CleaningRules.Find(item.Name);
        if (rule?.GuardProcesses is { Count: > 0 })
        {
            var running = rule.GuardProcesses.Where(HasRunningProcess).ToList();
            if (running.Count > 0)
            {
                progress?.Report($"跳过 {item.Name}（{string.Join("、", running)} 正在运行）");
                CleanLog.NoteFailure($"进程运行中跳过 [{string.Join("、", running)}]: {item.Path}");
                return default;
            }
        }

        // 规则声明只读报告（如 VS 安装缓存）：绝不清理
        if (rule?.Clean == "reportOnly")
            return default;

        // 存在性门卫：目录或单文件（如 C:\Windows\Memory.dmp）均放行
        if (!item.Exists || string.IsNullOrEmpty(item.Path) ||
            (!Directory.Exists(item.Path) && !File.Exists(item.Path)))
            return default;

        // 路径安全校验
        if (!IsPathSafeForCleaning(item.Path))
        {
            Debug.WriteLine($"路径安全校验失败，跳过清理: {item.Path}");
            return default;
        }

        // 规则声明的文件模式清理（thumbcache_*/iconcache_*、CbsPersist_* 等）
        if (rule?.FilesPatterns is { Count: > 0 })
        {
            CleanResult patternTotal = default;
            foreach (var pattern in rule.FilesPatterns)
            {
                patternTotal += rule.MinAgeDays > 0
                    ? CleanOldFiles(item.Path, pattern, rule.MinAgeDays, rule.Recursive)
                    : CleanFiles(item.Path, pattern, rule.Recursive);
            }

            if (patternTotal.FreedBytes > 0 && !DryRun)
            {
                item.SizeBytes = Math.Max(0, item.SizeBytes - patternTotal.FreedBytes);
                if (item.SizeBytes == 0) item.Exists = false;
            }
            return patternTotal;
        }

        // 规则声明的命令式清理（官方 prune：pnpm store prune / go clean -modcache 等）——
        // 优先走官方命令而非裸删，保证内容寻址存储的引用安全；释放量按目标目录前后差值计
        if (rule?.Clean == "command" && !string.IsNullOrWhiteSpace(rule.Command))
        {
            if (DryRun)
            {
                progress?.Report($"预览模式：跳过执行 {rule.Command}");
                return new CleanResult(0, 1, 0, 0, 0, 0);
            }

            long before = Directory.Exists(item.Path) ? SumFiles(item.Path) : 0;
            progress?.Report($"正在执行 {rule.Command} ...");
            // 经 cmd /c 解析：pnpm/conda 等在 Windows 上是 .cmd 垫片而非 exe
            var (ok, output) = RunCommandOutput("cmd", $"/d /s /c \"{rule.Command}\"", rule.CommandTimeoutMs);
            if (!ok)
            {
                CleanLog.NoteFailure($"命令清理失败 [{rule.Command}]: {output.Trim()}");
                return new CleanResult(0, 0, 0, 0, 1, 0);
            }

            long freed = 0;
            if (Directory.Exists(item.Path))
            {
                long after = SumFiles(item.Path);
                freed = Math.Max(0, before - after);
                item.SizeBytes = after;
            }
            return new CleanResult(freed, 1, 0, 0, 0, 0);
        }

        CleanResult result = item.Name switch
        {
            "pip 缓存" => CleanPipCache(item, progress),
            "Windows 临时文件" => CleanTempFiles(item.Path, cancellationToken),
            "系统临时文件" => CleanTempFiles(item.Path, cancellationToken),
            "系统内存转储" => CleanSingleFile(item.Path),
            "GitHub Desktop 旧版本" => CleanGithubDesktopOldVersions(item.Path, cancellationToken),
            "回收站" => CleanRecycleBin(cancellationToken),
            "Windows 更新下载缓存" => CleanWindowsUpdateCache(item.Path, cancellationToken),
            "Delivery Optimization 缓存" => CleanDeliveryOptimizationCache(item),
            "Windows 组件存储 (WinSxS)" => RunComponentCleanup(item, progress, cancellationToken),
            _ => CleanDirectory(item.Path, cancellationToken),
        };

        // 预览模式不改写条目状态，保持列表原样供用户核对
        if (!DryRun && result.FreedBytes > 0)
        {
            item.SizeBytes = Math.Max(0, item.SizeBytes - result.FreedBytes);
            if (item.SizeBytes == 0) item.Exists = false;
        }

        return result;
    }

    /// <summary>
    /// 显式放行的系统级清理路径前缀（精确到具体目录，绝不放开整个盘根）。
    /// 含 Conda 经典系统级安装位置——否则其 pkgs 项会被路径安全校验静默拒绝（清理死代码）。
    /// </summary>
    private static readonly string[] AllowedSystemPaths =
    [
        Path.Combine(SysRoot.TrimEnd('\\'), @"ProgramData\Microsoft\Windows\WER"),
        Path.Combine(SysRoot.TrimEnd('\\'), "$Recycle.Bin"),
        Path.Combine(SysRoot.TrimEnd('\\'), "anaconda3"),
        Path.Combine(SysRoot.TrimEnd('\\'), "miniconda3"),
        Path.Combine(SysRoot.TrimEnd('\\'), "Miniconda3"),
        Path.Combine(SysRoot.TrimEnd('\\'), "Anaconda3")
    ];

    /// <summary>
    /// 校验路径是否属于允许清理的范围（防止路径遍历和跨盘攻击）
    /// 允许的路径必须在 C 盘用户目录范围内，或在显式声明的系统级安全白名单内
    /// </summary>
    private static bool IsPathSafeForCleaning(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);

            // 必须在系统盘
            if (!fullPath.StartsWith(SysRoot, StringComparison.OrdinalIgnoreCase))
                return false;

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
            // 自动发现的多用户路径（系统盘 Users 下的其他用户目录）
            if (fullPath.StartsWith(SysRoot + "Users\\", StringComparison.OrdinalIgnoreCase))
                return true;

            // 显式声明的系统级安全路径（如 C:\ProgramData\Microsoft\Windows\WER）
            foreach (var allowed in AllowedSystemPaths)
            {
                if (fullPath.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(allowed + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

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
    private static CleanResult CleanPipCache(CacheItem item, IProgress<string>? progress)
    {
        long sizeBefore = item.SizeBytes;
        if (DryRun)
        {
            // 预览模式：purge 会清空整个缓存目录，按当前占用计将释放量
            return new CleanResult(sizeBefore, 0, 0, 0, 0, 0);
        }
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

        long freed = Math.Max(0, sizeBefore - SumFiles(item.Path));
        return new CleanResult(freed, 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// 清理临时文件（仅删除超过7天的）
    /// </summary>
    private static CleanResult CleanTempFiles(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return default;
        long freed = 0;
        int deleted = 0, locked = 0, denied = 0, other = 0, pendingReboot = 0;
        var cutoff = DateTime.Now.AddDays(-7);

        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.LastWriteTime >= cutoff) continue;
                try
                {
                    long len = file.Length;
                    if (DryRun)
                    {
                        // 预览模式：不删除，仅统计将释放的量
                        freed += len;
                        deleted++;
                        continue;
                    }
                    file.Delete();
                    // 删除成功才计入释放量：登记重启删除的文件尚未真正释放，不能虚报
                    freed += len;
                    deleted++;
                }
                catch (IOException)
                {
                    // 文件被占用：先尝试登记为重启删除（需管理员），成功计为待重启，否则计为占用失败
                    if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                    else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); CleanLog.NoteFailure($"被占用: {file.FullName}"); }
                }
                catch (UnauthorizedAccessException) { denied++; Debug.WriteLine($"权限不足，跳过 {file.FullName}"); CleanLog.NoteFailure($"权限不足: {file.FullName}"); }
                catch (Exception ex) { other++; Debug.WriteLine($"删除临时文件失败 {file.FullName}: {ex.Message}"); CleanLog.NoteFailure($"其它失败: {file.FullName}"); }
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

        return new CleanResult(freed, deleted, locked, denied, other, pendingReboot);
    }

    /// <summary>
    /// 删除匹配的文件并累计释放字节数（统一替代重复的删除循环）
    /// </summary>
    private static CleanResult CleanFiles(string path, string pattern, bool recursive = false)
    {
        if (!Directory.Exists(path)) return default;
        long freed = 0;
        int deleted = 0, locked = 0, denied = 0, other = 0, pendingReboot = 0;

        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles(pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                try
                {
                    long len = file.Length;
                    if (DryRun)
                    {
                        // 预览模式：不删除，仅统计将释放的量
                        freed += len;
                        deleted++;
                        continue;
                    }
                    file.Delete();
                    // 删除成功才计入释放量：登记重启删除的文件尚未真正释放，不能虚报
                    freed += len;
                    deleted++;
                }
                catch (IOException)
                {
                    // 文件被占用：先尝试登记为重启删除（需管理员），成功计为待重启，否则计为占用失败
                    if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                    else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); CleanLog.NoteFailure($"被占用: {file.FullName}"); }
                }
                catch (UnauthorizedAccessException) { denied++; Debug.WriteLine($"权限不足，跳过 {file.FullName}"); CleanLog.NoteFailure($"权限不足: {file.FullName}"); }
                catch (Exception ex) { other++; Debug.WriteLine($"删除文件失败 {file.FullName}: {ex.Message}"); CleanLog.NoteFailure($"其它失败: {file.FullName}"); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"清理 {path} 失败: {ex.Message}");
        }

        return new CleanResult(freed, deleted, locked, denied, other, pendingReboot);
    }

    /// <summary>
    /// 删除单个文件（如 C:\Windows\Memory.dmp），返回清理结果
    /// </summary>
    private static CleanResult CleanSingleFile(string filePath)
    {
        if (!File.Exists(filePath)) return default;
        try
        {
            long len = new FileInfo(filePath).Length;
            if (DryRun)
            {
                // 预览模式：不删除，仅统计将释放的量
                return new CleanResult(len, 1, 0, 0, 0, 0);
            }
            File.Delete(filePath);
            return new CleanResult(len, 1, 0, 0, 0, 0);
        }
        catch (IOException)
        {
            if (ScheduleDeleteOnReboot(filePath)) return new CleanResult(0, 0, 0, 0, 0, 1);
            Debug.WriteLine($"文件被占用，跳过 {filePath}");
            return new CleanResult(0, 0, 1, 0, 0, 0);
        }
        catch (UnauthorizedAccessException) { Debug.WriteLine($"权限不足，跳过 {filePath}"); return new CleanResult(0, 0, 0, 1, 0, 0); }
        catch (Exception ex) { Debug.WriteLine($"删除文件失败 {filePath}: {ex.Message}"); return new CleanResult(0, 0, 0, 0, 1, 0); }
    }

    /// <summary>
    /// 清理整个目录内容，保留目录本身
    /// 单次遍历累计释放字节数（替代原来的三次目录扫描）
    /// </summary>
    private static CleanResult CleanDirectory(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return default;
        long freed = 0;
        int deleted = 0, locked = 0, denied = 0, other = 0, pendingReboot = 0;

        try
        {
            var dir = new DirectoryInfo(path);

            // 删除文件并累计释放空间
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    long len = file.Length;
                    if (DryRun)
                    {
                        // 预览模式：不删除，仅统计将释放的量
                        freed += len;
                        deleted++;
                        continue;
                    }
                    file.Delete();
                    // 删除成功才计入释放量：登记重启删除的文件尚未真正释放，不能虚报
                    freed += len;
                    deleted++;
                }
                catch (IOException)
                {
                    // 文件被占用：先尝试登记为重启删除（需管理员），成功计为待重启，否则计为占用失败
                    if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                    else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); CleanLog.NoteFailure($"被占用: {file.FullName}"); }
                }
                catch (UnauthorizedAccessException) { denied++; Debug.WriteLine($"权限不足，跳过 {file.FullName}"); CleanLog.NoteFailure($"权限不足: {file.FullName}"); }
                catch (Exception ex) { other++; Debug.WriteLine($"删除文件失败 {file.FullName}: {ex.Message}"); CleanLog.NoteFailure($"其它失败: {file.FullName}"); }
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

        return new CleanResult(freed, deleted, locked, denied, other, pendingReboot);
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
    private static CleanResult CleanGithubDesktopOldVersions(string path, CancellationToken cancellationToken)
    {
        try
        {
            var versions = Directory.GetDirectories(path, "app-*")
                .OrderByDescending(d => d)
                .ToList();

            if (versions.Count <= 1) return default;

            long freed = 0;
            int deleted = 0, other = 0;
            for (int i = 1; i < versions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size = SumFiles(versions[i]);
                if (DryRun)
                {
                    // 预览模式：不删除，仅统计将释放的量
                    freed += size;
                    deleted++;
                    continue;
                }
                try { Directory.Delete(versions[i], true); freed += size; deleted++; }
                catch (Exception ex) { other++; Debug.WriteLine($"删除旧版本失败 {versions[i]}: {ex.Message}"); }
            }
            return new CleanResult(freed, deleted, 0, 0, other, 0);
        }
        catch { return default; }
    }

    /// <summary>
    /// 扫描微信4.0（xwechat）用户缓存目录。
    /// 新版微信数据位于 xwechat_files\wxid_*\ 下：
    ///   - cache：纯缓存（图片/视频预览等），可清理
    ///   - msg / db_storage：聊天消息数据库，绝不可清理（不在本方法范围内）
    /// 路径为动态多用户，需枚举 wxid_* 目录。
    /// </summary>
    private static void ScanWeChatCache(
        List<CacheItem> items,
        ScanContext ctx,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        var wxRoot = Path.Combine(ctx.UserProfile, "xwechat_files");
        if (!Directory.Exists(wxRoot)) return;

        try
        {
            var userDirs = Directory.GetDirectories(wxRoot, "wxid_*");
            if (userDirs.Length == 0) return;

            total += userDirs.Length;
            foreach (var userDir in userDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;

                var cacheDir = Path.Combine(userDir, "cache");
                if (!Directory.Exists(cacheDir)) continue;

                var wxid = Path.GetFileName(userDir);
                progress?.Report((current, total, $"微信缓存({wxid})"));
                items.Add(new CacheItem
                {
                    Name = $"微信缓存 ({wxid})",
                    Path = cacheDir,
                    Desc = "微信4.0 缓存目录（聊天记录在 msg，不受影响）",
                    Risk = RiskLevel.Safe,
                    Exists = true,
                    SizeBytes = SumFiles(cacheDir)
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"扫描微信缓存失败: {ex.Message}");
        }
    }

    // Chromium/Electron 系应用中属于纯缓存的子目录（删除后自动重建，不触碰登录态/IndexedDB/本地存储）
    private static readonly HashSet<string> ChromiumCacheDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache",
        "DawnCache", "DawnWebGPUCache", "DawnGraphiteCache", "GraphiteDawnCache",
        "GrShaderCache", "ShaderCache",
        "gecko_cache", "extensions_crx_cache",
        "CachedData",                  // Electron/VS Code 的 V8 代码缓存，可重建
        "CachedExtensionVSIXs",        // 扩展更新安装包缓存，只进不出（实测可达数 GB）
        "CacheStorage", "ScriptCache"   // 位于 Service Worker 下，只清这两类，绝不动 Database（会丢 PWA 登录）
    };

    // 缓存子目录 → 展示名后缀
    private static string CacheDirSuffix(string dirName) => dirName.ToLowerInvariant() switch
    {
        "cache" => "缓存",
        "code cache" => "代码缓存",
        "gpucache" or "dawncache" or "dawnwebgpucache" or "dawngraphitecache" or "graphitedawncache" => "GPU 缓存",
        "cachestorage" => "SW 缓存",
        "scriptcache" => "SW 脚本缓存",
        "grshadercache" or "shadercache" => "着色器缓存",
        "gecko_cache" => "Gecko 缓存",
        "extensions_crx_cache" or "cachedextensionvsixs" => "扩展包缓存",
        "cacheddata" => "数据缓存",
        "hot_fix" => "热更新缓存",
        _ => "缓存"
    };

    // 单个缓存子目录的最低报告阈值：低于此值不值得在列表里占一行
    private const long MinProfileCacheSize = 10 * 1024 * 1024;

    /// <summary>
    /// 扫描 Adobe 全家桶的自动恢复快照（本次实测的最大盲区：
    /// Roaming\Adobe\* Settings\&lt;语言&gt;\[x64\]DataRecovery 下按会话堆积 .aishm 文档镜像，
    /// AI 重度使用可达数 GB；目录名不含 cache，自动发现无法命中）。
    /// 仅报告达到阈值的目录；清理 = 清空目录内容（关闭对应 Adobe 程序后进行）。
    /// </summary>
    private static void ScanAdobeRecoveryCaches(
        List<CacheItem> items,
        ScanContext ctx,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        var adobeRoot = Path.Combine(ctx.AppData, "Adobe");
        if (!Directory.Exists(adobeRoot)) return;

        try
        {
            // 匹配各 Adobe 程序的设置目录（如 "Adobe Illustrator 29 Settings"）
            var settingsDirs = Directory.GetDirectories(adobeRoot, "* Settings");
            foreach (var settingsDir in settingsDirs)
            {
                // 设置目录下按语言再分（zh_CN 等），恢复数据在 <语言>\x64\DataRecovery 或 <语言>\DataRecovery
                foreach (var langDir in Directory.GetDirectories(settingsDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidates = new[]
                    {
                        Path.Combine(langDir, "x64", "DataRecovery"),
                        Path.Combine(langDir, "DataRecovery")
                    };
                    foreach (var recoveryDir in candidates)
                    {
                        if (!Directory.Exists(recoveryDir)) continue;

                        long size = SumFiles(recoveryDir);
                        if (size < MinProfileCacheSize) continue;   // 太小不值得报告

                        total++;
                        current++;
                        var appName = Path.GetFileName(settingsDir).Replace(" Settings", "");
                        progress?.Report((current, total, $"{appName} 恢复快照"));
                        items.Add(new CacheItem
                        {
                            Name = $"{appName} 恢复快照",
                            Path = recoveryDir,
                            Desc = "自动保存的崩溃恢复镜像，清理后未保存文档的恢复数据将丢失（已保存文件不受影响）",
                            Risk = RiskLevel.Warn,
                            Exists = true,
                            SizeBytes = size
                        });
                        break;  // 同一语言目录下两级 DataRecovery 不会并存，命中即止
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"扫描 Adobe 恢复快照失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 扫描 Edge/Chrome 的多 Profile 缓存。
    /// 已知项只覆盖 Default Profile；多账号用户的 Profile 1/2/3... 是常见盲区。
    /// 每个 Profile 只报告纯缓存子目录，绝不报告 Profile 目录本身（会删掉登录态）。
    /// </summary>
    private static void ScanBrowserProfileCaches(
        List<CacheItem> items,
        ScanContext ctx,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        var browsers = new (string RelRoot, string Label)[]
        {
            (@"Microsoft\Edge", "Edge"),
            (@"Google\Chrome", "Chrome"),
            (@"BraveSoftware\Brave-Browser", "Brave"),
            (@"Vivaldi", "Vivaldi")
        };

        foreach (var (relRoot, label) in browsers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userDataDir = Path.Combine(ctx.LocalAppData, relRoot, "User Data");
            if (!Directory.Exists(userDataDir)) continue;

            try
            {
                // 仅 Profile N 目录；Default 已在 KnownCaches 覆盖，Guest/System Profile 不含用户缓存
                foreach (var profileDir in Directory.GetDirectories(userDataDir, "Profile *"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var profileName = Path.GetFileName(profileDir);
                    ScanChromiumCacheSubDirs(items, profileDir, $"{label} {profileName}",
                        progress, ref current, ref total, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描 {label} 多 Profile 缓存失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 扫描豆包缓存：只清 cache 类子目录。
    /// 旧版按整个 Doubao 目录删除，会把 IndexedDB 登录态和沙箱环境一起清掉，此处修正为白名单子目录。
    /// </summary>
    private static void ScanDoubaoCaches(
        List<CacheItem> items,
        ScanContext ctx,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        var userDataDir = Path.Combine(ctx.LocalAppData, "Doubao", "User Data");
        if (!Directory.Exists(userDataDir)) return;

        try
        {
            // User Data 根下的散装缓存目录（gecko_cache/GrShaderCache 等）
            ScanChromiumCacheSubDirs(items, userDataDir, "豆包", progress, ref current, ref total, cancellationToken,
                onlyRootLevel: true);

            // 热更新资源包（与 gecko_cache 双份存放的 web 资源，均为可重建缓存）
            var hotFixDir = Path.Combine(userDataDir, "hot_fix");
            if (Directory.Exists(hotFixDir))
                AddProfileCacheItem(items, hotFixDir, "豆包", "hot_fix", progress, ref current, ref total);

            // Default 及后续可能出现的 Profile N
            foreach (var profileDir in Directory.GetDirectories(userDataDir, "*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profileName = Path.GetFileName(profileDir);
                if (profileName is "Guest Profile" or "System Profile") continue;
                ScanChromiumCacheSubDirs(items, profileDir, $"豆包 {profileName}",
                    progress, ref current, ref total, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"扫描豆包缓存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在目录的一级子目录中匹配 Chromium 系缓存目录并逐个生成清理项。
    /// onlyRootLevel=true 时不下探 Service Worker 子目录（用于 User Data 根的散装目录）。
    /// </summary>
    private static void ScanChromiumCacheSubDirs(
        List<CacheItem> items,
        string parentDir,
        string label,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken,
        bool onlyRootLevel = false)
    {
        try
        {
            foreach (var subDir in Directory.GetDirectories(parentDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subName = Path.GetFileName(subDir);

                if (ChromiumCacheDirNames.Contains(subName))
                {
                    AddProfileCacheItem(items, subDir, label, subName, progress, ref current, ref total);
                    continue;
                }

                // Service Worker 下只认 CacheStorage/ScriptCache，已在白名单内，此处进入下探
                if (!onlyRootLevel && subName.Equals("Service Worker", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var swSub in Directory.GetDirectories(subDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var swName = Path.GetFileName(swSub);
                        if (ChromiumCacheDirNames.Contains(swName))
                            AddProfileCacheItem(items, swSub, $"{label} SW", swName, progress, ref current, ref total);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"扫描 {label} 缓存子目录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 生成单个缓存子目录的清理项（达到阈值才报告，避免列表噪音）
    /// </summary>
    private static void AddProfileCacheItem(
        List<CacheItem> items,
        string cacheDir,
        string label,
        string dirName,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total)
    {
        long size = SumFiles(cacheDir);
        if (size < MinProfileCacheSize) return;

        total++;
        current++;
        progress?.Report((current, total, $"{label} {dirName}"));
        items.Add(new CacheItem
        {
            Name = $"{label} {CacheDirSuffix(dirName)}",
            Path = cacheDir,
            Desc = "应用缓存子目录，清理后自动重建，不影响登录状态",
            Risk = RiskLevel.Safe,
            Exists = true,
            SizeBytes = size
        });
    }

        // === 阶段 1 新增：覆盖面扩展（设计见 docs/upgrade-plan.md 第 4 节）===

        // Electron 系应用缓存子目录结构同源（Chromium），统一按白名单扫描，替代逐应用维护
        // 的单条目；LarkShell 的 aha 目录含用户文档数据，只扫根级散装缓存目录
        private static readonly (string Dir, string Label, bool RootOnly)[] ElectronApps =
        [
            ("Code", "VS Code", false),
            ("Cursor", "Cursor", false),
            ("Trae CN", "Trae CN", false),
            ("Qoder", "Qoder", false),
            ("Positron", "Positron", false),
            ("CherryStudio", "CherryStudio", false),
            ("Claude", "Claude Desktop", false),
            ("LarkShell", "飞书", true),
        ];

        /// <summary>
        /// 扫描 Electron 系应用（AppData 下）的缓存子目录
        /// </summary>
        private static void ScanElectronApps(
            List<CacheItem> items,
            ScanContext ctx,
            IProgress<(int current, int total, string name)>? progress,
            ref int current,
            ref int total,
            CancellationToken cancellationToken)
        {
            foreach (var (dir, label, rootOnly) in ElectronApps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var appRoot = Path.Combine(ctx.AppData, dir);
                if (!Directory.Exists(appRoot)) continue;

                ScanChromiumCacheSubDirs(items, appRoot, label, progress, ref current, ref total, cancellationToken,
                    onlyRootLevel: rootOnly);
            }
        }

        /// <summary>
        /// 扫描 Firefox 各 Profile 的磁盘缓存（cache2，与登录态/书签无关）
        /// </summary>
        private static void ScanFirefoxCaches(
            List<CacheItem> items,
            ScanContext ctx,
            IProgress<(int current, int total, string name)>? progress,
            ref int current,
            ref int total,
            CancellationToken cancellationToken)
        {
            var profilesRoot = Path.Combine(ctx.LocalAppData, @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesRoot)) return;

            try
            {
                foreach (var profile in Directory.GetDirectories(profilesRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cacheDir = Path.Combine(profile, "cache2");
                    if (!Directory.Exists(cacheDir)) continue;

                    long size = SumFiles(cacheDir);
                    if (size < MinProfileCacheSize) continue;

                    total++;
                    current++;
                    var profileName = Path.GetFileName(profile);
                    progress?.Report((current, total, $"Firefox {profileName}"));
                    items.Add(new CacheItem
                    {
                        Name = $"Firefox {profileName} 缓存",
                        Path = cacheDir,
                        Desc = "Firefox 磁盘缓存，清理后自动重建，不影响登录与书签",
                        Risk = RiskLevel.Safe,
                        Exists = true,
                        SizeBytes = size
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描 Firefox 缓存失败: {ex.Message}");
            }
        }

        // UWP 应用包中确定属于缓存的子族相对路径（LocalCache 是应用数据，绝不触碰）
        private static readonly (string RelPath, string Name, string Desc)[] UwpCacheFamilies =
        [
            (@"AC\INetCache", "UWP 网络缓存", "UWP 应用的 WinINet 缓存，跨全部应用包聚合"),
            (@"AC\Temp", "UWP 临时文件", "UWP 应用 AC\\Temp 下的临时文件"),
            (@"TempState", "UWP 临时状态", "UWP 应用包的 TempState 临时状态"),
        ];

        /// <summary>
        /// 扫描 UWP 应用包的安全缓存族，跨包聚合体积。
        /// item.Path 存 Packages 根目录（真实存在，可通过清理门卫），清理时按名称映射回子族。
        /// </summary>
        private static void ScanUwpCacheFamilies(
            List<CacheItem> items,
            ScanContext ctx,
            IProgress<(int current, int total, string name)>? progress,
            ref int current,
            ref int total,
            CancellationToken cancellationToken)
        {
            var packagesRoot = Path.Combine(ctx.LocalAppData, "Packages");
            if (!Directory.Exists(packagesRoot)) return;

            try
            {
                foreach (var (relPath, name, desc) in UwpCacheFamilies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long size = 0;
                    foreach (var package in Directory.GetDirectories(packagesRoot))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var target = Path.Combine(package, relPath);
                        if (Directory.Exists(target)) size += SumFiles(target);
                    }

                    if (size < MinProfileCacheSize) continue;

                    total++;
                    current++;
                    progress?.Report((current, total, name));
                    items.Add(new CacheItem
                    {
                        Name = name,
                        Path = packagesRoot,
                        Desc = desc,
                        Risk = RiskLevel.Safe,
                        Exists = true,
                        SizeBytes = size
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描 UWP 缓存族失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 逐包清理 UWP 缓存族（按项名称映射回子族相对路径）
        /// </summary>
        private static CleanResult CleanUwpCacheFamily(CacheItem item, CancellationToken cancellationToken)
        {
            string? relPath = item.Name switch
            {
                "UWP 网络缓存" => @"AC\INetCache",
                "UWP 临时文件" => @"AC\Temp",
                "UWP 临时状态" => @"TempState",
                _ => null
            };
            if (relPath == null || string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
                return default;

            CleanResult total = default;
            try
            {
                foreach (var package in Directory.GetDirectories(item.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.Combine(package, relPath);
                    if (Directory.Exists(target)) total += CleanDirectory(target, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理 UWP 缓存族失败: {ex.Message}");
            }
            return total;
        }

        /// <summary>
        /// 扫描 OpenAI Codex 自更新遗留的旧版本目录（按内容哈希命名，只进不出），保留最新
        /// </summary>
        private static void ScanCodexOldVersions(
            List<CacheItem> items,
            ScanContext ctx,
            IProgress<(int current, int total, string name)>? progress,
            ref int current,
            ref int total,
            CancellationToken cancellationToken)
        {
            var codexRoot = Path.Combine(ctx.LocalAppData, @"OpenAI\Codex");
            if (!Directory.Exists(codexRoot)) return;

            AddKeepNewestVersionGroup(items, Path.Combine(codexRoot, "bin"), "Codex 主程序",
                progress, ref current, ref total);

            var runtimesRoot = Path.Combine(codexRoot, "runtimes");
            if (!Directory.Exists(runtimesRoot)) return;

            try
            {
                foreach (var runtime in Directory.GetDirectories(runtimesRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddKeepNewestVersionGroup(items, runtime, $"Codex {Path.GetFileName(runtime)}",
                        progress, ref current, ref total);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描 Codex 运行时旧版本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 报告一组版本目录中除最新外的全部占用（低于阈值不报，避免列表噪音）
        /// </summary>
        private static void AddKeepNewestVersionGroup(
            List<CacheItem> items,
            string dirPath,
            string label,
            IProgress<(int current, int total, string name)>? progress,
            ref int current,
            ref int total)
        {
            if (!Directory.Exists(dirPath)) return;

            try
            {
                var versions = new DirectoryInfo(dirPath).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTime)
                    .ToList();
                if (versions.Count <= 1) return;

                long oldSize = 0;
                for (int i = 1; i < versions.Count; i++) oldSize += SumFiles(versions[i].FullName);
                if (oldSize < MinProfileCacheSize) return;

                total++;
                current++;
                progress?.Report((current, total, $"{label} 旧版本"));
                items.Add(new CacheItem
                {
                    Name = $"{label} 旧版本",
                    Path = dirPath,
                    Desc = "自更新遗留的旧版本目录（保留最新），清理后不影响使用",
                    Risk = RiskLevel.Safe,
                    Exists = true,
                    SizeBytes = oldSize
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描旧版本目录失败 {dirPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// 删除目录内除最新外全部子目录（配合 Codex 旧版本清理项）
        /// </summary>
        private static CleanResult CleanKeepNewest(string dirPath, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(dirPath)) return default;

            long freed = 0;
            int deleted = 0, other = 0;
            try
            {
                var versions = new DirectoryInfo(dirPath).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTime)
                    .ToList();
                for (int i = 1; i < versions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long size = SumFiles(versions[i].FullName);
                    if (DryRun)
                    {
                        // 预览模式：不删除，仅统计将释放的量
                        freed += size;
                        deleted++;
                        continue;
                    }
                    try
                    {
                        versions[i].Delete(true);
                        // 删除成功才计入释放量
                        freed += size;
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        other++;
                        Debug.WriteLine($"删除旧版本失败 {versions[i].FullName}: {ex.Message}");
                        CleanLog.NoteFailure($"其它失败: {versions[i].FullName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理旧版本失败 {dirPath}: {ex.Message}");
            }
            return new CleanResult(freed, deleted, 0, 0, other, 0);
        }

        /// <summary>
        /// 删除匹配且超过 ageDays 天的文件（如 CBS 的 CbsPersist_*，绝不能碰正在写入的 CBS.log）
        /// </summary>
        private static CleanResult CleanOldFiles(string path, string pattern, int ageDays, bool recursive = false)
        {
            if (!Directory.Exists(path)) return default;

            long freed = 0;
            int deleted = 0, locked = 0, denied = 0, other = 0, pendingReboot = 0;
            var cutoff = DateTime.Now.AddDays(-ageDays);

            try
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles(pattern,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    if (file.LastWriteTime >= cutoff) continue;
                    try
                    {
                        long len = file.Length;
                        if (DryRun)
                        {
                            // 预览模式：不删除，仅统计将释放的量
                            freed += len;
                            deleted++;
                            continue;
                        }
                        file.Delete();
                        // 删除成功才计入释放量
                        freed += len;
                        deleted++;
                    }
                    catch (IOException)
                    {
                        if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                        else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); CleanLog.NoteFailure($"被占用: {file.FullName}"); }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        denied++;
                        Debug.WriteLine($"权限不足，跳过 {file.FullName}");
                        CleanLog.NoteFailure($"权限不足: {file.FullName}");
                    }
                    catch (Exception ex)
                    {
                        other++;
                        Debug.WriteLine($"删除过期文件失败 {file.FullName}: {ex.Message}");
                        CleanLog.NoteFailure($"其它失败: {file.FullName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理过期文件失败 {path}: {ex.Message}");
            }

            return new CleanResult(freed, deleted, locked, denied, other, pendingReboot);
        }

        /// <summary>
        /// Windows 更新下载缓存：停 wuauserv/bits → 清理 → 起服务。
        /// 不停服务直接删，会让运行中服务持有的文件全部落入「重启删除」，本次释放量失真。
        /// </summary>
        private static CleanResult CleanWindowsUpdateCache(string downloadDir, CancellationToken cancellationToken)
        {
            // 预览模式不触碰服务状态；CleanDirectory 内部自行走干跑统计
            bool wuauservStopped = !DryRun && RunCommand("net", "stop wuauserv", 30_000);
            bool bitsStopped = !DryRun && RunCommand("net", "stop bits", 30_000);
            CleanResult result;
            try
            {
                result = CleanDirectory(downloadDir, cancellationToken);
            }
            finally
            {
                // 只重启自己停掉的服务，不改变系统原有的服务运行状态
                if (bitsStopped) RunCommand("net", "start bits", 30_000);
                if (wuauservStopped) RunCommand("net", "start wuauserv", 30_000);
            }
            return result;
        }

        /// <summary>
        /// 传递优化缓存：走官方 Delete-DeliveryOptimizationCache cmdlet（DeliveryOptimization 模块），
        /// 以清理前后目录差值计释放量；失败不计入释放
        /// </summary>
        private static CleanResult CleanDeliveryOptimizationCache(CacheItem item)
        {
            if (DryRun) return new CleanResult(0, 1, 0, 0, 0, 0);   // 预览：跳过官方 cmdlet
            long before = SumFiles(item.Path);
            bool ok = RunCommand("powershell", "-NoProfile -Command \"Delete-DeliveryOptimizationCache -Force\"", 120_000);
            if (!ok) return new CleanResult(0, 0, 0, 0, 1, 0);

            long after = SumFiles(item.Path);
            item.SizeBytes = Math.Max(0, after);
            return new CleanResult(Math.Max(0, before - after), 1, 0, 0, 0, 0);
        }

        /// <summary>
        /// Windows 升级残留（Windows.old/$GetCurrent/ESD）：走官方 cleanmgr /autoclean。
        /// 执行后无法回滚到旧版本，该风险由清理确认弹窗的「注意」分类提示。
        /// </summary>
        private static CleanResult CleanUpgradeRemnants(IProgress<string>? progress)
        {
        if (DryRun)
        {
            progress?.Report("预览模式：跳过执行 cleanmgr /autoclean");
            return new CleanResult(0, 1, 0, 0, 0, 0);
        }
        progress?.Report("正在执行 cleanmgr /autoclean（系统磁盘清理，可能需要数分钟）...");
        bool ok = RunCommand("cleanmgr", "/autoclean", 3_600_000);
            return new CleanResult(0, ok ? 1 : 0, 0, 0, ok ? 0 : 1, 0);
        }

    /// <summary>
    /// 查询 C 盘卷影副本（系统还原点）实际占用的空间。
    /// 解析 vssadmin 输出中第一处「数值 + 单位」（已用行在保留/上限行之前，标签文本随系统语言变化，数值格式稳定）。
    /// </summary>
    private static (long UsedBytes, bool Ok) QueryShadowStorageUsed()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "vssadmin",
                Arguments = $"list shadowstorage /for={SysDriveLetter}:",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (0, false);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            if (proc.ExitCode != 0) return (0, false);

            // 数值兼容小数逗号 locale（如 de-DE 输出 5,2 GB）：统一替换逗号后按不变文化解析
            var match = Regex.Match(output, @"(\d+(?:[.,]\d+)?)\s*(KB|MB|GB|TB)", RegexOptions.IgnoreCase);
            if (!match.Success) return (0, false);

            double value = double.Parse(match.Groups[1].Value.Replace(',', '.'),
                System.Globalization.CultureInfo.InvariantCulture);
            long bytes = match.Groups[2].Value.ToUpperInvariant() switch
            {
                "TB" => (long)(value * 1024L * 1024 * 1024 * 1024),
                "GB" => (long)(value * 1024 * 1024 * 1024),
                "MB" => (long)(value * 1024 * 1024),
                _ => (long)value
            };
            return (bytes, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"查询卷影副本占用失败: {ex.Message}");
            return (0, false);
        }
    }

    /// <summary>
    /// 清空回收站：逐个 SID 子目录清空内容（保留目录结构）。
    /// 回收站里的文件本就是「已删除」状态，风险为 Safe。
    /// </summary>
    private static CleanResult CleanRecycleBin(CancellationToken cancellationToken)
    {
        var binRoot = Path.Combine(SysRoot, "$Recycle.Bin");
        if (!Directory.Exists(binRoot)) return default;

        CleanResult total = default;
        try
        {
            foreach (var sidDir in Directory.GetDirectories(binRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += CleanDirectory(sidDir, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"清空回收站失败: {ex.Message}");
        }
        return total;
    }

    /// <summary>
    /// 缩减卷影副本存储上限到 3GB（保留最新还原点，丢弃更早的还原点）。
    /// 命令式清理：不计删除文件数，成功时以 DeletedCount=1 标记（与 DNS 清理同一约定）。
    /// </summary>
    private static CleanResult ShrinkShadowStorage(CacheItem item, IProgress<string>? progress)
    {
        if (DryRun)
        {
            // 预览：缩减到 3GB 上限后的预计回收量
            progress?.Report("预览模式：跳过执行 vssadmin");
            return new CleanResult(Math.Max(0, item.SizeBytes - 3L * 1024 * 1024 * 1024), 1, 0, 0, 0, 0);
        }
        progress?.Report("正在缩减还原点存储上限到 3GB...");
        bool ok = RunCommand("vssadmin",
            $"resize shadowstorage /for={SysDriveLetter}: /on={SysDriveLetter}: /maxsize=3GB", 120_000);
        if (ok) item.SizeBytes = 0;     // 清理后表格立即归零，不再显示旧占用
        return new CleanResult(0, ok ? 1 : 0, 0, 0, ok ? 0 : 1, 0);
    }

    /// <summary>
    /// DISM 组件存储清理：移除被新版本取代的旧组件（微软官方支持的 WinSxS 瘦身方式）。
    /// 耗时 15-30 分钟，异步执行不阻塞 UI；成功时以 DeletedCount=1 标记。
    /// 注意：不用 /ResetBase，保留已装更新的卸载能力。
    /// </summary>
    private static CleanResult RunComponentCleanup(CacheItem item, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (DryRun)
        {
            progress?.Report("预览模式：跳过执行 DISM 组件清理");
            return new CleanResult(0, 1, 0, 0, 0, 0);
        }
        progress?.Report("正在执行 DISM 组件清理（15-30 分钟，请耐心等待）...");
        cancellationToken.ThrowIfCancellationRequested();
        // 超时上限 2 小时：老机器首次组件清理可能远超 30 分钟，被超时误判为失败会误导用户
        bool ok = RunCommand("Dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 7_200_000);
        if (ok) item.SizeBytes = 0;
        return new CleanResult(0, ok ? 1 : 0, 0, 0, ok ? 0 : 1, 0);
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
    /// 递归遍历目录树，匹配缓存模式。
    /// 安全措施：仅扫描 C 盘、跳过 junction point/symlink、使用 SkipRoots 黑名单
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

        // 驱动器边界检查：仅允许系统盘
        if (!path.StartsWith(SysRoot, StringComparison.OrdinalIgnoreCase))
            return;

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

            // 跳过 junction point / symlink（防止跨盘跟随符号链接）
            if (IsReparsePoint(dir))
            {
                Debug.WriteLine($"跳过符号链接: {dir}");
                continue;
            }

            // 跳过系统根目录黑名单（如 C:\Windows, C:\Program Files 等）
            if (IsInSkipRoot(dir))
                continue;

            // 匹配缓存模式（规范化前导点：让 .cache/.tmp 等点开头目录也能被发现）
            var matchName = dirName.TrimStart('.');
            if (CacheDirNames.Contains(matchName))
            {
                // 去重：已知缓存路径不再报告
                try
                {
                    var normalized = Path.GetFullPath(dir).TrimEnd('\\');
                    if (knownPaths.Contains(normalized)) continue;
                }
                catch { continue; }

                // 二次驱动器校验（路径解析后可能变化）
                if (!dir.StartsWith(SysRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 计算大小
                long size = SumFiles(dir);
                if (size < MinReportSize) continue;

                // 路径安全校验
                if (!IsPathSafeForCleaning(dir)) continue;

                results.Add(new CacheItem
                {
                    Name = GenerateAutoName(matchName, dir),
                    Path = dir,
                    Desc = GenerateAutoDesc(matchName, dir),
                    Risk = AssessAutoRisk(dir, matchName),
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

    /// <summary>
    /// 检测目录是否为 junction point 或 symbolic link（Reparse Point）
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查路径是否在 SkipRoots 黑名单中
    /// </summary>
    private static bool IsInSkipRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd('\\');
            foreach (var root in SkipRoots)
            {
                var rootNorm = root.TrimEnd('\\');
                // 路径本身是黑名单根目录，或在其下
                if (fullPath.Equals(rootNorm, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(rootNorm + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
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
        // 项目日志可能是审计记录而非可丢弃缓存，标 Warn 交由用户判断，且不默认勾选
        if (dirName is "log" or "logs")
            return RiskLevel.Warn;
        return RiskLevel.Safe;
    }

    /// <summary>
    /// 系统盘当前可用字节数。清理效果以磁盘可用空间差为准，文件长度累计仅作明细口径
    /// （两者可能不同：其他程序并发写入、重启删除的空间在重启后才回收）。
    /// </summary>
    public static long GetCFreeBytes()
    {
        try { return new DriveInfo(SysRoot).AvailableFreeSpace; }
        catch { return 0; }
    }

    /// <summary>
    /// 执行命令并捕获标准输出（如 logman query -ets）。
    /// </summary>
    private static (bool Ok, string Output) RunCommandOutput(string fileName, string arguments, int timeoutMs = 15_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "");
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            if (!proc.HasExited) return (false, output);
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"执行命令失败 {fileName} {arguments}: {ex.Message}");
            return (false, "");
        }
    }

    /// <summary>
    /// 检测遗留的性能追踪会话（WPR 录制未停止、手动启动的内核追踪）。
    /// 此类会话是「C 盘持续变小」的经典元凶：长期运行的 ETW 追踪会持续向磁盘刷缓冲。
    /// Circular Kernel Context Logger、EventLog-* 等是系统正常会话，不在检测范围。
    /// </summary>
    private static void ScanLeftoverTraces(
        List<CacheItem> items,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = QueryLeftoverTraceSessions();
        if (sessions.Count == 0) return;

        total++;
        current++;
        progress?.Report((current, total, "遗留性能追踪会话"));
        items.Add(new CacheItem
        {
            Name = "遗留性能追踪会话",
            Path = "（命令式清理）",
            Desc = $"检测到 {sessions.Count} 个长期运行的诊断追踪会话（{string.Join("、", sessions.Take(2))}{(sessions.Count > 2 ? " 等" : "")}），会持续写盘；清理 = 停止这些会话",
            Risk = RiskLevel.Warn,
            Exists = true,
            SizeBytes = 0
        });
    }

    /// <summary>
    /// 从 logman query -ets 输出中筛出「正在运行」的遗留追踪会话名
    /// </summary>
    private static List<string> QueryLeftoverTraceSessions()
    {
        var (_, output) = RunCommandOutput("logman", "query -ets");
        var sessions = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            // 表头兼容中英文系统（数据收集器集 / Data Collector Set）
            if (trimmed.Length == 0 || trimmed.StartsWith('-') ||
                trimmed.StartsWith("数据收集器集") || trimmed.StartsWith("Data Collector Set")) continue;
            if (!trimmed.Contains("正在运行") && !trimmed.Contains("Running")) continue;

            string? name = null;
            if (trimmed.Contains("WPR_initiated_"))
            {
                name = trimmed.Split(' ')[0];
            }
            else if (trimmed.StartsWith("NT Kernel Logger"))
            {
                // 手动实例的内核追踪；Circular Kernel Context Logger 是系统正常会话，不碰
                name = "NT Kernel Logger";
            }
            if (name != null && !sessions.Contains(name)) sessions.Add(name);
        }
        return sessions;
    }

    /// <summary>
    /// 停止遗留的追踪会话（logman stop "&lt;name&gt;" -ets）。
    /// 部分 SYSTEM 保护级会话可能停不掉，失败计入其它失败；停止后追踪文件不再增长，
    /// 受保护残留会话在下次重启后自然消失。
    /// </summary>
    private static CleanResult CleanLeftoverTraces()
    {
        if (DryRun)
        {
            // 预览：按将要停止的会话数计
            return new CleanResult(0, QueryLeftoverTraceSessions().Count, 0, 0, 0, 0);
        }
        var sessions = QueryLeftoverTraceSessions();
        if (sessions.Count == 0) return new CleanResult(0, 1, 0, 0, 0, 0);

        int stopped = 0, failed = 0;
        foreach (var session in sessions)
        {
            if (RunCommand("logman", $"stop \"{session}\" -ets", 30_000)) stopped++;
            else failed++;
        }
        return new CleanResult(0, stopped, 0, 0, failed, 0);
    }

    /// <summary>
    /// 扫描 Docker/WSL：vhdx 虚拟磁盘（只增不减，可压缩回收）与 Docker 未使用数据（prune 可回收量）。
    /// 取代旧的「整删 Docker 数据」危险项——prune 与 compact 均为官方支持的再生性操作。
    /// Docker daemon 未运行时不显示 prune 项（Docker Desktop 启动后可见）。
    /// </summary>
    private static void ScanDockerWsl(
        List<CacheItem> items,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        // —— WSL/Docker vhdx 虚拟磁盘 ——
        var vhdxFiles = CollectVhdxFiles(cancellationToken);
        long vhdxTotal = 0;
        foreach (var f in vhdxFiles)
        {
            try { vhdxTotal += f.Length; } catch { }
        }

        if (vhdxTotal >= 512L * 1024 * 1024)
        {
            total++;
            current++;
            progress?.Report((current, total, "WSL/Docker 虚拟磁盘"));
            items.Add(new CacheItem
            {
                Name = "WSL/Docker 虚拟磁盘",
                Path = "（命令式清理）",
                Desc = $"{vhdxFiles.Count} 个 vhdx 虚拟磁盘，只增不减；清理 = 离线压缩回收空白（需先关闭 WSL/Docker，运行中的发行版会被强制关闭）",
                Risk = RiskLevel.Warn,
                Exists = true,
                SizeBytes = vhdxTotal
            });
        }

        // —— Docker 未使用数据（prune）——
        var (dockerOk, _) = RunCommandOutput("docker", "info", 15_000);
        if (!dockerOk) return;

        var (_, dfOutput) = RunCommandOutput("docker", "system df", 20_000);
        long reclaimable = ParseDockerReclaimable(dfOutput);
        if (reclaimable < 100L * 1024 * 1024) return;

        total++;
        current++;
        progress?.Report((current, total, "Docker 未使用数据"));
        items.Add(new CacheItem
        {
            Name = "Docker 未使用数据",
            Path = "（命令式清理）",
            Desc = "未使用的镜像/容器/卷/构建缓存（docker system prune -a --volumes），运行中的容器不受影响",
            Risk = RiskLevel.Warn,
            Exists = true,
            SizeBytes = reclaimable
        });
    }

    /// <summary>
    /// 收集 WSL/Docker 的 vhdx 虚拟磁盘文件（扫描与压缩共用同一套根，保证口径一致）
    /// </summary>
    private static List<FileInfo> CollectVhdxFiles(CancellationToken cancellationToken)
    {
        var files = new List<FileInfo>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(localAppData, "Docker", "wsl"),
            Path.Combine(localAppData, "wsl"),
            Path.Combine(localAppData, "Packages")
        };
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                files.AddRange(new DirectoryInfo(root).EnumerateFiles("*.vhdx", options));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"枚举 vhdx 失败 {root}: {ex.Message}");
            }
        }
        return files;
    }

    /// <summary>
    /// 解析 docker system df 输出中各行的 RECLAIMABLE 列（带百分比后缀的列）并求和
    /// </summary>
    private static long ParseDockerReclaimable(string output)
    {
        long total = 0;
        foreach (var line in output.Split('\n'))
        {
            // RECLAIMABLE 列形如 "800MB (55%)"，SIZE 列无百分比，靠后缀区分
            var m = Regex.Match(line, @"([\d.]+)\s*(B|KB|MB|GB|TB)\s*\(\d+%");
            if (!m.Success) continue;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var value))
                continue;
            total += m.Groups[2].Value.ToUpperInvariant() switch
            {
                "TB" => (long)(value * 1024L * 1024 * 1024 * 1024),
                "GB" => (long)(value * 1024L * 1024 * 1024),
                "MB" => (long)(value * 1024L * 1024),
                "KB" => (long)(value * 1024),
                _ => (long)value
            };
        }
        return total;
    }

    /// <summary>
    /// Docker 未使用数据清理：以 prune 前后 docker system df 差值计释放量
    /// </summary>
    private static CleanResult CleanDockerPrune(IProgress<string>? progress)
    {
        if (DryRun)
        {
            progress?.Report("预览模式：跳过执行 docker system prune");
            return new CleanResult(0, 1, 0, 0, 0, 0);
        }

        var (_, beforeDf) = RunCommandOutput("docker", "system df", 20_000);
        long beforeBytes = ParseDockerReclaimable(beforeDf);

        progress?.Report("正在执行 docker system prune -a --volumes（可能需要数分钟）...");
        bool ok = RunCommand("docker", "system prune -a --volumes -f", 1_800_000);
        if (!ok) return new CleanResult(0, 0, 0, 0, 1, 0);

        var (_, afterDf) = RunCommandOutput("docker", "system df", 20_000);
        long afterBytes = ParseDockerReclaimable(afterDf);
        return new CleanResult(Math.Max(0, beforeBytes - afterBytes), 1, 0, 0, 0, 0);
    }

    /// <summary>
    /// WSL/Docker vhdx 离线压缩：wsl --shutdown 后逐盘 diskpart compact vdisk，
    /// 释放量 = 压缩前后文件实际大小差。压缩是官方再生性操作，不删除任何数据。
    /// </summary>
    private static CleanResult CleanWslVhdx(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (DryRun)
        {
            progress?.Report("预览模式：跳过 vhdx 压缩（实际执行需先关闭 WSL/Docker）");
            return new CleanResult(0, 1, 0, 0, 0, 0);
        }

        var files = CollectVhdxFiles(cancellationToken);
        if (files.Count == 0) return default;

        long SizeSum()
        {
            long t = 0;
            foreach (var f in files)
            {
                try { t += f.Length; } catch { }
            }
            return t;
        }

        progress?.Report("正在关闭 WSL（Docker Desktop 的 WSL 后端随之停止）...");
        RunCommand("wsl", "--shutdown", 30_000);

        long before = SizeSum();
        var scriptPath = Path.Combine(Path.GetTempPath(), $"cachecleaner-compact-{Guid.NewGuid():N}.txt");
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.WriteAllText(scriptPath,
                        $"select vdisk file=\"{file.FullName}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n");
                    if (!RunCommand("diskpart", $"/s \"{scriptPath}\"", 1_800_000))
                        CleanLog.NoteFailure($"vhdx 压缩失败: {file.FullName}");
                }
                catch (Exception ex)
                {
                    CleanLog.NoteFailure($"vhdx 压缩失败 {file.FullName}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
        }

        return new CleanResult(Math.Max(0, before - SizeSum()), 1, 0, 0, 0, 0);
    }

    /// <summary>
    /// 未收录 agent/工具目录探测：家目录下未被任何规则/扫描器/agent 目录覆盖、
    /// 且体积超阈值的 dot-dir，以「只报告不清理」方式列出——确认语义后可经
    /// rules.user.json 纳入。这是「检测所有 agent」的兜底：已知全自动，未知看得见。
    /// </summary>
    private static void ScanUnknownAgentHomes(
        List<CacheItem> items,
        ScanContext ctx,
        IProgress<(int current, int total, string name)>? progress,
        ref int current,
        ref int total,
        CancellationToken cancellationToken)
    {
        // 已覆盖集合：本次扫描已生成的条目路径 + agent 目录 home + 明确的非工具目录
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.Path) || it.Path == "（命令式清理）") continue;
            try { covered.Add(Path.GetFullPath(it.Path).TrimEnd('\\')); } catch { }
        }
        foreach (var agent in AgentCatalog.All)
        {
            var home = agent.HomeFullPath;
            if (home != null) covered.Add(home.TrimEnd('\\'));
        }
        foreach (var benign in BenignHomeDirs)
            covered.Add(Path.Combine(ctx.UserProfile, benign).TrimEnd('\\'));

        int reported = 0;
        foreach (var dir in Directory.GetDirectories(ctx.UserProfile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (!name.StartsWith('.')) continue;                       // 仅探测 dot-dir
            if (IsReparsePoint(dir)) continue;                        // 跳过 junction

            var full = Path.GetFullPath(dir).TrimEnd('\\');
            if (covered.Contains(full)) continue;

            long size = SumFiles(dir);
            if (size < 200L * 1024 * 1024) continue;                  // 噪音阈值

            total++;
            current++;
            progress?.Report((current, total, $"未收录 {name}"));
            items.Add(new CacheItem
            {
                Name = $"未收录工具目录 ({name})",
                Path = full,
                Desc = $"家目录下未收录的工具/agent 目录（{CacheScanner.FormatSize(size)}，只报告不清理）。确认语义后可在 rules.user.json 中纳入",
                Risk = RiskLevel.Warn,
                Exists = true,
                SizeBytes = size
            });
            covered.Add(full);

            if (++reported >= 5) break;                               // 最多报告 5 个，避免刷屏
        }
    }

    /// <summary>
    /// 家目录下判定为「非 agent 工具」的常见目录（凭证/运行时/系统），不进入未收录报告
    /// </summary>
    private static readonly HashSet<string> BenignHomeDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".gnupg", ".dotnet", ".docker", ".kube", ".aws", ".config", ".git", ".azure"
    };

    /// <summary>
    /// 通用命令执行（如 ipconfig /flushdns），返回是否成功退出
    /// </summary>
    private static bool RunCommand(string fileName, string arguments, int timeoutMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // WaitForExit(timeout) 返回 false = 超时未退出，此时读 ExitCode 会抛
            // InvalidOperationException；进程仍在后台运行，如实报失败，绝不能误杀
            // 长任务（DISM 组件清理是小时级操作，中途终止会损坏组件存储）
            if (!proc.WaitForExit(timeoutMs))
            {
                Debug.WriteLine($"命令超时未退出（仍在后台运行）: {fileName} {arguments}");
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"执行命令失败 {fileName} {arguments}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 刷新 DNS 解析缓存（ipconfig /flushdns）。命令式清理，无目录路径。
    /// </summary>
    private static CleanResult FlushDnsCache()
    {
        if (DryRun) return new CleanResult(0, 1, 0, 0, 0, 0);   // 预览：DNS 刷新无字节可计
        bool ok = RunCommand("ipconfig", "/flushdns");
        // DNS 缓存不以字节计；DeletedCount 借用为「已刷新」标记，失败计入 OtherFailureCount
        return new CleanResult(0, ok ? 1 : 0, 0, 0, ok ? 0 : 1, 0);
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

    /// <summary>
    /// 检查某个进程名是否正在运行（规则 guardProcesses 的检查原语）
    /// </summary>
    private static bool HasRunningProcess(string processName)
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"进程检测失败: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 检测当前正在运行、常见会占用缓存的目标程序（P1-1，用于清理前提示用户关闭）
    /// </summary>
    public static List<string> DetectRunningTargets()
    {
        // 进程名（不含 .exe）→ 友好显示名
        var targets = new (string Proc, string Display)[]
        {
            ("msedge", "Edge"), ("chrome", "Chrome"),
            ("firefox", "Firefox"), ("brave", "Brave"), ("vivaldi", "Vivaldi"),
            ("Code", "VS Code"), ("Cursor", "Cursor"), ("Trae CN", "Trae"),
            ("Doubao", "豆包"), ("JianyingPro", "剪映"), ("bilibili", "哔哩哔哩"),
            ("RStudio", "RStudio"), ("Positron", "Positron"), ("Qoder", "Qoder"),
            ("CherryStudio", "CherryStudio"), ("Claude", "Claude Desktop"),
            ("Obsidian", "Obsidian"),
            ("Illustrator", "Illustrator"), ("Photoshop", "Photoshop"),
            ("InDesign", "InDesign"), ("Adobe Premiere Pro", "Premiere Pro"),
            ("WeChat", "微信3"), ("Weixin", "微信4"),   // 小程序运行时清理前应关闭
            ("SodaMusic", "汽水音乐"),
            ("LarkShell", "飞书"), ("Feishu", "飞书")
        };

        var running = new List<string>();
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { names.Add(p.ProcessName); } catch { }
            }
            foreach (var (proc, display) in targets)
            {
                if (names.Contains(proc) && !running.Contains(display))
                    running.Add(display);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"检测运行中进程失败: {ex.Message}");
        }
        return running;
    }

    /// <summary>
    /// 标记文件在下次重启时删除（用于被占用文件，需管理员权限）。
    /// 机制：写入注册表 HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations，
    /// 开机时由 smss.exe 在任何进程锁文件前完成删除（BleachBit 等同类工具同款做法）。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    /// <summary>
    /// 尝试登记文件重启删除；成功返回 true，失败（如非管理员/路径不支持）返回 false
    /// </summary>
    private static bool ScheduleDeleteOnReboot(string filePath)
    {
        try
        {
            return MoveFileEx(filePath, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"登记重启删除失败 {filePath}: {ex.Message}");
            return false;
        }
    }
}
