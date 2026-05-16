using System.Diagnostics;

namespace CacheCleaner;

public class MainForm : Form
{
    // 缓存的字体对象（避免 CellFormatting 中反复创建导致 GDI 泄漏）
    private static readonly Font FontTitle = new("微软雅黑", 16, FontStyle.Bold);
    private static readonly Font FontNormal = new("微软雅黑", 9.5F);
    private static readonly Font FontNormalBold = new("微软雅黑", 9.5F, FontStyle.Bold);
    private static readonly Font FontSmall = new("微软雅黑", 9F);
    private static readonly Font FontSmallBold = new("微软雅黑", 9F, FontStyle.Bold);

    // 常用颜色常量（替代散落各处的魔术数字）
    private static readonly Color AccentBlue = Color.FromArgb(0, 120, 215);
    private static readonly Color DangerRed = Color.FromArgb(220, 53, 69);
    private static readonly Color SuccessGreen = Color.FromArgb(40, 167, 69);
    private static readonly Color WarnYellow = Color.FromArgb(255, 193, 7);
    private static readonly Color GrayLight = Color.FromArgb(240, 240, 240);
    private static readonly Color GrayLighter = Color.FromArgb(248, 248, 248);
    private static readonly Color GrayText = Color.FromArgb(60, 60, 60);
    private static readonly Color GrayButton = Color.FromArgb(108, 117, 125);

    private DataGridView dgv = null!;
    private Button btnScan = null!;
    private Button btnClean = null!;
    private Button btnSelectAll = null!;
    private Button btnSelectNone = null!;
    private Button btnCancel = null!;
    private ProgressBar progressBar = null!;
    private Label lblStatus = null!;
    private Label lblTotal = null!;
    private CancellationTokenSource? _cts;

    public MainForm()
    {
        SetupForm();
        SetupControls();
    }

    private void SetupForm()
    {
        Text = "C盘缓存清理工具 v3.0";
        Size = new Size(820, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimumSize = new Size(820, 600);
        BackColor = Color.FromArgb(245, 245, 245);
    }

    private void SetupControls()
    {
        // 标题栏
        var titlePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = AccentBlue
        };
        var titleLabel = new Label
        {
            Text = "  C盘缓存清理工具",
            ForeColor = Color.White,
            Font = FontTitle,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        titlePanel.Controls.Add(titleLabel);
        Controls.Add(titlePanel);

        // 操作按钮区域
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(15, 10, 15, 5)
        };

        btnScan = CreateButton("扫描缓存", AccentBlue, Color.White);
        btnScan.Location = new Point(15, 10);
        btnScan.Size = new Size(120, 35);
        btnScan.Click += BtnScan_Click;

        btnClean = CreateButton("清理选中", DangerRed, Color.White);
        btnClean.Location = new Point(150, 10);
        btnClean.Size = new Size(120, 35);
        btnClean.Enabled = false;
        btnClean.Click += BtnClean_Click;

        btnSelectAll = CreateButton("全选", GrayButton, Color.White);
        btnSelectAll.Location = new Point(290, 10);
        btnSelectAll.Size = new Size(80, 35);
        btnSelectAll.Enabled = false;
        btnSelectAll.Click += (_, _) => SetAllChecked(true);

        btnSelectNone = CreateButton("取消全选", GrayButton, Color.White);
        btnSelectNone.Location = new Point(385, 10);
        btnSelectNone.Size = new Size(90, 35);
        btnSelectNone.Enabled = false;
        btnSelectNone.Click += (_, _) => SetAllChecked(false);

        btnCancel = CreateButton("取消扫描", Color.FromArgb(183, 28, 28), Color.White);
        btnCancel.Location = new Point(490, 10);
        btnCancel.Size = new Size(90, 35);
        btnCancel.Visible = false;
        btnCancel.Click += (_, _) => _cts?.Cancel();

        buttonPanel.Controls.AddRange([btnScan, btnClean, btnSelectAll, btnSelectNone, btnCancel]);
        Controls.Add(buttonPanel);

        // 进度条
        progressBar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 8,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };
        Controls.Add(progressBar);

