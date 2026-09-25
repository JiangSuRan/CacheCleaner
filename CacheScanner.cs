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

    // Conda 可能的安装路径（仅限 C 盘用户目录和已知安全路径）
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
        ("pnpm 缓存", @"pnpm-cache", "pnpm 包管理器缓存", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("pnpm store", @"pnpm\store", "pnpm 内容寻址包存储，清理后按需重新下载", RiskLevel.Safe, BaseFolder.LocalAppData),

        // IDE / 编辑器（Cache/CachedData/VSIX 等缓存子目录统一由 ScanElectronApps 扫描，此处保留日志与工作区状态）
        ("VS Code 日志", @"Code\logs", "VS Code 日志文件", RiskLevel.Safe, BaseFolder.AppData),
        ("RStudio 缓存", @"RStudio\cache", "RStudio 缓存（仅 cache 子目录）", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("RStudio 日志", @"RStudio\log", "RStudio 日志文件", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("VS Code 工作区状态", @"Code\User\workspaceStorage", "VS Code 工作区界面状态，清理后各工作区状态重置", RiskLevel.Warn, BaseFolder.AppData),
        ("Cursor 工作区状态", @"Cursor\User\workspaceStorage", "Cursor 工作区界面状态，清理后各工作区状态重置", RiskLevel.Warn, BaseFolder.AppData),
        ("Positron 工作区状态", @"Positron\User\workspaceStorage", "Positron 工作区界面状态，清理后各工作区状态重置", RiskLevel.Warn, BaseFolder.AppData),

        // 下载工具
        ("迅雷缓存", @"Thunder Network", "迅雷下载缓存", RiskLevel.Warn, BaseFolder.LocalAppData),

        // 视频/图片工具
        ("剪映缓存", @"JianyingPro\User Data\Cache", "剪映视频编辑缓存", RiskLevel.Warn, BaseFolder.LocalAppData),

        // 浏览器缓存
        ("Edge 缓存", @"Microsoft\Edge\User Data\Default\Cache", "Edge 浏览器缓存", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("Chrome 缓存", @"Google\Chrome\User Data\Default\Cache", "Chrome 浏览器缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

        // Windows 系统缓存
        ("Windows 临时文件", @"Temp", "Windows 临时文件（仅清理超过7天的）", RiskLevel.Warn, BaseFolder.LocalAppData),
        ("Windows 缩略图缓存", @"Microsoft\Windows\Explorer", "资源管理器缩略图与图标缓存（thumbcache_*/iconcache_*）", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("Windows 预读取", @"Prefetch", "Windows 程序预读取缓存", RiskLevel.Safe, BaseFolder.Windows),

        // AI 编辑器（缓存子目录统一由 ScanElectronApps 扫描）
        ("Cursor 日志", @"Cursor\logs", "Cursor AI 编辑器日志", RiskLevel.Safe, BaseFolder.AppData),
        ("Trae CN 日志", @"Trae CN\logs", "字节跳动 Trae IDE 日志", RiskLevel.Safe, BaseFolder.AppData),
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
        ("NVIDIA 着色器缓存", @"NVIDIA\DXCache", "NVIDIA DirectX 着色器缓存，清理后自动重建", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("NVIDIA OpenGL 缓存", @"NVIDIA\GLCache", "NVIDIA OpenGL 着色器缓存，清理后自动重建", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("AMD 着色器缓存", @"AMD\DxCache", "AMD DirectX 着色器缓存，清理后自动重建", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("AMD Dxc 着色器缓存", @"AMD\DxcCache", "AMD DXC 着色器缓存，清理后自动重建", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("Intel 着色器缓存", @"Intel\ShaderCache", "Intel GPU 着色器缓存，清理后自动重建", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("崩溃转储", @"CrashDumps", "应用程序崩溃转储文件", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("PowerToys 更新缓存", @"Microsoft\PowerToys\Updates", "PowerToys 更新包", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("Electron 安装临时文件", @"SquirrelTemp", "Electron 应用安装临时文件", RiskLevel.Safe, BaseFolder.LocalAppData),
        ("腾讯日志", @"Tencent\Logs", "腾讯软件日志文件", RiskLevel.Safe, BaseFolder.AppData),
        ("微信小程序运行时", @"Tencent\WeChat\XPlugin", "微信3.x 小程序运行时组件，清理后首次使用小程序需重新下载", RiskLevel.Warn, BaseFolder.AppData),
        ("微信4.0 日志", @"Tencent\xwechat\log", "微信4.0 运行日志", RiskLevel.Safe, BaseFolder.AppData),
        ("微信4.0 更新包", @"Tencent\xwechat\update", "微信4.0 已下载的更新安装包", RiskLevel.Safe, BaseFolder.AppData),
        ("微信4.0 小程序运行时", @"Tencent\xwechat\xplugin", "微信4.0 小程序运行时组件，清理后首次使用小程序需重新下载", RiskLevel.Warn, BaseFolder.AppData),
        ("微信输入法安装包", @"Tencent\WeType\setup", "微信输入法下载的安装包缓存，更新时重新下载", RiskLevel.Warn, BaseFolder.AppData),
        ("GitHub Desktop 更新包", @"GitHubDesktop\packages", "GitHub Desktop 更新包缓存", RiskLevel.Safe, BaseFolder.LocalAppData),

        // Docker（高风险）
        ("Docker 镜像/容器", @"Docker", "Docker Desktop 所有数据（镜像+容器+卷）", RiskLevel.Danger, BaseFolder.LocalAppData),

        // === 系统级缓存（P0-3 新增，需管理员权限才能真正清理）===
        // Windows 更新 / 传递优化
        ("Windows 更新下载缓存", @"SoftwareDistribution\Download", "Windows Update 下载缓存（自动停启 wuauserv/bits）", RiskLevel.Safe, BaseFolder.Windows),
        ("Delivery Optimization 缓存", @"SoftwareDistribution\DeliveryOptimization", "Windows 传递优化缓存（官方 cmdlet 清理）", RiskLevel.Safe, BaseFolder.Windows),

        // 系统临时（C:\Windows\Temp，复用 CleanTempFiles 的 7 天规则）
        ("系统临时文件", @"Temp", "系统临时文件（仅清理超过 7 天的）", RiskLevel.Warn, BaseFolder.Windows),

        // Windows 错误报告（位于 ProgramData，由 AllowedSystemPaths 白名单放行）
        ("Windows 错误报告归档", @"Microsoft\Windows\WER\ReportArchive", "Windows 错误报告归档", RiskLevel.Safe, BaseFolder.ProgramData),
        ("Windows 错误报告队列", @"Microsoft\Windows\WER\ReportQueue", "Windows 错误报告队列", RiskLevel.Safe, BaseFolder.ProgramData),
        ("VS 安装缓存", @"Package Cache", "Visual Studio 安装包缓存（只读报告：删除会破坏修复/卸载）", RiskLevel.Warn, BaseFolder.ProgramData),
        ("CBS 服务日志", @"Logs\CBS", "Windows 组件服务日志（CbsPersist_*），仅清理 30 天前的", RiskLevel.Warn, BaseFolder.Windows),

        // 崩溃转储（系统级）
        ("系统崩溃转储", @"Minidump", "系统蓝屏/崩溃 minidump 文件", RiskLevel.Warn, BaseFolder.Windows),
        ("内核崩溃报告", @"LiveKernelReports", "Windows 实时内核崩溃报告", RiskLevel.Warn, BaseFolder.Windows),
        ("系统内存转储", @"Memory.dmp", "系统内存转储文件（蓝屏后可能数 GB）", RiskLevel.Warn, BaseFolder.Windows),

        // 音乐/视频客户端缓存（常见空间大头，原名即 Cache，安全清理后自动重建）
        ("汽水音乐缓存", @"SodaMusic\LunaCacheV2", "汽水音乐播放缓存，清理后自动重建", RiskLevel.Safe, BaseFolder.AppData),

        // 用户主目录下的 CLI 工具缓存（点开头目录，自动发现原本会漏掉）
        ("CLI 工具缓存根", @".cache", "通用 CLI 工具缓存（codex-runtimes/babeldoc/chroma 等）", RiskLevel.Safe, BaseFolder.UserProfile),
        ("Codex 临时文件", @".codex\.tmp", "OpenAI Codex CLI 临时文件", RiskLevel.Safe, BaseFolder.UserProfile),

        // DNS 缓存（命令式清理，无目录路径）
        ("DNS 解析缓存", "", "DNS 解析缓存，执行 ipconfig /flushdns", RiskLevel.Safe, BaseFolder.LocalAppData),

        // === 系统级空间大头（P0：常规缓存清理工具的典型盲区）===
        // 回收站位于盘根，不在任何用户目录下，走 DriveRoot 基准 + AllowedSystemPaths 白名单
        ("回收站", @"$Recycle.Bin", "回收站中已删除的文件，清空后不可恢复", RiskLevel.Safe, BaseFolder.DriveRoot),
        ("Windows 升级残留", "", "Windows.old/$GetCurrent/ESD 升级残留，走 cleanmgr /autoclean，执行后无法回滚旧版本", RiskLevel.Warn, BaseFolder.DriveRoot),

        // 还原点（卷影副本）：vssadmin 查询实际占用，清理 = 把上限缩减到 3GB（保留最新还原点）
        ("系统还原点（卷影副本）", "", "卷影副本占用，清理 = 缩减上限到 3GB（保留最新还原点）", RiskLevel.Warn, BaseFolder.LocalAppData),

        // WinSxS：硬链接导致资源管理器显示体积虚高，全量统计极慢；仅提供 DISM 清理入口
        ("Windows 组件存储 (WinSxS)", @"WinSxS", "组件存储，DISM 清理被取代的旧组件（15-30 分钟）", RiskLevel.Warn, BaseFolder.Windows),
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

                // 系统内存转储是单文件（C:\Windows\Memory.dmp，非目录），单独扫描
                if (name == "系统内存转储")
                {
                    var dmpPath = Path.Combine(ctx.WindowsDir, "Memory.dmp");
                    item.Path = dmpPath;
                    item.Exists = File.Exists(dmpPath);
                    if (item.Exists)
                    {
                        try { item.SizeBytes = new FileInfo(dmpPath).Length; } catch { }
                    }
                    items.Add(item);
                    continue;
                }

                // DNS 缓存为命令式清理（ipconfig /flushdns），无目录路径，不可走通用解析
                if (name == "DNS 解析缓存")
                {
                    item.Path = "（命令式清理）";
                    item.Exists = true;
                    items.Add(item);
                    continue;
                }

                // 回收站：SumFiles 对隐藏+系统属性目录可正常统计（本程序以管理员运行）
                if (name == "回收站")
                {
                    item.Path = Path.Combine(@"C:\", relativePath);
                    item.Exists = Directory.Exists(item.Path);
                    if (item.Exists) item.SizeBytes = SumFiles(item.Path);
                    items.Add(item);
                    continue;
                }

                // 系统还原点：命令式扫描（vssadmin list shadowstorage 解析实际占用），清理为缩减上限
                if (name == "系统还原点（卷影副本）")
                {
                    var (usedBytes, ok) = QueryShadowStorageUsed();
                    item.Path = "（命令式清理）";
                    item.Exists = ok;
                    item.SizeBytes = usedBytes;
                    items.Add(item);
                    continue;
                }

                // WinSxS：硬链接重复计数导致统计值虚高，且全量遍历极慢——跳过统计，仅提供清理入口
                if (name == "Windows 组件存储 (WinSxS)")
                {
                    item.Path = Path.Combine(ctx.WindowsDir, relativePath);
                    item.Exists = Directory.Exists(item.Path);
                    item.SizeBytes = 0;
                    items.Add(item);
                    continue;
                }

                // Windows 升级残留：三处常见位置合并报告，清理走 cleanmgr /autoclean
                if (name == "Windows 升级残留")
                {
                    long remnantBytes = 0;
                    foreach (var remnant in new[] { @"C:\Windows.old", @"C:\$GetCurrent", @"C:\ESD" })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Directory.Exists(remnant)) remnantBytes += SumFiles(remnant);
                    }
                    item.Path = "（命令式清理）";
                    item.Exists = remnantBytes > 0;
                    item.SizeBytes = remnantBytes;
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
                        // 缩略图缓存仅计算 thumbcache_*/iconcache_* 文件（避免先全扫再覆盖的双扫问题）
                        item.SizeBytes = name == "Windows 缩略图缓存"
                            ? SumFiles(resolved, "thumbcache_*", SearchOption.TopDirectoryOnly)
                              + SumFiles(resolved, "iconcache_*", SearchOption.TopDirectoryOnly)
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
                Directory.Exists(Path.Combine(@"C:\", relativePath))
                    ? Path.Combine(@"C:\", relativePath)
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

        // 只读报告项：删除 Package Cache 会破坏 Visual Studio 的修复/卸载能力，绝不清理
        if (item.Name == "VS 安装缓存")
            return default;

        // UWP 缓存族项的 Path 是 Packages 根目录，实际按名称携带的子族逐包清理
        if (item.Name.StartsWith("UWP "))
            return CleanUwpCacheFamily(item, cancellationToken);

        // AI CLI 自更新遗留目录（保留最新版本）；GitHub Desktop 旧版本走下方精确匹配
        if (item.Name.StartsWith("Codex") && item.Name.EndsWith(" 旧版本"))
            return CleanKeepNewest(item.Path, cancellationToken);

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

        CleanResult result = item.Name switch
        {
            "pip 缓存" => CleanPipCache(item, progress),
            "Windows 临时文件" => CleanTempFiles(item.Path, cancellationToken),
            "系统临时文件" => CleanTempFiles(item.Path, cancellationToken),
            "Windows 缩略图缓存" => CleanThumbAndIconCache(item.Path),
            "系统内存转储" => CleanSingleFile(item.Path),
            "GitHub Desktop 旧版本" => CleanGithubDesktopOldVersions(item.Path, cancellationToken),
            "回收站" => CleanRecycleBin(cancellationToken),
            "Windows 更新下载缓存" => CleanWindowsUpdateCache(item.Path, cancellationToken),
            "Delivery Optimization 缓存" => CleanDeliveryOptimizationCache(item),
            "CBS 服务日志" => CleanOldFiles(item.Path, "CbsPersist_*", 30),
            "Windows 组件存储 (WinSxS)" => RunComponentCleanup(item, progress, cancellationToken),
            _ => CleanDirectory(item.Path, cancellationToken),
        };

        if (result.FreedBytes > 0)
        {
            item.SizeBytes = Math.Max(0, item.SizeBytes - result.FreedBytes);
            if (item.SizeBytes == 0) item.Exists = false;
        }

        return result;
    }

    /// <summary>
    /// 显式放行的系统级清理路径前缀（精确到具体目录，绝不放开整个 C:\ProgramData）
    /// </summary>
    private static readonly string[] AllowedSystemPaths =
    [
        @"C:\ProgramData\Microsoft\Windows\WER",
        @"C:\$Recycle.Bin"
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

            // 必须在 C 盘
            if (!fullPath.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
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
            // 自动发现的多用户路径（C:\Users\ 下的其他用户目录）
            if (fullPath.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase))
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
                    file.Delete();
                    // 删除成功才计入释放量：登记重启删除的文件尚未真正释放，不能虚报
                    freed += len;
                    deleted++;
                }
                catch (IOException)
                {
                    // 文件被占用：先尝试登记为重启删除（需管理员），成功计为待重启，否则计为占用失败
                    if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                    else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); }
                }
                catch (UnauthorizedAccessException) { denied++; Debug.WriteLine($"权限不足，跳过 {file.FullName}"); }
                catch (Exception ex) { other++; Debug.WriteLine($"删除临时文件失败 {file.FullName}: {ex.Message}"); }
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
    private static CleanResult CleanFiles(string path, string pattern)
    {
        if (!Directory.Exists(path)) return default;
        long freed = 0;
        int deleted = 0, locked = 0, denied = 0, other = 0, pendingReboot = 0;

        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles(pattern))
            {
                try
                {
                    long len = file.Length;
                    file.Delete();
                    // 删除成功才计入释放量：登记重启删除的文件尚未真正释放，不能虚报
                    freed += len;
                    deleted++;
                }
                catch (IOException)
                {
                    // 文件被占用：先尝试登记为重启删除（需管理员），成功计为待重启，否则计为占用失败
                    if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                    else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); }
                }
                catch (UnauthorizedAccessException) { denied++; Debug.WriteLine($"权限不足，跳过 {file.FullName}"); }
                catch (Exception ex) { other++; Debug.WriteLine($"删除文件失败 {file.FullName}: {ex.Message}"); }
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
                    file.Delete();
                    // 删除成功才计入释放量：登记重启删除的文件尚未真正释放，不能虚报
                    freed += len;
                    deleted++;
                }
                catch (IOException)
                {
                    // 文件被占用：先尝试登记为重启删除（需管理员），成功计为待重启，否则计为占用失败
                    if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                    else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); }
                }
                catch (UnauthorizedAccessException) { denied++; Debug.WriteLine($"权限不足，跳过 {file.FullName}"); }
                catch (Exception ex) { other++; Debug.WriteLine($"删除文件失败 {file.FullName}: {ex.Message}"); }
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
        private static CleanResult CleanOldFiles(string path, string pattern, int ageDays)
        {
            if (!Directory.Exists(path)) return default;

            long freed = 0;
            int deleted = 0, locked = 0, denied = 0, other = 0, pendingReboot = 0;
            var cutoff = DateTime.Now.AddDays(-ageDays);

            try
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                {
                    if (file.LastWriteTime >= cutoff) continue;
                    try
                    {
                        long len = file.Length;
                        file.Delete();
                        // 删除成功才计入释放量
                        freed += len;
                        deleted++;
                    }
                    catch (IOException)
                    {
                        if (ScheduleDeleteOnReboot(file.FullName)) pendingReboot++;
                        else { locked++; Debug.WriteLine($"文件被占用，跳过 {file.FullName}"); }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        denied++;
                        Debug.WriteLine($"权限不足，跳过 {file.FullName}");
                    }
                    catch (Exception ex)
                    {
                        other++;
                        Debug.WriteLine($"删除过期文件失败 {file.FullName}: {ex.Message}");
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
            bool wuauservStopped = RunCommand("net", "stop wuauserv", 30_000);
            bool bitsStopped = RunCommand("net", "stop bits", 30_000);
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
            progress?.Report("正在执行 cleanmgr /autoclean（系统磁盘清理，可能需要数分钟）...");
            bool ok = RunCommand("cleanmgr", "/autoclean", 3_600_000);
            return new CleanResult(0, ok ? 1 : 0, 0, 0, ok ? 0 : 1, 0);
        }

        /// <summary>
        /// 缩略图 + 图标缓存合并清理（同目录并存，iconcache_* 此前一直漏清）
        /// </summary>
        private static CleanResult CleanThumbAndIconCache(string path)
        {
            var thumbs = CleanFiles(path, "thumbcache_*");
            var icons = CleanFiles(path, "iconcache_*");
            return thumbs + icons;
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
                Arguments = "list shadowstorage /for=C:",
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

            var match = Regex.Match(output, @"(\d+(?:\.\d+)?)\s*(KB|MB|GB|TB)", RegexOptions.IgnoreCase);
            if (!match.Success) return (0, false);

            double value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
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
        var binRoot = @"C:\$Recycle.Bin";
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
        progress?.Report("正在缩减还原点存储上限到 3GB...");
        bool ok = RunCommand("vssadmin", "resize shadowstorage /for=C: /on=C: /maxsize=3GB", 120_000);
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

        // 驱动器边界检查：仅允许 C 盘
        if (!path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
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
                if (!dir.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
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
