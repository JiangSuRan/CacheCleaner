using System.Diagnostics;

namespace CacheCleaner;

/// <summary>
/// 清理审计日志：落盘到 %LOCALAPPDATA%\CacheCleaner\logs\clean-yyyyMMdd.log。
/// 「清理效果不理想」类问题的第一手诊断材料：逐项释放量、失败明细、C 盘可用空间前后差。
/// 日志写入自身失败绝不影响清理流程。
/// </summary>
public static class CleanLog
{
    private static readonly object Gate = new();
    private static readonly List<string> FailureNotes = new();

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CacheCleaner", "logs");

    /// <summary>登记一条失败明细（被占用/权限不足等），随清理汇总一并落盘</summary>
    public static void NoteFailure(string note)
    {
        lock (Gate)
        {
            // 失败可能成千上万（如整个目录被锁），封顶防止内存膨胀
            if (FailureNotes.Count < 500) FailureNotes.Add(note);
        }
    }

    /// <summary>记录一次扫描的全部条目（大小、风险、路径）</summary>
    public static void LogScan(IReadOnlyList<CacheItem> items)
    {
        var lines = new List<string>
        {
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 扫描：{items.Count} 项 ==="
        };
        foreach (var item in items)
        {
            lines.Add($"  {CacheScanner.FormatSize(item.SizeBytes),12}  [{item.Risk}]  {item.Name}  ->  {item.Path}");
        }
        Write(lines);
    }

    /// <summary>记录清理开始与起始可用空间</summary>
    public static void LogCleanStart(int itemCount, long freeBytesBefore)
    {
        Write(new List<string>
        {
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 清理开始：{itemCount} 项，C 盘可用 {CacheScanner.FormatSize(freeBytesBefore)} ==="
        });
    }

    /// <summary>记录单个条目的清理结果明细</summary>
    public static void LogCleanItem(string name, string path, CleanResult result)
    {
        Write(new List<string>
        {
            $"  [{DateTime.Now:HH:mm:ss}] {name}: 释放 {CacheScanner.FormatSize(result.FreedBytes)}，删除 {result.DeletedCount}，" +
            $"占用 {result.LockedCount}，权限 {result.PermissionDeniedCount}，其它失败 {result.OtherFailureCount}，待重启 {result.PendingRebootCount}  ({path})"
        });
    }

    /// <summary>记录清理汇总：总量、磁盘可用空间前后差、失败明细，并清空本轮失败缓存</summary>
    public static void LogCleanSummary(CleanResult total, long freeBefore, long freeAfter)
    {
        List<string> notes;
        lock (Gate)
        {
            notes = new List<string>(FailureNotes);
            FailureNotes.Clear();
        }

        var lines = new List<string>
        {
            $"  [{DateTime.Now:HH:mm:ss}] 清理汇总：文件累计 {CacheScanner.FormatSize(total.FreedBytes)}，" +
            $"C 盘可用 {CacheScanner.FormatSize(freeBefore)} -> {CacheScanner.FormatSize(freeAfter)}（净增 {CacheScanner.FormatSize(Math.Max(0, freeAfter - freeBefore))}）"
        };
        if (notes.Count > 0)
        {
            lines.Add($"  失败明细 {notes.Count} 条：");
            lines.AddRange(notes.Select(n => "    " + n));
        }
        Write(lines);
    }

    private static void Write(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var file = Path.Combine(LogDir, $"clean-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllLines(file, lines);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"审计日志写入失败: {ex.Message}");
        }
    }
}
