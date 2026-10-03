using CacheCleaner;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;

class Regression
{
    static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static object Invoke(string type, string method, params object[] args)
    {
        try { return typeof(CacheScanner).Assembly.GetType("CacheCleaner." + type)!.GetMethod(method, Hidden)!.Invoke(null, args)!; }
        catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }
    static void Assert(bool ok, string description) { if (!ok) throw new Exception(description); Console.WriteLine("PASS " + description); }
    static (bool Ok, string Output) Command(string argument, int timeout, CancellationToken token = default) =>
        ((bool, string))Invoke("CommandRunner", "Run", Environment.ProcessPath!, argument, timeout, token, null!);
    static CleanResult Clean(string path, bool preview, DateTime? cutoff = null, CancellationToken token = default, IProgress<string>? progress = null)
    {
        CacheScanner.DryRun = preview;
        return (CleanResult)Invoke("FileCleaner", "Clean", path, "*", true, (object?)cutoff!, true, token, progress!);
    }
    sealed class ImmediateProgress(Action<string> action) : IProgress<string> { public void Report(string text) => action(text); }
    [STAThread] static int Main(string[] args)
    {
        if (args.Contains("--flood"))
        {
            for (int i = 0; i < 12000; i++) { Console.Out.WriteLine(new string('o',128)); Console.Error.WriteLine(new string('e',128)); }
            return 0;
        }
        if (args.Contains("--sleep")) { Thread.Sleep(1800); return 0; }
        if (args.Contains("--failure")) { Console.Error.WriteLine("fixture-error"); return 3; }
        if (args.Contains("--ui")) return TestUi();
        string fixtures = Path.Combine(Path.GetDirectoryName(typeof(CacheScanner).Assembly.Location)!, "regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        try
        {
            var watch = Stopwatch.StartNew();
            var flood = Command("--flood", 15000);
            Assert(flood.Ok && watch.Elapsed.TotalSeconds < 15, "大量 stdout/stderr 不堵塞进程");
            Assert(flood.Output.Length < 66000, "命令输出有内存上限");
            var failure = Command("--failure", 3000);
            Assert(!failure.Ok && failure.Output.Contains("fixture-error"), "错误退出保留 stderr");
            watch.Restart();
            Assert(!Command("--sleep", 150).Ok && watch.ElapsedMilliseconds < 1200, "命令超时按时返回");
            using (var cancel = new CancellationTokenSource(150))
            {
                watch.Restart();
                try { Command("--sleep", 10000, cancel.Token); throw new Exception("未取消"); }
                catch (OperationCanceledException ex) { Assert(watch.ElapsedMilliseconds < 1200 && ex.Message.Contains("后台"), "取消命令等待保留后台说明"); }
            }
            string previewDir = Directory.CreateDirectory(Path.Combine(fixtures,"preview")).FullName;
            Directory.CreateDirectory(Path.Combine(previewDir,"empty"));
            File.WriteAllBytes(Path.Combine(previewDir,"old.bin"), new byte[100]);
            File.SetLastWriteTime(Path.Combine(previewDir,"old.bin"), DateTime.Now.AddDays(-10));
            File.WriteAllBytes(Path.Combine(previewDir,"new.bin"), new byte[30]);
            var preview = Clean(previewDir,true,DateTime.Now.AddDays(-7));
            Assert(preview.FreedBytes == 100 && File.Exists(Path.Combine(previewDir,"old.bin")), "预览只统计符合保留期的文件");
            Assert(Directory.Exists(Path.Combine(previewDir,"empty")), "预览不删除空目录");
            var actual = Clean(previewDir,false,DateTime.Now.AddDays(-7));
            Assert(actual.FreedBytes == 100 && !File.Exists(Path.Combine(previewDir,"old.bin")) && File.Exists(Path.Combine(previewDir,"new.bin")), "真实删除保留最近文件");
            string recursive = Directory.CreateDirectory(Path.Combine(fixtures,"recursive","child")).FullName;
            File.WriteAllBytes(Path.Combine(recursive,"data"),new byte[50]);
            Assert(Clean(Path.GetDirectoryName(recursive)!,false).FreedBytes == 50 && !Directory.Exists(recursive), "递归删除准确计量并移除空子目录");
            string retained = Directory.CreateDirectory(Path.Combine(fixtures,"retained")).FullName;
            string readOnly = Path.Combine(retained,"readonly"); File.WriteAllText(readOnly,"retain"); File.SetAttributes(readOnly,FileAttributes.ReadOnly);
            File.WriteAllBytes(Path.Combine(retained,"allowed"),new byte[20]);
            var partial = Clean(retained,false);
            Assert(partial.PermissionDeniedCount == 1 && partial.FreedBytes == 20 && File.Exists(readOnly), "失败文件如实统计，其他文件继续清理");
            var measure = ((long Bytes,bool Complete))Invoke("FileCleaner","Measure",retained,CancellationToken.None);
            Assert(measure.Complete && measure.Bytes == 6,"剩余文件大小核实");
            var retainedItem = new CacheItem { Name="[项目] regression", Path=retained, Exists=true, SizeBytes=200 };
            var retainedResult = CacheScanner.CleanItem(retainedItem);
            Assert(retainedResult.PermissionDeniedCount == 1 && retainedItem.SizeBytes == 6,"CleanItem 保留真实残留大小，不误报零");
            string versions=Directory.CreateDirectory(Path.Combine(fixtures,"versions")).FullName;
            string oldVersion=Directory.CreateDirectory(Path.Combine(versions,"old")).FullName;
            string newest=Directory.CreateDirectory(Path.Combine(versions,"new")).FullName;
            File.WriteAllBytes(Path.Combine(oldVersion,"delete"),new byte[15]);
            File.WriteAllText(Path.Combine(oldVersion,"retain"),"retain");
            File.SetAttributes(Path.Combine(oldVersion,"retain"),FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(newest,"program"),"newest");
            Directory.SetLastWriteTime(oldVersion,DateTime.Now.AddDays(-2));
            Directory.SetLastWriteTime(newest,DateTime.Now);
            var versionResult=(CleanResult)Invoke("CacheScanner","CleanKeepNewest",versions,CancellationToken.None);
            Assert(versionResult.FreedBytes == 15 && versionResult.PermissionDeniedCount == 1 && File.Exists(Path.Combine(newest,"program")),"旧版本部分失败仍清理其他文件且保留最新版本");
            string cancelling = Directory.CreateDirectory(Path.Combine(fixtures,"cancel")).FullName;
            for(int i=0;i<5;i++) File.WriteAllBytes(Path.Combine(cancelling,i.ToString()),new byte[10]);
            using(var cancel = new CancellationTokenSource())
            {
                try { Clean(cancelling,false,null,cancel.Token,new ImmediateProgress(_ => cancel.Cancel())); throw new Exception("未取消"); }
                catch(OperationCanceledException ex)
                {
                    var partialResult=(CleanResult)ex.GetType().GetProperty("Partial",Hidden)!.GetValue(ex)!;
                    Assert(partialResult.FreedBytes == 10 && Directory.GetFiles(cancelling).Length == 4,"取消保留部分完成计量且停止后续删除");
                }
            }
            CacheScanner.DryRun = false;
            var report = new CacheItem { Name="未收录工具目录 (fixture)", Path=retained, Exists=true, SizeBytes=6 };
            Assert(!CacheScanner.CanClean(report) && CacheScanner.CleanItem(report).SkippedCount == 1 && File.Exists(readOnly),"只读报告不可清理且明确跳过");
            Console.WriteLine("REGRESSION_OK"); return 0;
        }
        finally
        {
            CacheScanner.DryRun = false;
            foreach(var file in Directory.EnumerateFiles(fixtures,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
            Directory.Delete(fixtures,true);
        }
    }
    static int TestUi()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        var form = new MainForm { TopMost = true }; form.Show(); form.Activate();
        for(int i=0;i<10;i++) { Application.DoEvents(); Thread.Sleep(50); }
        object Call(string name, params object[] values) => typeof(MainForm).GetMethods(Hidden).Single(m => m.Name==name && m.DeclaringType==typeof(MainForm)).Invoke(form,values)!;
        T Field<T>(string name) => (T)typeof(MainForm).GetField(name,Hidden)!.GetValue(form)!;
        Call("AddCacheRow",new CacheItem { Name="未收录工具目录 (fixture)", Exists=true, SizeBytes=100 },true);
        Call("AddCacheRow",new CacheItem { Name="[项目] fixture", Exists=true, SizeBytes=200 },true);
        Call("SetAllChecked",true);
        var grid=Field<DataGridView>("dgv");
        Assert(grid.Rows[0].Cells["Checked"].Value is false && grid.Rows[0].Cells["Checked"].ReadOnly,"全选排除只读报告");
        Assert(grid.Rows[1].Cells["Checked"].Value is true && grid.Rows[1].Tag is CacheItem { Name:"[项目] fixture" },"行保留原始清理身份");
        Assert(grid.Columns.Contains("Result"),"处理结果独立列");
        Call("SetState",Enum.Parse(typeof(MainForm).Assembly.GetType("CacheCleaner.UiState")!,"Cleaning"),"清理中","fixture");
        Call("SetControlsEnabled",false);
        Assert(!grid.Enabled && Field<Control>("btnCancel").Enabled,"清理时锁定表格并允许取消");
        Call("SetState",Enum.Parse(typeof(MainForm).Assembly.GetType("CacheCleaner.UiState")!,"CleanCompleted"),"清理完成","fixture");
        Call("SetControlsEnabled",true);
        Assert(grid.Enabled && !Field<Control>("btnCancel").Enabled,"完成恢复可操作状态");
        form.Refresh(); Application.DoEvents();
        using(var bitmap = new Bitmap(form.Width,form.Height))
        {
            using var graphics=Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(form.Location,Point.Empty,form.Size);
            bitmap.Save(Path.Combine(AppContext.BaseDirectory,"ui-check.png"));
        }
        Console.WriteLine("UI_REGRESSION_OK");
        // 不触发主窗体关闭时的设置保存。
        Environment.Exit(0); return 0;
    }
}
