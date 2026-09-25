using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CacheCleaner;

/// <summary>
/// Agent 目录：主流 agent 的存储布局收录（设计见 docs/agent-catalog.md）。
/// 每个条目 = 身份 + 探测签名（detect）+ 分类子目录（items）+ neverTouch 白名单；
/// 条目在加载时编译为现有规则执行（home 不存在时条目自然隐藏）。
/// 收录标准：有官方文档或源码级证据；语义未核验的 agent 只进「未收录报告」。
/// </summary>
internal static class AgentCatalog
{
    private static IReadOnlyList<CatalogAgent>? _all;

    public static IReadOnlyList<CatalogAgent> All => _all ??= Load();

    /// <summary>把收录条目编译为现有规则（同名规则以先加载的来源优先）</summary>
    public static IEnumerable<CleaningRule> CompileRules()
    {
        foreach (var agent in All)
        {
            foreach (var item in agent.Items)
            {
                yield return new CleaningRule
                {
                    Name = item.Name,
                    Base = agent.HomeBase,
                    Path = Path.Combine(agent.Home, item.Path),
                    Desc = item.Desc,
                    Risk = item.Risk,
                    Clean = item.Clean,
                    FilesPatterns = item.FilesPatterns,
                    MinAgeDays = item.MinAgeDays,
                    Recursive = item.Recursive,
                    SkipSize = item.SkipSize,
                    Command = item.Command,
                    CommandTimeoutMs = item.CommandTimeoutMs
                };
            }
        }
    }

    private static IReadOnlyList<CatalogAgent> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("CacheCleaner.agents.catalog.json");
            if (stream == null) return [];
            var parsed = JsonSerializer.Deserialize<CatalogFile>(stream, CleaningRules.JsonOptions);
            return parsed?.Agents ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"解析 agent 目录失败: {ex.Message}");
            return [];
        }
    }
}

internal sealed class CatalogFile
{
    public List<CatalogAgent> Agents { get; set; } = [];
}

/// <summary>单个 agent 的收录条目</summary>
internal sealed class CatalogAgent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";

    /// <summary>home 的基准目录（多数为 userProfile dot-home）</summary>
    public BaseFolder HomeBase { get; set; } = BaseFolder.UserProfile;

    /// <summary>相对基准目录的 home 路径</summary>
    public string Home { get; set; } = "";

    /// <summary>探测签名：home 下任一特征文件存在即认定该 agent 在本机启用过</summary>
    public List<string> Detect { get; set; } = [];

    /// <summary>置信分级：verified / documented / source / inferred-fork / unverified</summary>
    public string Confidence { get; set; } = "";

    /// <summary>最近核实日期（布局漂移复核依据）</summary>
    public string Verified { get; set; } = "";

    public string Source { get; set; } = "";

    /// <summary>绝不触碰的白名单（凭证/配置/用户数据），生成规则时强制跳过</summary>
    public List<string> NeverTouch { get; set; } = [];

    public List<CatalogItem> Items { get; set; } = [];

    /// <summary>home 的绝对路径（探测与覆盖判定用）</summary>
    public string? HomeFullPath => HomeBase switch
    {
        BaseFolder.UserProfile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Home),
        BaseFolder.LocalAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Home),
        BaseFolder.AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Home),
        BaseFolder.ProgramData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Home),
        BaseFolder.Windows => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Home),
        _ => Path.Combine(CacheScanner.SysRoot, Home)
    };
}

/// <summary>agent 下单个子目录的收录条目（字段与 CleaningRule 对齐，编译为规则）</summary>
internal sealed class CatalogItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>分类标签（cache | sessions | models），文档用途；实际语义由下列字段表达</summary>
    public string Class { get; set; } = "";

    public string Desc { get; set; } = "";
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;
    public string Clean { get; set; } = "directory";
    public List<string>? FilesPatterns { get; set; }
    public int MinAgeDays { get; set; }
    public bool Recursive { get; set; }
    public bool SkipSize { get; set; }
    public string? Command { get; set; }
    public int CommandTimeoutMs { get; set; } = 600_000;
}
