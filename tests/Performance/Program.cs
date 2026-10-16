using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

class Performance
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly App = null!;
    private static object? Call(string type, string method, params object?[] arguments)
    {
        try { return App.GetType("CacheCleaner." + type)!.GetMethod(method, Static)!.Invoke(null, arguments); }
        catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }
    static int Main(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("fixture|live assembly.dll output.json [root]");
        string output = Path.GetFullPath(args[2]);
        if (File.Exists(output)) throw new IOException("输出已存在，使用新文件名");
        App = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        object result = args[0] switch { "fixture" => Fixture(args[3]), "live" => Live(), _ => throw new ArgumentException("未知模式") };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
        return 0;
    }
    private static object Timed(string name, Func<object?> action)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocations = GC.GetTotalAllocatedBytes(true);
        var generations = Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
        var watch = Stopwatch.StartNew();
        var result = action();
        watch.Stop();
        process.Refresh();
        return new { Name = name, WallMs = watch.Elapsed.TotalMilliseconds,
            CpuMs = (process.TotalProcessorTime-cpu).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true)-allocations,
            GcCollections = Enumerable.Range(0,3).Select(i => GC.CollectionCount(i)-generations[i]).ToArray(),
            PeakWorkingSetBytes=process.PeakWorkingSet64, Result = result };
    }
    private static object Fixture(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var measurements = new List<object>();
        // 预热 JIT；数据集不随实现变动，结果必须一致。
        Call("FileCleaner", "Measure", root, CancellationToken.None);
        Call("CacheScanner", "SumFiles", root, "*", SearchOption.AllDirectories);
        for (int i=0;i<7;i++)
        {
            foreach (string operation in i%2==0 ? new[]{"Measure","SumFiles","Preview","Projects"} : new[]{"Projects","Preview","SumFiles","Measure"})
                measurements.Add(Timed(operation, () => operation switch
                {
                    "Measure" => NormalizeMeasure(Call("FileCleaner","Measure",root,CancellationToken.None)!),
                    "SumFiles" => Call("CacheScanner","SumFiles",root,"*",SearchOption.AllDirectories),
                    "Projects" => Projects(root),
                    _ => Preview(root)
                }));
        }
        return new { Kind="fixture", Environment=EnvironmentInfo(), Measurements=measurements };
    }
    private static object NormalizeMeasure(object value)
    {
        var type=value.GetType();
        return new { Bytes=(long)type.GetField("Item1")!.GetValue(value)!, Complete=(bool)type.GetField("Item2")!.GetValue(value)! };
    }
    private static object Projects(string root)
    {
        var types=App.GetType("CacheCleaner.ProjectRegistry")!.GetProperty("All", Static)!.GetValue(null);
        var list=(System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(App.GetType("CacheCleaner.CacheItem")!))!;
        Call("CacheScanner","WalkProjects",root,0,types,list,CancellationToken.None);
        return list.Cast<object>().Select(item => JsonSerializer.SerializeToElement(item,item.GetType())).ToArray();
    }
    private static object Preview(string root)
    {
        var scanner=App.GetType("CacheCleaner.CacheScanner")!;
        scanner.GetProperty("DryRun")!.SetValue(null,true);
        try { return Call("FileCleaner","Clean",root,"*",true,null,true,CancellationToken.None,null)!; }
        finally { scanner.GetProperty("DryRun")!.SetValue(null,false); }
    }
    private static object Live()
    {
        var diagnostics = App.GetType("CacheCleaner.ScanDiagnostics") == null ? null : (IDisposable?)Call("ScanDiagnostics","Begin");
        object known = null!, auto = null!;
        var measurement=Timed("CompleteScan", () =>
        {
            known=Call("CacheScanner","ScanAll",null,CancellationToken.None)!;
            auto=Call("CacheScanner","ScanAutoDiscovered",known,null,CancellationToken.None)!;
            var identities=((System.Collections.IEnumerable)known).Cast<object>()
                .Concat(((System.Collections.IEnumerable)auto).Cast<object>())
                .Select(item => string.Join("\0",new[]{"Name","Path","Risk","Exists"}.Select(name => item.GetType().GetProperty(name)!.GetValue(item)?.ToString())))
                .Order(StringComparer.Ordinal);
            return new { KnownCount=((System.Collections.ICollection)known).Count, AutoCount=((System.Collections.ICollection)auto).Count,
                IdentityHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",identities)))) };
        });
        diagnostics?.Dispose();
        return new { Kind="live-read-only", Environment=EnvironmentInfo(), Measurement=measurement,
            Diagnostics=diagnostics == null ? (JsonElement?)null : JsonSerializer.SerializeToElement(diagnostics,diagnostics.GetType()) };
    }
    private static object EnvironmentInfo() => new { Os=Environment.OSVersion.VersionString,
        Runtime=Environment.Version.ToString(), LogicalCpus=Environment.ProcessorCount,
        AppVersion=App.GetName().Version?.ToString(), Utc=DateTime.UtcNow };
}
