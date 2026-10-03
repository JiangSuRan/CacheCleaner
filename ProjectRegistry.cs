using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CacheCleaner;

/// <summary>
/// 项目工程产物注册表（设计见 docs/optimization-plan-v5.md）：
/// 按标记文件识别项目类型（node/python/rust/dotnet/maven/gradle/dart/php），
/// 每种类型声明可再生的产物目录（node_modules/.venv/target/bin/obj...）与再生成命令。
/// 「可再生」= 硬编码白名单（kondo/npkill 的社区共识做法），只删注册表精确路径。
/// </summary>
internal static class ProjectRegistry
{
    private static IReadOnlyList<ProjectType> _all = Load();

    public static IReadOnlyList<ProjectType> All => _all;

    private static IReadOnlyList<ProjectType> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("CacheCleaner.projects.registry.json");
            if (stream == null) return [];
            var parsed = JsonSerializer.Deserialize<ProjectRegistryFile>(stream, CleaningRules.JsonOptions);
            return parsed?.ProjectTypes ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"解析项目注册表失败: {ex.Message}");
            return [];
        }
    }
}

internal sealed class ProjectRegistryFile
{
    public List<ProjectType> ProjectTypes { get; set; } = [];
}

/// <summary>一种项目类型：标记文件 + 可再生产物清单</summary>
internal sealed class ProjectType
{
    public string Id { get; set; } = "";

    /// <summary>主标记文件（目录下存在该文件即认定为项目根）</summary>
    public string Marker { get; set; } = "";

    /// <summary>附加标记文件（任一命中即认定）</summary>
    public List<string> Markers { get; set; } = [];

    /// <summary>按后缀匹配的标记（如 .sln）</summary>
    public List<string> MarkersSuffix { get; set; } = [];

    public List<ProjectArtifact> Artifacts { get; set; } = [];
}

/// <summary>项目根下一个可再生产物（目录或单文件）</summary>
internal sealed class ProjectArtifact
{
    public string Path { get; set; } = "";

    /// <summary>再生成命令（写入条目描述，让用户删之前知道怎么恢复）</summary>
    public string Regenerate { get; set; } = "";

    /// <summary>单文件产物（如 .eslintcache），默认为目录</summary>
    public bool File { get; set; }
}
