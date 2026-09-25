using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CacheCleaner;

/// <summary>
/// 声明式清理规则（JSON）：
/// - 默认规则内嵌为 rules.default.json，随程序发布；
/// - 可在 exe 同目录或 %LOCALAPPDATA%\CacheCleaner\ 放 rules.user.json 新增/按名称覆盖规则，
///   无需重新编译（对标 Winapp2.ini 的规则库思路）。过程性扫描器仍留在代码中。
/// 规则字段：
///   name/base(localAppData|appData|userProfile|windows|programData|driveRoot)/path/desc
///   risk(safe|warn|danger)/kind(path|file|special)/clean(directory|files|reportOnly)
///   filesPatterns/minAgeDays/skipSize/guardProcesses
/// </summary>
internal static class CleaningRules
{
    private static IReadOnlyList<CleaningRule>? _all;

    public static IReadOnlyList<CleaningRule> All => _all ??= Load();

    /// <summary>按名称精确查规则（清理端据此取清理方式与进程守卫）</summary>
    public static CleaningRule? Find(string name) =>
        All.FirstOrDefault(r => r.Name.Equals(name, StringComparison.Ordinal));

    private static IReadOnlyList<CleaningRule> Load()
    {
        var rules = new List<CleaningRule>();

        // 1) 内嵌默认规则——解析失败也要兜住，宁可少显示条目不可崩溃
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("CacheCleaner.rules.default.json");
            if (stream != null)
            {
                var embedded = JsonSerializer.Deserialize<List<CleaningRule>>(stream, JsonOptions);
                if (embedded != null) rules.AddRange(embedded);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"解析内嵌规则失败: {ex.Message}");
        }

        // 2) 侧车规则：exe 同目录（便携场景）与用户目录（安装场景），同名覆盖、新名追加；
        //    侧车写错绝不影响正常扫描
        var sidecars = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "rules.user.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CacheCleaner", "rules.user.json")
        };
        foreach (var sidecar in sidecars)
        {
            if (!File.Exists(sidecar)) continue;
            try
            {
                var userRules = JsonSerializer.Deserialize<List<CleaningRule>>(File.ReadAllText(sidecar), JsonOptions);
                if (userRules == null) continue;

                foreach (var rule in userRules)
                {
                    rules.RemoveAll(r => r.Name.Equals(rule.Name, StringComparison.Ordinal));
                    rules.Add(rule);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"解析侧车规则失败 {sidecar}: {ex.Message}");
            }
        }

        // 3) Agent 目录：按收录数据编译 agent 规则（同名以先前来源优先）
        foreach (var rule in AgentCatalog.CompileRules())
        {
            rules.RemoveAll(r => r.Name.Equals(rule.Name, StringComparison.Ordinal));
            rules.Add(rule);
        }

        return rules;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>单条声明式清理规则（字段说明见 CleaningRules 类注释）</summary>
internal sealed class CleaningRule
{
    public string Name { get; set; } = "";
    public BaseFolder Base { get; set; } = BaseFolder.LocalAppData;
    public string Path { get; set; } = "";
    public string Desc { get; set; } = "";
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;

    /// <summary>path=基准目录+相对路径（默认）；file=单文件；special=按名称分发的特殊项</summary>
    public string Kind { get; set; } = "path";

    /// <summary>directory=清空目录内容（默认）；files=按 filesPatterns 清匹配文件；reportOnly=只报告不清理</summary>
    public string Clean { get; set; } = "directory";

    public List<string>? FilesPatterns { get; set; }

    /// <summary>files 模式下仅清理早于该天数的文件（如 CBS 日志 30 天）</summary>
    public int MinAgeDays { get; set; }

    /// <summary>files 模式递归子目录（默认仅顶层）</summary>
    public bool Recursive { get; set; }

    /// <summary>true 时跳过体积统计（WinSxS：硬链接虚高且全量遍历极慢）</summary>
    public bool SkipSize { get; set; }

    /// <summary>clean=command 时执行的官方清理命令（如 pnpm store prune / go clean -modcache），经 cmd /c 解析 .cmd 垫片</summary>
    public string? Command { get; set; }

    /// <summary>命令式清理的超时（毫秒），默认 10 分钟</summary>
    public int CommandTimeoutMs { get; set; } = 600_000;

    /// <summary>清理前检查的进程名，任一运行中即跳过该项（如浏览器运行时其缓存必然被锁）</summary>
    public List<string>? GuardProcesses { get; set; }
}
