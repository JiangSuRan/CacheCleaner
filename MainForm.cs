using System.Diagnostics;

namespace CacheCleaner;

public class MainForm : Form
{
    private DataGridView dgv;
    private Button btnScan;
    private Button btnClean;
    private Button btnSelectAll;
    private Button btnSelectNone;
    private ProgressBar progressBar;
    private Label lblStatus;
    private Label lblTotal;
    private long totalFreed;

    public MainForm()
    {
        SetupForm();
        SetupControls();
    }

    private void SetupForm()
    {
        Text = "C盘缓存清理工具 v2.0";
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
            BackColor = Color.FromArgb(0, 120, 215)
        };
        var titleLabel = new Label
        {
            Text = "  C盘缓存清理工具",
            ForeColor = Color.White,
            Font = new Font("微软雅黑", 16, FontStyle.Bold),
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

        btnScan = CreateButton("扫描缓存", Color.FromArgb(0, 120, 215), Color.White);
        btnScan.Location = new Point(15, 10);
        btnScan.Size = new Size(120, 35);
        btnScan.Click += BtnScan_Click;

        btnClean = CreateButton("清理选中", Color.FromArgb(220, 53, 69), Color.White);
        btnClean.Location = new Point(150, 10);
        btnClean.Size = new Size(120, 35);
        btnClean.Enabled = false;
        btnClean.Click += BtnClean_Click;

        btnSelectAll = CreateButton("全选", Color.FromArgb(108, 117, 125), Color.White);
        btnSelectAll.Location = new Point(290, 10);
        btnSelectAll.Size = new Size(80, 35);
        btnSelectAll.Enabled = false;
        btnSelectAll.Click += (_, _) => SetAllChecked(true);

        btnSelectNone = CreateButton("取消全选", Color.FromArgb(108, 117, 125), Color.White);
        btnSelectNone.Location = new Point(385, 10);
        btnSelectNone.Size = new Size(90, 35);
        btnSelectNone.Enabled = false;
        btnSelectNone.Click += (_, _) => SetAllChecked(false);

        buttonPanel.Controls.AddRange([btnScan, btnClean, btnSelectAll, btnSelectNone]);
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
            BackColor = Color.FromArgb(240, 240, 240)
        };
        lblStatus = new Label
        {
            Text = "  就绪。点击「扫描缓存」开始。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("微软雅黑", 9F)
        };
        lblTotal = new Label
        {
            Text = "",
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("微软雅黑", 9F, FontStyle.Bold),
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
            Font = new Font("微软雅黑", 9.5F),
            Padding = new Padding(0)
        };

