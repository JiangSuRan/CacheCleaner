namespace CacheCleaner;

internal sealed class DiskUsageDialog : Form
{
    private readonly TextBox root = new() { Width = 300, Text = CacheScanner.SysRoot };
    private readonly Button choose = new() { Text = "选择目录", AutoSize = true };
    private readonly Button start = new() { Text = "开始扫描", AutoSize = true };
    private readonly Button cancel = new() { Text = "取消", AutoSize = true, Enabled = false };
    private readonly Button up = new() { Text = "上一级", AutoSize = true, Enabled = false };
    private readonly CheckBox filesView = new() { Text = "大文件前 100 项", AutoSize = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 48, AutoEllipsis = true };
    private readonly Label location = new() { Dock = DockStyle.Top, Height = 30, AutoEllipsis = true };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White
    };
    private readonly IReadOnlyList<CacheItem> known;
    private readonly Stack<DiskUsageEntry> parents = new();
    private DiskUsageResult? result;
    private DiskUsageEntry? current;
    private CancellationTokenSource? cts;
    private bool closeAfterCancel;

    internal DiskUsageDialog(IReadOnlyList<CacheItem> knownItems)
    {
        known = knownItems;
        Text = "全盘占用扫描";
        Size = new Size(1100, 720);
        MinimumSize = new Size(850, 500);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("微软雅黑", 9F);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(8), WrapContents = true };
        toolbar.Controls.AddRange([root, choose, start, cancel, up, filesView]);
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 48,
            Text = "统计所有可访问目录，不限制大小或层数；双击目录查看下一层。用途待确认的占用需进一步核实。\n显示文件逻辑大小，硬链接可能重复计量；目录链接和云占位文件跳过，扫描全程只读。"
        };
        grid.Columns.Add("Path", "目录 / 文件");
        grid.Columns.Add("Bytes", "逻辑大小（字节）");
        grid.Columns["Bytes"]!.DefaultCellStyle.Format = "N0";
        grid.Columns.Add("Status", "统计状态");
        grid.Columns.Add("Coverage", "现有规则覆盖");
        grid.Columns["Path"]!.FillWeight = 240;
        Controls.Add(grid);
        Controls.Add(location);
        Controls.Add(hint);
        Controls.Add(toolbar);
        Controls.Add(status);
        choose.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { InitialDirectory = root.Text, Description = "选择要统计的磁盘或目录" };
            if (picker.ShowDialog(this) == DialogResult.OK) root.Text = picker.SelectedPath;
        };
        start.Click += async (_, _) => await Scan();
        cancel.Click += (_, _) => cts?.Cancel();
        up.Click += (_, _) => { if (parents.TryPop(out var parent)) { current = parent; ShowEntries(); } };
        filesView.CheckedChanged += (_, _) => ShowEntries();
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || filesView.Checked || grid.Rows[e.RowIndex].Tag is not DiskUsageEntry entry || entry.IsFile) return;
            if (current != null) parents.Push(current);
            current = entry;
            ShowEntries();
        };
        FormClosing += (_, e) =>
        {
            if (cts == null) return;
            closeAfterCancel = true;
            cts.Cancel();
            e.Cancel = true;
        };
    }

    private async Task Scan()
    {
        if (cts != null) return;
        cts = new CancellationTokenSource();
        start.Enabled = choose.Enabled = root.Enabled = filesView.Enabled = up.Enabled = grid.Enabled = false;
        cancel.Enabled = true;
        status.Text = "正在扫描，首次全盘统计可能需要数分钟……";
        try
        {
            string selected = root.Text;
            var progress = new Progress<string>(message => { if (cts != null) status.Text = message; });
            result = await Task.Run(() => DiskUsageScanner.Scan(selected, progress, cts.Token));
            current = result.Root;
            parents.Clear();
            ShowEntries();
            status.Text = $"完成：{result.Directories:N0} 个目录 / {result.Files:N0} 个文件；逻辑大小 {CacheScanner.FormatSize(result.Root.Bytes)}；无法读取 {result.Root.Unreadable:N0} 项，跳过链接/占位项 {result.Root.LinksSkipped:N0} 项。";
        }
        catch (OperationCanceledException) { status.Text = "已取消；如有上次扫描结果，继续保留显示。"; }
        catch (Exception ex) { status.Text = $"扫描失败：{ex.Message}"; }
        finally
        {
            cts.Dispose();
            cts = null;
            start.Enabled = choose.Enabled = root.Enabled = filesView.Enabled = grid.Enabled = true;
            cancel.Enabled = false;
            up.Enabled = parents.Count > 0 && !filesView.Checked;
            if (closeAfterCancel) Close();
        }
    }

    private void ShowEntries()
    {
        grid.Rows.Clear();
        if (result == null || current == null) return;
        location.Text = filesView.Checked ? "大文件排行（当前扫描范围）" : $"{current.Path} · 合计 {CacheScanner.FormatSize(current.Bytes)}";
        IEnumerable<DiskUsageEntry> entries = filesView.Checked ? result.LargestFiles : current.Children.OrderByDescending(i => i.Bytes);
        foreach (var entry in entries)
        {
            var rule = known.FirstOrDefault(k => k.Exists && System.IO.Path.IsPathFullyQualified(k.Path) && PathProtection.IsWithin(entry.Path, k.Path));
            string coverage = known.Count == 0 ? "请先运行缓存扫描以比对规则" : rule != null ? $"已识别：{rule.Name}"
                : known.Any(k => k.Exists && System.IO.Path.IsPathFullyQualified(k.Path) && PathProtection.IsWithin(k.Path, entry.Path)) ? "部分子目录有规则" : "用途待确认";
            int row = grid.Rows.Add(entry.Path, entry.Bytes,
                $"无法读取 {entry.Unreadable} / 跳过链接 {entry.LinksSkipped}", coverage);
            grid.Rows[row].Tag = entry;
        }
        if (!filesView.Checked && current.DirectFileBytes > 0)
            grid.Rows.Add("本目录直接存放的文件（合计；大文件可切换排行查看）", current.DirectFileBytes, "已统计", "需按文件核实用途");
        up.Enabled = parents.Count > 0 && !filesView.Checked;
    }
}
