using System.Diagnostics;
using System.Text;

namespace CacheCleaner;

internal static class CommandRunner
{
    internal static (bool Ok, string Output) Run(string fileName, string arguments, int timeoutMs,
        CancellationToken token = default, IProgress<string>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        var output = new StringBuilder();
        var gate = new object();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        bool detached = false;
        async Task Drain(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                    lock (gate)
                        if (output.Length < 65536) output.AppendLine(line[..Math.Min(line.Length, 65536 - output.Length)]);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        void LeaveRunning()
        {
            detached = true;
            CleanLog.NoteFailure($"命令仍在后台运行（PID {process.Id}）: {fileName} {arguments}");
            // 系统维护命令不能强杀；保持输出读取直至自然退出，避免再次堵住管道。
            _ = Task.Run(async () =>
            {
                try { await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
                finally { process.Dispose(); }
            });
        }
        try
        {
            process.Start();
            stdout = Drain(process.StandardOutput);
            stderr = Drain(process.StandardError);
            var watch = Stopwatch.StartNew();
            long lastUpdate = -1;
            while (!process.WaitForExit(100))
            {
                if (token.IsCancellationRequested)
                {
                    LeaveRunning();
                    throw new OperationCanceledException($"已停止等待 {fileName}；命令仍在后台运行（PID {process.Id}）", token);
                }
                if (watch.ElapsedMilliseconds >= timeoutMs)
                {
                    LeaveRunning();
                    return (false, $"等待 {fileName} 超时，命令仍在后台运行（PID {process.Id}）");
                }
                if (watch.Elapsed.Seconds != lastUpdate)
                {
                    lastUpdate = watch.Elapsed.Seconds;
                    progress?.Report($"{fileName} 执行中 · 已用时 {watch.Elapsed:hh\\:mm\\:ss}");
                }
            }
            // 子进程可能继承管道句柄；主进程退出后也不能无限等待 ReadToEnd。
            Task.WaitAll([stdout, stderr], 1000);
            lock (gate) return (process.ExitCode == 0, output.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message); }
        finally { if (!detached) process.Dispose(); }
    }
}
