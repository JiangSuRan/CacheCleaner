using System.Diagnostics;

namespace CacheCleaner;

internal sealed class DiskUsageEntry
{
    public required string Path { get; init; }
    public bool IsFile { get; init; }
    public long Bytes { get; set; }
    public long DirectFileBytes { get; set; }
    public int Unreadable { get; set; }
    public int LinksSkipped { get; set; }
    public List<DiskUsageEntry> Children { get; } = [];
}

internal sealed record DiskUsageResult(DiskUsageEntry Root, IReadOnlyList<DiskUsageEntry> LargestFiles, long Files, long Directories);

internal static class DiskUsageScanner
{
    internal static DiskUsageResult Scan(string root, IProgress<string>? progress, CancellationToken token)
    {
        root = System.IO.Path.GetFullPath(root);
        if (PathProtection.HasLinkedAncestor(root)) throw new IOException("请选择实际目录，不能使用目录链接。");
        var top = new DiskUsageEntry { Path = root };
        var stack = new Stack<(DiskUsageEntry Entry, bool Finish)>();
        stack.Push((top, false));
        var largest = new PriorityQueue<DiskUsageEntry, long>();
        var watch = Stopwatch.StartNew();
        long files = 0, directories = 0;
        while (stack.TryPop(out var work))
        {
            token.ThrowIfCancellationRequested();
            var entry = work.Entry;
            if (work.Finish)
            {
                foreach (var child in entry.Children)
                {
                    entry.Bytes += child.Bytes;
                    entry.Unreadable += child.Unreadable;
                    entry.LinksSkipped += child.LinksSkipped;
                }
                continue;
            }
            directories++;
            stack.Push((entry, true));
            try
            {
                // 保留已读文件的大小，单个不可访问条目不会让整个目录归零。
                foreach (var info in new DirectoryInfo(entry.Path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = info.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            entry.Children.Add(new DiskUsageEntry { Path = info.FullName, IsFile = true, LinksSkipped = 1 });
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var child = new DiskUsageEntry { Path = info.FullName };
                            entry.Children.Add(child);
                            stack.Push((child, false));
                        }
                        else
                        {
                            var file = new DiskUsageEntry { Path = info.FullName, IsFile = true, Bytes = ((FileInfo)info).Length };
                            entry.Bytes += file.Bytes;
                            entry.DirectFileBytes += file.Bytes;
                            files++;
                            largest.Enqueue(file, file.Bytes);
                            if (largest.Count > 100) largest.Dequeue();
                        }
                    }
                    catch (IOException) { entry.Children.Add(new DiskUsageEntry { Path = info.FullName, Unreadable = 1 }); }
                    catch (UnauthorizedAccessException) { entry.Children.Add(new DiskUsageEntry { Path = info.FullName, Unreadable = 1 }); }
                }
            }
            catch (IOException) { entry.Unreadable++; }
            catch (UnauthorizedAccessException) { entry.Unreadable++; }
            if (watch.ElapsedMilliseconds >= 200)
            {
                progress?.Report($"已扫描 {directories:N0} 个目录 / {files:N0} 个文件 · {entry.Path}");
                watch.Restart();
            }
        }
        return new DiskUsageResult(top, largest.UnorderedItems.Select(i => i.Element).OrderByDescending(i => i.Bytes).ToList(), files, directories);
    }
}
