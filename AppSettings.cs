using System.Diagnostics;
using System.Text.Json;

namespace CacheCleaner;

/// <summary>
/// 应用设置持久化（%LOCALAPPDATA%\CacheCleaner\settings.json）：
/// 预览模式开关 + 上次清理时用户勾选的条目名（下次扫描按记录恢复勾选）。
/// 读写失败静默降级为默认行为，绝不影响主流程。
/// </summary>
internal static class AppSettings
{
    public static bool PreviewMode { get; set; }

    /// <summary>上次勾选的条目名集合；HasCheckedSnapshot=false 时按风险等级自动勾选</summary>
    public static HashSet<string> CheckedNames { get; } = new(StringComparer.Ordinal);

    /// <summary>是否已存在用户勾选记录（区分「首次使用」与「用户清空了全部勾选」）</summary>
    public static bool HasCheckedSnapshot { get; private set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CacheCleaner", "settings.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(FilePath));
            if (dto == null) return;

            PreviewMode = dto.PreviewMode;
            HasCheckedSnapshot = dto.HasCheckedSnapshot;
            CheckedNames.Clear();
            foreach (var name in dto.CheckedNames) CheckedNames.Add(name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取设置失败: {ex.Message}");
        }
    }

    /// <summary>保存预览开关与当前勾选集合（以本次传入为准整体覆盖）</summary>
    public static void Save(bool previewMode, IEnumerable<string> checkedNames)
    {
        try
        {
            PreviewMode = previewMode;
            CheckedNames.Clear();
            foreach (var name in checkedNames) CheckedNames.Add(name);
            HasCheckedSnapshot = true;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(new SettingsDto(PreviewMode, HasCheckedSnapshot, CheckedNames.ToList()),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存设置失败: {ex.Message}");
        }
    }

    private sealed record SettingsDto(bool PreviewMode, bool HasCheckedSnapshot, List<string> CheckedNames);
}
