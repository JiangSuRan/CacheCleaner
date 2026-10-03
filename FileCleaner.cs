using System.Diagnostics;

namespace CacheCleaner;

internal sealed class CleaningCancelledException(CleanResult partial, CancellationToken token)
    : OperationCanceledException("已取消，当前项目已完成部分处理", token)
{
    internal CleanResult Partial { get; } = partial;
}

internal static class FileCleaner
{
    internal static (long Bytes, bool Complete) Measure(string path, CancellationToken token)
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
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    try { bytes += new FileInfo(file).Length; }
                    catch (IOException) { complete = false; }
                    catch (UnauthorizedAccessException) { complete = false; }
                }
                foreach (var child in Directory.EnumerateDirectories(directory)) stack.Push(child);
            }
            catch (IOException) { complete = false; }
            catch (UnauthorizedAccessException) { complete = false; }
        }
        return (bytes, complete);
    }

    internal static CleanResult Clean(string path, string pattern, bool recursive, DateTime? cutoff,
        bool removeEmptyDirectories, CancellationToken token, IProgress<string>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(path)) return default;
        long freed = 0;
        int deleted = 0, locked = 0, denied = 0, other = 0, pending = 0;
        void CheckCancellation()
        {
            if (token.IsCancellationRequested)
                throw new CleaningCancelledException(new CleanResult(freed, deleted, locked, denied, other, pending), token);
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
                // 不沿 junction/symlink 越过选中目录的边界。
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                { CleanLog.NoteFailure($"保留目录链接: {directory}"); continue; }
                visited.Add(directory);
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                {
                    CheckCancellation();
                    try
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (cutoff.HasValue && info.LastWriteTime >= cutoff.Value) continue;
                        long size = info.Length;
                        if (!CacheScanner.DryRun) info.Delete();
                        freed += size; deleted++;
                    }
                    catch (UnauthorizedAccessException ex) { Failure(file, ex); }
                    catch (IOException ex)
                    {
                        if (!CacheScanner.DryRun && CacheScanner.ScheduleDeleteOnReboot(file)) pending++;
                        else { locked++; CleanLog.NoteFailure($"被占用或无法删除 {file}: {ex.Message}"); }
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
        return new CleanResult(freed, deleted, locked, denied, other, pending);
    }
}
