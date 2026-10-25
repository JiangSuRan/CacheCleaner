using CacheCleaner;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;

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
        if (args.Contains("--ui"))
        {
            try { return TestUi(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--disk-live"))
        {
            var watch = Stopwatch.StartNew();
            var scan = Invoke("DiskUsageScanner", "Scan", Path.GetPathRoot(Environment.SystemDirectory)!, null!, CancellationToken.None);
            var node = scan.GetType().GetProperty("Root")!.GetValue(scan)!;
            foreach (string property in new[] { "Bytes", "Unreadable", "LinksSkipped" })
                Console.WriteLine(property + "=" + node.GetType().GetProperty(property)!.GetValue(node));
            Console.WriteLine($"DISK_SCAN_OK elapsed={watch.Elapsed.TotalSeconds:F2}s files={scan.GetType().GetProperty("Files")!.GetValue(scan)} directories={scan.GetType().GetProperty("Directories")!.GetValue(scan)}");
            return 0;
        }
        string fixtures = Path.Combine(Path.GetDirectoryName(typeof(CacheScanner).Assembly.Location)!, "regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        try
        {
            // 仅在测试进程内增加规则，验证父目录清理不能绕过只读子规则。
            string reports = Directory.CreateDirectory(Path.Combine(fixtures, "report-parent", "models")).FullName;
            File.WriteAllText(Path.Combine(reports, "model"), "model");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(reports)!, "cache"), "cache");
            var assembly = typeof(CacheScanner).Assembly;
            var rulesType = assembly.GetType("CacheCleaner.CleaningRules")!;
            var rules = (System.Collections.IList)rulesType.GetProperty("All", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var testRule = Activator.CreateInstance(assembly.GetType("CacheCleaner.CleaningRule")!)!;
            testRule.GetType().GetProperty("Name")!.SetValue(testRule, "fixture report-only");
            testRule.GetType().GetProperty("Path")!.SetValue(testRule, reports);
            testRule.GetType().GetProperty("Clean")!.SetValue(testRule, "reportOnly");
            rules.Add(testRule);
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
            Assert(Clean(Path.GetDirectoryName(reports)!, false).FreedBytes == 5
                && File.Exists(Path.Combine(reports, "model")), "清理父目录保留 reportOnly 子目录");
            var actual = Clean(previewDir,false,DateTime.Now.AddDays(-7));
            Assert(actual.FreedBytes == 100 && !File.Exists(Path.Combine(previewDir,"old.bin")) && File.Exists(Path.Combine(previewDir,"new.bin")), "真实删除保留最近文件");
            string recursive = Directory.CreateDirectory(Path.Combine(fixtures,"recursive","child")).FullName;
            File.WriteAllBytes(Path.Combine(recursive,"data"),new byte[50]);
            Assert(Clean(Path.GetDirectoryName(recursive)!,false).FreedBytes == 50 && !Directory.Exists(recursive), "递归删除准确计量并移除空子目录");
            string retained = Directory.CreateDirectory(Path.Combine(fixtures,"retained")).FullName;
            string readOnly = Path.Combine(retained,"readonly"); File.WriteAllText(readOnly,"retain"); File.SetAttributes(readOnly,FileAttributes.ReadOnly);
            string history = Directory.CreateDirectory(Path.Combine(retained,"LocalHistory")).FullName;
            string protectedFile = Path.Combine(history,"unsaved"); File.WriteAllText(protectedFile,"retain");
            File.WriteAllBytes(Path.Combine(retained,"allowed"),new byte[20]);
            var readOnlyPreview = Clean(retained,true);
            Assert(File.GetAttributes(readOnly).HasFlag(FileAttributes.ReadOnly) && readOnlyPreview.FreedBytes == 26,"预览不改变只读属性，也不统计受保护子目录");
            var partial = Clean(retained,false);
            Assert(partial.PermissionDeniedCount == 0 && partial.FreedBytes == 26 && !File.Exists(readOnly) && File.Exists(protectedFile), "只读垃圾可以删除，父目录清理保留本地历史");
            var measure = ((long Bytes,bool Complete))Invoke("FileCleaner","Measure",retained,CancellationToken.None);
            Assert(measure.Complete && measure.Bytes == 6,"剩余文件大小核实");
            var retainedItem = new CacheItem { Name="[项目] regression", Path=retained, Exists=true, SizeBytes=200 };
            var retainedResult = CacheScanner.CleanItem(retainedItem);
            Assert(retainedResult.PermissionDeniedCount == 0 && retainedItem.SizeBytes == 6,"CleanItem 保留真实残留大小，不误报零");
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
            Assert(versionResult.FreedBytes == 21 && versionResult.PermissionDeniedCount == 0 && !Directory.Exists(oldVersion) && File.Exists(Path.Combine(newest,"program")),"清除旧版本的只读文件且保留最新版本");
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
            Assert(!CacheScanner.CanClean(report) && CacheScanner.CleanItem(report).SkippedCount == 1 && File.Exists(protectedFile),"只读报告不可清理且明确跳过");
            TestProjectMarkers(fixtures);
            TestCoverage(fixtures);
            Console.WriteLine("REGRESSION_OK"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            CacheScanner.DryRun = false;
            foreach(var file in Directory.EnumerateFiles(fixtures,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
            Directory.Delete(fixtures,true);
        }
    }
    static void TestCoverage(string fixtures)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixtures, "coverage")).FullName;
        File.WriteAllBytes(Path.Combine(root, "small"), new byte[19]);
        string blocked = Directory.CreateDirectory(Path.Combine(root, "blocked")).FullName;
        File.WriteAllBytes(Path.Combine(blocked, "secret"), new byte[30]);
        var info = new DirectoryInfo(blocked);
        var originalAcl = info.GetAccessControl();
        var deniedAcl = info.GetAccessControl();
        deniedAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ListDirectory, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(deniedAcl);
            Assert(CacheScanner.SumFiles(root) == 19, "不可读子目录不再导致整项统计归零");
            var scan = Invoke("DiskUsageScanner", "Scan", root, null!, CancellationToken.None);
            var node = scan.GetType().GetProperty("Root")!.GetValue(scan)!;
            Assert((long)node.GetType().GetProperty("Bytes")!.GetValue(node)! == 19
                && (int)node.GetType().GetProperty("Unreadable")!.GetValue(node)! > 0, "全盘扫描保留可读大小并报告不可读位置");
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(originalAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access);
            info.SetAccessControl(restored);
        }
        string locked = Path.Combine(root, "readonly-locked");
        File.WriteAllText(locked, "locked");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        using (var handle = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Invoke("FileCleaner", "DeleteFile", new FileInfo(locked)); throw new Exception("未报告占用"); }
            catch (IOException) { Assert(File.GetAttributes(locked).HasFlag(FileAttributes.ReadOnly), "删除失败恢复原只读属性"); }
        }
        var before = File.GetLastWriteTimeUtc(Path.Combine(root, "small"));
        var complete = Invoke("DiskUsageScanner", "Scan", root, null!, CancellationToken.None);
        var completeRoot = complete.GetType().GetProperty("Root")!.GetValue(complete)!;
        long completeBytes = (long)completeRoot.GetType().GetProperty("Bytes")!.GetValue(completeRoot)!;
        Assert(completeBytes == 55
            && File.GetLastWriteTimeUtc(Path.Combine(root, "small")) == before, $"全盘扫描无最小体积门槛且不修改文件（统计 {completeBytes} 字节）");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { Invoke("DiskUsageScanner", "Scan", root, null!, cancellation.Token); throw new Exception("未取消"); }
            catch (OperationCanceledException) { Assert(true, "全盘扫描响应取消"); }
        }
        string linkRoot = Directory.CreateDirectory(Path.Combine(fixtures, "links")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(fixtures, "link-target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "keep"), new byte[42]);
        File.WriteAllBytes(Path.Combine(linkRoot, "cache"), new byte[11]);
        string junction = Path.Combine(linkRoot, "junction");
        using (var command = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            command.WaitForExit();
            Assert(command.ExitCode == 0, "建立隔离目录链接测试夹具");
        }
        try
        {
            var linked = Invoke("DiskUsageScanner", "Scan", linkRoot, null!, CancellationToken.None);
            var linkedRoot = linked.GetType().GetProperty("Root")!.GetValue(linked)!;
            Assert((long)linkedRoot.GetType().GetProperty("Bytes")!.GetValue(linkedRoot)! == 11
                && (int)linkedRoot.GetType().GetProperty("LinksSkipped")!.GetValue(linkedRoot)! == 1, "全盘扫描不跟随 junction 且报告跳过");
            Assert(Clean(linkRoot, false).FreedBytes == 11 && File.Exists(Path.Combine(target, "keep")), "清理目录不越过 junction 修改目标文件");
        }
        finally { Directory.Delete(junction); }
        string projects = Directory.CreateDirectory(Path.Combine(fixtures, "many-projects")).FullName;
        for (int i = 0; i < 65; i++)
        {
            string project = Directory.CreateDirectory(Path.Combine(projects, i.ToString())).FullName;
            File.WriteAllText(Path.Combine(project, "package.json"), "{}");
            var artifact = Directory.CreateDirectory(Path.Combine(project, "node_modules")).FullName;
            using var data = File.Create(Path.Combine(artifact, "data")); data.SetLength(1024 * 1024);
        }
        string deep = Directory.CreateDirectory(Path.Combine(projects, "0", "packages", "a", "b", "c", "d", "e", "f", "nested")).FullName;
        File.WriteAllText(Path.Combine(deep, "package.json"), "{}");
        string deepArtifact = Directory.CreateDirectory(Path.Combine(deep, "node_modules")).FullName;
        using (var data = File.Create(Path.Combine(deepArtifact, "data"))) data.SetLength(1024 * 1024);
        var found = CacheScanner.ScanProjects(projects);
        Assert(found.Count == 66 && found.Any(i => i.Path == deepArtifact), "项目扫描覆盖深层嵌套项目、1MB 产物且不截断前60项");
        string profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string hf = Path.Combine(profile, ".cache", "huggingface", "model.bin");
        Assert((bool)Invoke("PathProtection", "IsProtected", hf), "HuggingFace 报告目录保护覆盖其子文件");
        string skills = Path.Combine(profile, ".codex", "skills", "fixture");
        Assert((bool)Invoke("PathProtection", "IsProtected", skills), "Agent neverTouch 在执行层保护技能目录");
        Assert(!(bool)Invoke("PathProtection", "IsWithin", root + "-sibling", root), "路径边界不会误匹配同名前缀目录");
    }

    static void TestProjectMarkers(string fixtures)
    {
        string root=Directory.CreateDirectory(Path.Combine(fixtures,"projects")).FullName;
        void Artifact(string project,string name)
        {
            var directory=Directory.CreateDirectory(Path.Combine(root,project,name)).FullName;
            using var stream=File.Create(Path.Combine(directory,"data"));
            stream.SetLength(50L*1024*1024);
        }
        Artifact("node","node_modules"); Artifact("node",".venv");
        File.WriteAllText(Path.Combine(root,"node","PACKAGE.JSON"),"{}");
        File.WriteAllText(Path.Combine(root,"node","pyproject.toml"),"");
        Artifact("python",".venv");
        File.WriteAllText(Path.Combine(root,"python","requirements.txt"),"");
        Artifact("dotnet","bin");
        File.WriteAllText(Path.Combine(root,"dotnet","solution.SLN"),"");
        Artifact("dotnet-project","obj");
        File.WriteAllText(Path.Combine(root,"dotnet-project","example.csproj"),"");
        var types=typeof(CacheScanner).Assembly.GetType("CacheCleaner.ProjectRegistry")!.GetProperty("All",BindingFlags.Public|BindingFlags.Static)!.GetValue(null)!;
        var found=new List<CacheItem>();
        Invoke("CacheScanner","WalkProjects",root,0,types,found,CancellationToken.None);
        Assert(found.Count==4,"项目匹配保持类型优先级、附加标记和后缀规则，并识别 csproj");
        Assert(found.Single(item=>item.Path==Path.Combine(root,"node","node_modules")).Risk==RiskLevel.Warn,"主标记大小写匹配与活跃项目分级保持一致");
        Assert(found.Single(item=>item.Path==Path.Combine(root,"python",".venv")).Risk==RiskLevel.Warn,"附加标记参与活跃度判断，近期项目保持注意级别");
        Assert(found.Single(item=>item.Path==Path.Combine(root,"dotnet-project","obj")).Risk==RiskLevel.Warn,"csproj 后缀标记参与活跃度判断");
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
        Assert(Field<Control>("btnTrend").ContextMenuStrip!.Items.Cast<ToolStripItem>().Any(i => i.Text == "全盘占用扫描"), "主界面提供全盘占用扫描入口");
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
        var diskType = typeof(CacheScanner).Assembly.GetType("CacheCleaner.DiskUsageDialog")!;
        using (var disk = (Form)Activator.CreateInstance(diskType, Hidden, null, new object[] { Array.Empty<CacheItem>() }, null)!)
        {
            disk.Show(form);
            Application.DoEvents();
            var diskGrid = (DataGridView)diskType.GetField("grid", Hidden)!.GetValue(disk)!;
            Assert(diskGrid.ReadOnly && !diskGrid.AllowUserToDeleteRows, "全盘占用窗口全程只读");
            Assert(disk.Controls.OfType<FlowLayoutPanel>().Single().Controls.Cast<Control>().All(c => c.Bounds.Bottom <= c.Parent!.ClientSize.Height), "全盘扫描工具栏完整显示");
            string uiFixture = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ui-fixture-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                string child = Directory.CreateDirectory(Path.Combine(uiFixture, "sample-cache")).FullName;
                File.WriteAllBytes(Path.Combine(child, "small-file"), new byte[120]);
                File.WriteAllBytes(Path.Combine(uiFixture, "direct-file"), new byte[30]);
                ((TextBox)diskType.GetField("root", Hidden)!.GetValue(disk)!).Text = uiFixture;
                var scanTask = (Task)diskType.GetMethod("Scan", Hidden)!.Invoke(disk, null)!;
                var watch = Stopwatch.StartNew();
                while (!scanTask.IsCompleted && watch.Elapsed.TotalSeconds < 10) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(scanTask.IsCompletedSuccessfully && diskGrid.Rows.Count == 2, "窗口扫描完成显示目录与直接文件合计");
                typeof(DataGridView).GetMethod("OnCellDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(diskGrid, new object[] { new DataGridViewCellEventArgs(0, 0) });
                Assert(diskGrid.Rows.Count == 1, "双击目录可查看下一层");
                ((Button)diskType.GetField("up", Hidden)!.GetValue(disk)!).PerformClick();
                Assert(diskGrid.Rows.Count == 2, "上一级恢复目录列表");
                var filesView = (CheckBox)diskType.GetField("filesView", Hidden)!.GetValue(disk)!;
                filesView.Checked = true;
                Assert(diskGrid.Rows.Count == 2 && diskGrid.Rows[0].Cells["Bytes"].Value is long bytes && bytes == 120, "大文件排行按实际大小排序");
                filesView.Checked = false;
            }
            finally { Directory.Delete(uiFixture, true); }
            using var bitmap = new Bitmap(disk.Width, disk.Height);
            disk.DrawToBitmap(bitmap, new Rectangle(Point.Empty, disk.Size));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "disk-ui-check.png"));
        }
        Console.WriteLine("UI_REGRESSION_OK");
        // 不触发主窗体关闭时的设置保存。
        Environment.Exit(0); return 0;
    }
}
