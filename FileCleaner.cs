using System.Diagnostics;

namespace CacheCleaner;

internal sealed class CleaningCancelledException(CleanResult partial, CancellationToken token)
    : OperationCanceledException("已取消，当前项目已完成部分处理", token)
{
    internal CleanResult Partial { get; } = partial;
}

internal static class FileCleaner
{
    internal static (long Bytes, bool Complete) Measure(string path, CancellationToken token) =>
        MeasureFiles(path, "*", true, token);

    internal static (long Bytes, bool Complete) MeasureFiles(string path, string pattern, bool recursive, CancellationToken token)
    {
        long bytes = 0;
        bool complete = true;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { complete = false; continue; }
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern))
                {
                    token.ThrowIfCancellationRequested();
                    try { if ((file.Attributes & FileAttributes.ReparsePoint) == 0) bytes += file.Length; }
                    catch (IOException) { complete = false; }
                    catch (UnauthorizedAccessException) { complete = false; }
                }
                if (recursive)
                    foreach (var child in Directory.EnumerateDirectories(directory)) stack.Push(child);
            }
            catch (IOException) { complete = false; }
            catch (UnauthorizedAccessException) { complete = false; }
        }
        return (bytes, complete);
    }

    internal static void DeleteFile(FileInfo file)
    {
        var original = file.Attributes;
        bool readOnly = (original & FileAttributes.ReadOnly) != 0;
        if (readOnly) file.Attributes = original & ~FileAttributes.ReadOnly;
        try { file.Delete(); }
        catch
        {
            if (readOnly && File.Exists(file.FullName))
                try { File.SetAttributes(file.FullName, original); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static CleanResult Clean(string path, string pattern, bool recursive, DateTime? cutoff,
        bool removeEmptyDirectories, CancellationToken token, IProgress<string>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(path)) return default;
        if (PathProtection.HasLinkedAncestor(path) || PathProtection.IsProtected(path))
            return new CleanResult { SkippedCount = 1, Detail = "保留受保护路径或目录链接" };
        long freed = 0;
        int deleted = 0, locked = 0, denied = 0, other = 0, pending = 0, protectedCount = 0, readOnlyDeleted = 0;
        CleanResult Result() => new(freed, deleted, locked, denied, other, pending)
        { Detail = $"已处理只读文件 {readOnlyDeleted} 个；保留受保护路径 {protectedCount} 个" };
        void CheckCancellation()
        {
            if (token.IsCancellationRequested)
                throw new CleaningCancelledException(Result(), token);
        }
        var visited = new List<string>();
        var stack = new Stack<string>();
        stack.Push(path);
        var watch = Stopwatch.StartNew();
        long lastReport = -1;
        void Failure(string entry, Exception error)
        {
            if (error is UnauthorizedAccessException) denied++; else other++;
            CleanLog.NoteFailure($"无法处理 {entry}: {error.Message}");
        }
        while (stack.TryPop(out var directory))
        {
            CheckCancellation();
            try
            {
                if (PathProtection.IsProtected(directory)) { protectedCount++; continue; }
                // 不沿 junction/symlink 越过选中目录的边界。
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                { CleanLog.NoteFailure($"保留目录链接: {directory}"); continue; }
                visited.Add(directory);
                foreach (var info in new DirectoryInfo(directory).EnumerateFiles(pattern))
                {
                    CheckCancellation();
                    var file = info.FullName;
                    try
                    {
                        // 只读预览复用枚举元数据；删除或判断保留期前刷新，避免误删新文件。
                        if (!CacheScanner.DryRun || cutoff.HasValue) info.Refresh();
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (PathProtection.IsProtected(file)) { protectedCount++; continue; }
                        if (cutoff.HasValue && info.LastWriteTime >= cutoff.Value) continue;
                        long size = info.Length;
                        bool readOnly = info.IsReadOnly;
                        if (!CacheScanner.DryRun) DeleteFile(info);
                        if (readOnly) readOnlyDeleted++;
                        freed += size; deleted++;
                    }
                    catch (UnauthorizedAccessException ex) { Failure(file, ex); }
                    catch (IOException ex)
                    {
                        bool sharingViolation = (ex.HResult & 0xffff) is 32 or 33;
                        if (sharingViolation && !CacheScanner.DryRun && CacheScanner.ScheduleDeleteOnReboot(file)) pending++;
                        else if (sharingViolation) { locked++; CleanLog.NoteFailure($"文件被占用 {file}: {ex.Message}"); }
                        else { other++; CleanLog.NoteFailure($"文件读写失败 {file}: {ex.Message}"); }
                    }
                    if (watch.ElapsedMilliseconds / 500 != lastReport)
                    {
                        lastReport = watch.ElapsedMilliseconds / 500;
                        progress?.Report($"已处理 {deleted} 个文件 · {CacheScanner.FormatSize(freed)} · {Path.GetFileName(directory)}");
                    }
                }
                if (recursive)
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    { CheckCancellation(); stack.Push(child); }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) { Failure(directory, ex); }
            catch (UnauthorizedAccessException ex) { Failure(directory, ex); }
        }
        if (removeEmptyDirectories && !CacheScanner.DryRun)
            foreach (var directory in visited.AsEnumerable().Reverse())
            {
                CheckCancellation();
                if (directory == path) continue;
                try { Directory.Delete(directory, false); }
                catch (IOException) { /* 保留仍有文件的目录；不递归删除未处理文件。 */ }
                catch (UnauthorizedAccessException ex) { Failure(directory, ex); }
            }
        return Result();
    }
}