        // 列头样式
        dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
        dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(60, 60, 60);
        dgv.ColumnHeadersDefaultCellStyle.Font = new Font("微软雅黑", 9.5F, FontStyle.Bold);
        dgv.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(240, 240, 240);
        dgv.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(60, 60, 60);
        dgv.ColumnHeadersHeight = 36;
        dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        // 交替行颜色
        dgv.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 248, 248);

        // 行高
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
        // 把 dgv 移到最底层，让 Dock.Fill 生效
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
            Font = new Font("微软雅黑", 9.5F),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
    }

    /// <summary>
    /// 勾选框点击处理
    /// </summary>
    private void Dgv_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && dgv.Columns[e.ColumnIndex].Name == "Checked")
        {
            dgv.EndEdit();
            UpdateTotalSize();
        }
    }

    /// <summary>
    /// 格式化单元格显示
    /// </summary>
    private void Dgv_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;

        // 大小列格式化
        if (dgv.Columns[e.ColumnIndex].Name == "Size" && e.Value is long size)
        {
            e.Value = CacheScanner.FormatSize(size);
            e.FormattingApplied = true;
        }

        // 风险列颜色
        if (dgv.Columns[e.ColumnIndex].Name == "Risk" && e.Value is string risk)
        {
            e.Value = risk switch
            {
                "safe" => "安全",
                "warn" => "注意",
                "danger" => "危险",
                _ => risk
            };
            e.FormattingApplied = true;
            e.CellStyle!.ForeColor = risk switch
            {
                "safe" => Color.FromArgb(40, 167, 69),
                "warn" => Color.FromArgb(255, 193, 7),
                "danger" => Color.FromArgb(220, 53, 69),
                _ => Color.Black
            };
            e.CellStyle.Font = new Font("微软雅黑", 9.5F, FontStyle.Bold);
        }

        // 项目名称列：不存在的项显示灰色
        if (dgv.Columns[e.ColumnIndex].Name == "Name")
        {
            var pathVal = dgv.Rows[e.RowIndex].Cells["Path"].Value?.ToString();
            if (string.IsNullOrEmpty(pathVal))
            {
                e.CellStyle!.ForeColor = Color.Gray;
            }
        }
    }

    /// <summary>
    /// 扫描按钮点击
    /// </summary>
    private void BtnScan_Click(object? sender, EventArgs e)
    {
        btnScan.Enabled = false;
        btnClean.Enabled = false;
        btnSelectAll.Enabled = false;
        btnSelectNone.Enabled = false;
        progressBar.Visible = true;
        lblStatus.Text = "  正在扫描缓存，请稍候...";

        dgv.Rows.Clear();

        var worker = new Thread(() =>
        {
            var progress = new Progress<(int current, int total, string name)>(p =>
            {
                progressBar.Value = 0;
                progressBar.Maximum = p.total;
                progressBar.Value = Math.Min(p.current, p.total);
                lblStatus.Text = $"  正在扫描: {p.name} ({p.current}/{p.total})";
            });

            var items = CacheScanner.ScanAll((IProgress<(int, int, string)>)progress);

            this.BeginInvoke(() =>
            {
                foreach (var item in items)
                {
                    if (!item.Exists) continue; // 只显示存在的缓存
                    int rowIdx = dgv.Rows.Add(
                        item.Checked,
                        item.Name,
                        item.SizeBytes,
                        item.Desc,
                        item.Risk,
                        item.Path
                    );

                    // 安全项默认勾选，警告/危险项不勾选
                    if (item.Risk == "safe")
                    {
                        dgv.Rows[rowIdx].Cells["Checked"].Value = true;
                    }
                }

                progressBar.Visible = false;
                btnScan.Enabled = true;
                btnClean.Enabled = true;
                btnSelectAll.Enabled = true;
                btnSelectNone.Enabled = true;
                lblStatus.Text = $"  扫描完成，共发现 {dgv.Rows.Count} 个缓存项目。";
                UpdateTotalSize();
            });
        })
        {
            IsBackground = true
        };
        worker.Start();
    }

    /// <summary>
    /// 清理按钮点击
    /// </summary>
    private void BtnClean_Click(object? sender, EventArgs e)
    {
        // 收集选中项
        var selectedItems = new List<(int rowIndex, string name, string path, string risk)>();
        for (int i = 0; i < dgv.Rows.Count; i++)
        {
            if (dgv.Rows[i].Cells["Checked"].Value is true)
            {
                string risk = dgv.Rows[i].Cells["Risk"].Value?.ToString() ?? "safe";
                selectedItems.Add((
                    i,
                    dgv.Rows[i].Cells["Name"].Value?.ToString() ?? "",
                    dgv.Rows[i].Cells["Path"].Value?.ToString() ?? "",
                    risk
                ));
            }
        }

        if (selectedItems.Count == 0)
        {
            MessageBox.Show("请先勾选要清理的项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 检查是否有危险项
        var dangerItems = selectedItems.Where(x => x.risk == "danger").ToList();
        var warnItems = selectedItems.Where(x => x.risk == "warn").ToList();

        string warning = "";
        if (dangerItems.Count > 0)
        {
            warning += $"\n\n危险项目:\n{string.Join("\n", dangerItems.Select(x => $"  - {x.name}"))}\n这些操作可能导致数据丢失！";
        }
        if (warnItems.Count > 0)
        {
            warning += $"\n\n注意项目:\n{string.Join("\n", warnItems.Select(x => $"  - {x.name}"))}\n请确保相关程序已关闭。";
        }

        var result = MessageBox.Show(
            $"确认清理 {selectedItems.Count} 个项目？{warning}",
            "确认清理",
            MessageBoxButtons.OKCancel,
            dangerItems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question
        );

        if (result != DialogResult.OK) return;

        // 开始清理
        btnClean.Enabled = false;
        btnScan.Enabled = false;
        btnSelectAll.Enabled = false;
        btnSelectNone.Enabled = false;
        progressBar.Visible = true;
        totalFreed = 0;

        var worker = new Thread(() =>
        {
            long totalFreedLocal = 0;

            for (int i = 0; i < selectedItems.Count; i++)
            {
                var (rowIdx, name, path, _) = selectedItems[i];

                this.BeginInvoke(() =>
                {
                    lblStatus.Text = $"  正在清理: {name} ({i + 1}/{selectedItems.Count})";
                    progressBar.Value = 0;
                    progressBar.Maximum = selectedItems.Count;
                    progressBar.Value = Math.Min(i + 1, selectedItems.Count);

                    // 高亮当前行
                    dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(255, 243, 205);
                });

                var item = new CacheItem { Name = name, Path = path, Exists = true };
                var cleanProgress = new Progress<string>(msg =>
                {
                    this.BeginInvoke(() => lblStatus.Text = $"  {msg}");
                });
                long freed = CacheScanner.CleanItem(item, (IProgress<string>)cleanProgress);
                totalFreedLocal += freed;

                // 更新行显示
                this.BeginInvoke(() =>
                {
                    if (freed > 0)
                    {
                        dgv.Rows[rowIdx].Cells["Size"].Value = item.SizeBytes;
                        dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(212, 237, 218);
                    }
                    else
                    {
                        dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(248, 248, 248);
                    }
                });
            }

            // 完成
            this.BeginInvoke(() =>
            {
                totalFreed = totalFreedLocal;
                progressBar.Visible = false;
                btnScan.Enabled = true;
                btnClean.Enabled = true;
                btnSelectAll.Enabled = true;
                btnSelectNone.Enabled = true;

                string freedStr = CacheScanner.FormatSize(totalFreed);
                lblStatus.Text = $"  清理完成！共释放 {freedStr}。";
                lblTotal.Text = $"释放: {freedStr}  ";
                lblTotal.ForeColor = Color.FromArgb(40, 167, 69);

                MessageBox.Show(
                    $"清理完成！\n\n共释放: {freedStr}",
                    "清理完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                // 自动重新扫描
                BtnScan_Click(null, EventArgs.Empty);
            });
        })
        {
            IsBackground = true
        };
        worker.Start();
    }

    /// <summary>
    /// 更新选中项总大小
    /// </summary>
    private void UpdateTotalSize()
    {
        long total = 0;
        for (int i = 0; i < dgv.Rows.Count; i++)
        {
            if (dgv.Rows[i].Cells["Checked"].Value is true)
            {
                total += dgv.Rows[i].Cells["Size"].Value as long? ?? 0;
            }
        }
        lblTotal.Text = $"选中: {CacheScanner.FormatSize(total)}  ";
        lblTotal.ForeColor = total > 0 ? Color.FromArgb(0, 120, 215) : Color.Gray;
    }

    /// <summary>
    /// 全选/取消全选
    /// </summary>
    private void SetAllChecked(bool check)
    {
        for (int i = 0; i < dgv.Rows.Count; i++)
        {
            dgv.Rows[i].Cells["Checked"].Value = check;
        }
        dgv.EndEdit();
        UpdateTotalSize();
    }
}