        // 状态栏
        var statusPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 35,
            BackColor = GrayLight
        };
        lblStatus = new Label
        {
            Text = "  就绪。点击「扫描缓存」开始。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = FontSmall
        };
        lblTotal = new Label
        {
            Text = "",
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
            Font = FontSmallBold,
            Size = new Size(200, 35)
        };
        statusPanel.Controls.AddRange([lblStatus, lblTotal]);
        Controls.Add(statusPanel);

        // 数据表格
        dgv = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ReadOnly = false,
            MultiSelect = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            GridColor = Color.FromArgb(230, 230, 230),
            Font = FontNormal
        };

        dgv.ColumnHeadersDefaultCellStyle.BackColor = GrayLight;
        dgv.ColumnHeadersDefaultCellStyle.ForeColor = GrayText;
        dgv.ColumnHeadersDefaultCellStyle.Font = FontNormalBold;
        dgv.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = GrayLight;
        dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = GrayText;
        dgv.ColumnHeadersHeight = 36;
        dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        dgv.AlternatingRowsDefaultCellStyle.BackColor = GrayLighter;
        dgv.RowTemplate.Height = 32;

        // 列定义
        var colCheck = new DataGridViewCheckBoxColumn
        {
            Name = "Checked",
            HeaderText = "",
            Width = 45,
            TrueValue = true,
            FalseValue = false,
            IndeterminateValue = false
        };

        var colName = new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "缓存项目",
            Width = 160,
            ReadOnly = true
        };

        var colSize = new DataGridViewTextBoxColumn
        {
            Name = "Size",
            HeaderText = "大小",
            Width = 100,
            ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        };

        var colDesc = new DataGridViewTextBoxColumn
        {
            Name = "Desc",
            HeaderText = "说明",
            Width = 300,
            ReadOnly = true
        };

        var colRisk = new DataGridViewTextBoxColumn
        {
            Name = "Risk",
            HeaderText = "风险",
            Width = 60,
            ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
        };

        var colPath = new DataGridViewTextBoxColumn
        {
            Name = "Path",
            HeaderText = "路径",
            Visible = false,
            ReadOnly = true
        };

        dgv.Columns.AddRange([colCheck, colName, colSize, colDesc, colRisk, colPath]);
        dgv.CellContentClick += Dgv_CellContentClick;
        dgv.CellFormatting += Dgv_CellFormatting;

        Controls.Add(dgv);
        Controls.SetChildIndex(dgv, 0);
    }

    private Button CreateButton(string text, Color backColor, Color foreColor)
    {
        return new Button
        {
            Text = text,
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Font = FontNormal,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
    }

    /// <summary>
    /// 统一设置操作按钮的启用状态（替代 4 处重复的按钮切换代码）
    /// </summary>
    private void SetControlsEnabled(bool enabled)
    {
        btnScan.Enabled = enabled;
        btnClean.Enabled = enabled;
        btnSelectAll.Enabled = enabled;
        btnSelectNone.Enabled = enabled;
    }

    private void Dgv_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && dgv.Columns[e.ColumnIndex].Name == "Checked")
        {
            dgv.EndEdit();
            UpdateTotalSize();
        }
    }

    private void Dgv_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;

        if (dgv.Columns[e.ColumnIndex].Name == "Size" && e.Value is long size)
        {
            e.Value = CacheScanner.FormatSize(size);
            e.FormattingApplied = true;
        }

        if (dgv.Columns[e.ColumnIndex].Name == "Risk" && e.Value is RiskLevel risk)
        {
            e.Value = risk switch
            {
                RiskLevel.Safe => "安全",
                RiskLevel.Warn => "注意",
                RiskLevel.Danger => "危险",
                _ => risk.ToString()
            };
            e.FormattingApplied = true;
            e.CellStyle!.ForeColor = risk switch
            {
                RiskLevel.Safe => SuccessGreen,
                RiskLevel.Warn => WarnYellow,
                RiskLevel.Danger => DangerRed,
                _ => Color.Black
            };
            e.CellStyle.Font = FontNormalBold; // 使用缓存的字体，不再每次 new
        }

        if (dgv.Columns[e.ColumnIndex].Name == "Name")
        {
            var pathVal = dgv.Rows[e.RowIndex].Cells["Path"].Value?.ToString();
            if (string.IsNullOrEmpty(pathVal))
                e.CellStyle!.ForeColor = Color.Gray;
        }
    }

    /// <summary>
    /// 扫描按钮点击（两阶段：已知缓存 + 自动发现）
    /// </summary>
    private async void BtnScan_Click(object? sender, EventArgs e)
    {
        SetControlsEnabled(false);
        btnCancel.Visible = true;
        progressBar.Visible = true;
        lblStatus.Text = "  正在扫描已知缓存...";
        dgv.Rows.Clear();

        _cts = new CancellationTokenSource();
        var knownProgress = new Progress<(int current, int total, string name)>(p =>
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Maximum = p.total;
            progressBar.Value = Math.Min(p.current, p.total);
            lblStatus.Text = $"  扫描已知缓存: {p.name} ({p.current}/{p.total})";
        });

        try
        {
            // Phase 1: 已知缓存（快速，1-2秒）
            var knownItems = await Task.Run(() => CacheScanner.ScanAll(knownProgress, _cts.Token));

            foreach (var item in knownItems)
            {
                if (!item.Exists) continue;
                AddCacheRow(item, isKnown: true);
            }

            UpdateTotalSize();
            lblStatus.Text = $"  已知缓存扫描完成 ({dgv.Rows.Count} 项)，正在自动发现更多缓存...";

            // Phase 2: 自动发现（较慢，10-30秒）
            var autoProgress = new Progress<(int dirsScanned, string currentPath)>(p =>
            {
                progressBar.Style = ProgressBarStyle.Marquee;
                lblStatus.Text = $"  自动发现: 已扫描 {p.dirsScanned} 个目录... ({p.currentPath})";
            });

            var autoItems = await Task.Run(() =>
                CacheScanner.ScanAutoDiscovered(knownItems, autoProgress, _cts.Token));

            int autoCount = 0;
            foreach (var item in autoItems)
            {
                if (!item.Exists) continue;
                AddCacheRow(item, isKnown: false);
                autoCount++;
            }

            lblStatus.Text = $"  扫描完成: {dgv.Rows.Count} 个缓存项目 (其中 {autoCount} 个自动发现)。";
            UpdateTotalSize();
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "  扫描已取消。";
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"  扫描出错: {ex.Message}";
            Debug.WriteLine($"扫描异常: {ex}");
        }
        finally
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Visible = false;
            btnCancel.Visible = false;
            SetControlsEnabled(true);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 添加缓存项到表格，自动发现项有视觉区分
    /// </summary>
    private void AddCacheRow(CacheItem item, bool isKnown)
    {
        string displayName = isKnown ? item.Name : $"[自动发现] {item.Name}";
        int rowIdx = dgv.Rows.Add(
            item.Checked,
            displayName,
            item.SizeBytes,
            item.Desc,
            item.Risk,
            item.Path
        );

        if (!isKnown)
            dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(235, 245, 255);

        if (item.Risk == RiskLevel.Safe)
            dgv.Rows[rowIdx].Cells["Checked"].Value = true;
    }

    /// <summary>
    /// 清理按钮点击（逐项 await，自然回到 UI 线程更新，无需 BeginInvoke）
    /// </summary>
    private async void BtnClean_Click(object? sender, EventArgs e)
    {
        // 收集选中项
        var selectedItems = new List<(int rowIndex, string name, string path, RiskLevel risk)>();
        for (int i = 0; i < dgv.Rows.Count; i++)
        {
            if (dgv.Rows[i].Cells["Checked"].Value is true)
            {
                selectedItems.Add((
                    i,
                    dgv.Rows[i].Cells["Name"].Value?.ToString() ?? "",
                    dgv.Rows[i].Cells["Path"].Value?.ToString() ?? "",
                    dgv.Rows[i].Cells["Risk"].Value is RiskLevel r ? r : RiskLevel.Safe
                ));
            }
        }

        if (selectedItems.Count == 0)
        {
            MessageBox.Show("请先勾选要清理的项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 构建警告信息
        var dangerItems = selectedItems.Where(x => x.risk == RiskLevel.Danger).ToList();
        var warnItems = selectedItems.Where(x => x.risk == RiskLevel.Warn).ToList();

        string warning = "";
        if (dangerItems.Count > 0)
            warning += $"\n\n危险项目:\n{string.Join("\n", dangerItems.Select(x => $"  - {x.name}"))}\n这些操作可能导致数据丢失！";
        if (warnItems.Count > 0)
            warning += $"\n\n注意项目:\n{string.Join("\n", warnItems.Select(x => $"  - {x.name}"))}\n请确保相关程序已关闭。";

        var result = MessageBox.Show(
            $"确认清理 {selectedItems.Count} 个项目？{warning}",
            "确认清理",
            MessageBoxButtons.OKCancel,
            dangerItems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question
        );

        if (result != DialogResult.OK) return;

        SetControlsEnabled(false);
        progressBar.Visible = true;

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => lblStatus.Text = $"  {msg}");
        long totalFreed = 0;

        try
        {
            for (int i = 0; i < selectedItems.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var (rowIdx, name, path, _) = selectedItems[i];

                // 更新进度（已在 UI 线程，直接操作控件）
                lblStatus.Text = $"  正在清理: {name} ({i + 1}/{selectedItems.Count})";
                progressBar.Maximum = selectedItems.Count;
                progressBar.Value = i + 1;
                dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(255, 243, 205);

                // 在后台线程执行清理
                var item = new CacheItem { Name = name, Path = path, Exists = true };
                long itemFreed = await Task.Run(() => CacheScanner.CleanItem(item, progress, _cts.Token));
                totalFreed += itemFreed;

                // await 后自动回到 UI 线程，直接更新控件
                if (itemFreed > 0)
                {
                    dgv.Rows[rowIdx].Cells["Size"].Value = item.SizeBytes;
                    dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(212, 237, 218);
                }
                else
                {
                    dgv.Rows[rowIdx].DefaultCellStyle.BackColor = GrayLighter;
                }
            }

            string freedStr = CacheScanner.FormatSize(totalFreed);
            lblStatus.Text = $"  清理完成！共释放 {freedStr}。";
            lblTotal.Text = $"释放: {freedStr}  ";
            lblTotal.ForeColor = SuccessGreen;

            MessageBox.Show(
                $"清理完成！\n\n共释放: {freedStr}",
                "清理完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            // 自动重新扫描
            BtnScan_Click(null, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "  清理已取消。";
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"  清理出错: {ex.Message}";
            Debug.WriteLine($"清理异常: {ex}");
        }
        finally
        {
            progressBar.Visible = false;
            SetControlsEnabled(true);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void UpdateTotalSize()
    {
        long total = 0;
        for (int i = 0; i < dgv.Rows.Count; i++)
        {
            if (dgv.Rows[i].Cells["Checked"].Value is true)
                total += dgv.Rows[i].Cells["Size"].Value as long? ?? 0;
        }
        lblTotal.Text = $"选中: {CacheScanner.FormatSize(total)}  ";
        lblTotal.ForeColor = total > 0 ? AccentBlue : Color.Gray;
    }

    private void SetAllChecked(bool check)
    {
        dgv.SuspendLayout();
        try
        {
            for (int i = 0; i < dgv.Rows.Count; i++)
                dgv.Rows[i].Cells["Checked"].Value = check;
        }
        finally
        {
            dgv.ResumeLayout();
        }
        dgv.EndEdit();
        UpdateTotalSize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
