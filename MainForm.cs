using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CacheCleaner;

/// <summary>
/// 主界面（v5.1 一体化改版）：无边框圆角窗口 + 四层视觉体系。
/// L0 浅色底 / L1 右侧等比插画与渐变遮罩 / L2 半透明白色内容面板 /
/// L3 控件。标题栏自绘（图标 + C盘缓存清理 + 最小化/关闭，可拖动），按钮按
/// Primary/Secondary/Ghost 层级动态分配主按钮。固定尺寸窗口 → 绝对布局。
/// 业务逻辑与皮肤完全解耦。
/// </summary>
public class MainForm : Form
{
    // ---- 状态机 ----
    private UiState _state = UiState.Idle;

    // ---- 面板 ----
    private SoftPanel _titleBar = null!;
    private SoftPanel _headerTitle = null!;
    private SoftPanel _headerToolbar = null!;
    private SoftPanel _statusArea = null!;
    private SoftPanel contentPanel = null!;

    // ---- 标题栏控件 ----
    private Label lblBarTitle = null!;
    private CaptionButton btnMin = null!;
    private CaptionButton btnClose = null!;

    // ---- 头部控件 ----
    private Label lblTitle = null!;
    private Label lblSubTitle = null!;

    // ---- 操作按钮 ----
    private GradientButton btnScan = null!;
    private GradientButton btnClean = null!;
    private GradientButton btnSelectAll = null!;
    private GradientButton btnSelectNone = null!;
    private GradientButton btnCancel = null!;
    private GradientButton btnTrend = null!;

    // ---- 内容区控件 ----
    private AnimeDataGridView dgv = null!;
    private Label lblSection = null!;
    private Label lblFound = null!;
    private Label lblEmpty = null!;

    // ---- 状态区控件 ----
    private Label lblStateDot = null!;
    private Label lblCount = null!;
    private Label lblStateTitle = null!;
    private Label lblStateDetail = null!;
    private Label lblReclaim = null!;
    private Label lblSelected = null!;
    private CheckBox chkPreview = null!;

    // ---- 进度条 ----
    private ProgressLite progress = null!;

    private CancellationTokenSource? _cts;
    private int _hoverRow = -1;
    private bool _layoutReady;

    public MainForm()
    {
        SetupForm();
        SetupControls();
        _layoutReady = true;
        LayoutUI();
        SetControlsEnabled(true);

        // 设置持久化：恢复预览开关；关闭窗口时保存当前勾选
        AppSettings.Load();
        chkPreview.Checked = AppSettings.PreviewMode;
        FormClosing += (_, _) =>
        {
            var names = new List<string>();
            for (int i = 0; i < dgv.Rows.Count; i++)
                if (dgv.Rows[i].Cells["Checked"].Value is true)
                    names.Add(dgv.Rows[i].Cells["Name"].Value?.ToString() ?? "");
            AppSettings.Save(chkPreview.Checked, names);
        };
    }

    // ==================== 无边框窗口：拖动 / 圆角 / 边框 ====================

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    /// <summary>标题栏按下拖动窗口（标准 HTCAPTION 手法）</summary>
    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutUI();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // DWM 系统圆角（不对窗口做 Region 裁剪 → 无黑角）；旧系统静默忽略
        try
        {
            int round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { /* 旧系统 */ }
    }

    // ==================== 布局常量 ====================

    private const int BarH = 40;        // 自绘标题栏
    private const int TitleH = 58;      // 头部标题层
    private const int ToolbarH = 46;    // 操作栏
    private const int StatusH = 46;     // 状态区
    private const int ProgressH = 5;    // 进度条

    // ==================== 控件构建 ====================

    private void SetupForm()
    {
        Text = "Cache Cleaner";
        FormBorderStyle = FormBorderStyle.None;   // 自绘标题栏（窗体不可缩放，无拖拽缩放风险）
        Size = new Size(980, 700);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        BackColor = Theme.PageBg;
        DoubleBuffered = true;
    }

    private void SetupControls()
    {
        // ---- 自绘标题栏（40px，与背景连成一体）----
        _titleBar = new SoftPanel { VeilAlpha = 170 };
        _titleBar.MouseDown += TitleBar_MouseDown;

        lblBarTitle = new Label
        {
            Text = "◇  C盘缓存清理",
            Font = new Font(Theme.FontFamily, 11.5F, FontStyle.Bold),
            ForeColor = Theme.TextMain,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        _titleBar.Controls.Add(lblBarTitle);
        lblBarTitle.MouseDown += TitleBar_MouseDown;

        btnMin = new CaptionButton('\uE921', CaptionKind.Minimize);
        btnClose = new CaptionButton('\uE8BB', CaptionKind.Close);
        _titleBar.Controls.Add(btnMin);
        _titleBar.Controls.Add(btnClose);
        Controls.Add(_titleBar);

        // ---- 头部第一层（58px）：产品名 ----
        _headerTitle = new SoftPanel { VeilAlpha = 170 };

        lblTitle = new Label
        {
            Text = "C盘缓存清理",
            Font = new Font(Theme.FontFamily, 19F, FontStyle.Bold),
            ForeColor = Theme.TextMain,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        lblSubTitle = new Label
        {
            Text = "Cache Cleaner",
            Font = Theme.SubTitleFont,
            ForeColor = Theme.TextSub,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        _headerTitle.Controls.Add(lblTitle);
        _headerTitle.Controls.Add(lblSubTitle);
        Controls.Add(_headerTitle);

        // ---- 头部第二层（46px）：操作栏 ----
        _headerToolbar = new SoftPanel { VeilAlpha = 190 };

        btnScan = MakeButton("扫描缓存", '\uE721', ButtonStyle.Primary);
        btnClean = MakeButton("清理选中", '\uE74D', ButtonStyle.Secondary);
        btnSelectAll = MakeButton("全选", '\uE73A', ButtonStyle.Ghost);
        btnSelectNone = MakeButton("取消选择", '\uE739', ButtonStyle.Ghost);
        btnCancel = MakeButton("取消扫描", '\uE711', ButtonStyle.Ghost);
        btnTrend = MakeButton("趋势分析", '\uE9D2', ButtonStyle.Secondary);

        btnScan.Click += BtnScan_Click;
        btnClean.Click += BtnClean_Click;
        btnSelectAll.Click += (_, _) => SetAllChecked(true);
        btnSelectNone.Click += (_, _) => SetAllChecked(false);
        btnCancel.Click += (_, _) => _cts?.Cancel();
        btnTrend.Click += (_, _) => new GrowthDialog().ShowDialog(this);

        foreach (var b in new[] { btnScan, btnClean, btnSelectAll, btnSelectNone, btnCancel, btnTrend })
        {
            FitButton(b);
            if (b.Width < 88) b.Width = 88;   // 短文案按钮保底宽度，避免拥挤
        }

        _headerToolbar.Controls.AddRange([btnScan, btnClean, btnSelectAll, btnSelectNone, btnCancel, btnTrend]);
        Controls.Add(_headerToolbar);

        // ---- 主内容区：圆角 12 浅色面板（扫描结果 + 空状态）----
        contentPanel = new SoftPanel { VeilAlpha = _state.VeilAlpha() };

        lblSection = new Label
        {
            Text = "扫描结果",
            Font = Theme.SectionFont,
            ForeColor = Theme.TextMain,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        lblFound = new Label
        {
            Text = "",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSub,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        lblEmpty = new Label
        {
            Text = "还没有扫描缓存\n点击扫描，看看 C 盘藏了什么",
            Font = new Font(Theme.FontFamily, 13F),
            ForeColor = Theme.TextSub,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            Visible = true
        };
        var listPanel = new RoundedContainer { Dock = DockStyle.Fill, Padding = new Padding(1) };
        contentPanel.Controls.Add(listPanel);
        Controls.Add(contentPanel);

        // ---- 数据表格（沿用组件，重做观感）----
        dgv = new AnimeDataGridView(this)
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(0xE8, 0xEE, 0xF6),
            EnableHeadersVisualStyles = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ReadOnly = false,
            MultiSelect = false,
            ShowCellToolTips = true,
            Font = new Font(Theme.FontFamily, 12F),
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Color.FromArgb(0xF2, 0xF6, 0xFB),
                ForeColor = Theme.TextMain,
                Font = new Font(Theme.FontFamily, 12F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                SelectionBackColor = Color.FromArgb(0xF2, 0xF6, 0xFB),
                SelectionForeColor = Theme.TextMain
            },
            ColumnHeadersHeight = 34,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            DefaultCellStyle =
            {
                BackColor = Color.White,
                ForeColor = Theme.TextMain,
                SelectionBackColor = Color.FromArgb(0xEA, 0xF2, 0xFF),
                SelectionForeColor = Theme.TextMain,
                Padding = new Padding(Theme.SpaceS, 0, Theme.SpaceS, 0),
                WrapMode = DataGridViewTriState.False
            },
            AlternatingRowsDefaultCellStyle =
            {
                BackColor = Color.FromArgb(0xFA, 0xFC, 0xFF),
                ForeColor = Theme.TextMain,
                SelectionBackColor = Color.FromArgb(0xEA, 0xF2, 0xFF),
                SelectionForeColor = Theme.TextMain,
                Padding = new Padding(Theme.SpaceS, 0, Theme.SpaceS, 0),
                WrapMode = DataGridViewTriState.False
            },
            RowTemplate = { Height = 38 }
        };

        var colCheck = new DataGridViewCheckBoxColumn
        {
            Name = "Checked", HeaderText = "", Width = 44,
            TrueValue = true, FalseValue = false, IndeterminateValue = false
        };
        var colName = new DataGridViewTextBoxColumn
        {
            Name = "Name", HeaderText = "缓存项目", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 56,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        var colSize = new DataGridViewTextBoxColumn
        {
            Name = "Size", HeaderText = "大小", Width = 126, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
        };
        var colDesc = new DataGridViewTextBoxColumn
        {
            Name = "Desc", HeaderText = "说明", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 44,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        var colRisk = new DataGridViewTextBoxColumn
        {
            Name = "Risk", HeaderText = "状态", Width = 68, ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                ForeColor = Theme.TextMain
            }
        };
        var colPath = new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "路径", Visible = false, ReadOnly = true };

        dgv.Columns.AddRange([colCheck, colName, colSize, colDesc, colRisk, colPath]);
        dgv.CellContentClick += Dgv_CellContentClick;
        dgv.CellFormatting += Dgv_CellFormatting;
        dgv.CellPainting += Dgv_CellPainting;
        dgv.CellMouseEnter += Dgv_CellMouseEnter;
        dgv.CellMouseLeave += Dgv_CellMouseLeave;
        listPanel.Controls.Add(dgv);
        var sectionStrip = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(248, 251, 255) };
        lblSection.Font = Theme.BodyBoldFont;
        lblSection.Location = new Point(14, 10);
        sectionStrip.Controls.AddRange([lblSection, lblFound]);
        void PositionFound() => lblFound.Location = new Point(Math.Max(0, sectionStrip.Width - lblFound.Width - 14), 12);
        sectionStrip.Resize += (_, _) => PositionFound();
        lblFound.SizeChanged += (_, _) => PositionFound();
        listPanel.Controls.Add(sectionStrip);
        dgv.BringToFront();
        // 空状态属于表格，避免兄弟控件互相遮挡。
        dgv.Controls.Add(lblEmpty);
        dgv.Resize += (_, _) => lblEmpty.Bounds = new Rectangle(24, 50, Math.Max(1, dgv.Width * 2 / 5), Math.Max(1, dgv.Height - 70));

        // ---- 底部状态区（46px）----
        _statusArea = new SoftPanel { VeilAlpha = 190, ShowTopLine = true };

        lblStateDot = new Label
        {
            Text = "●",
            Font = new Font(Theme.FontFamily, 9F),
            ForeColor = Theme.TextSub,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        lblStateTitle = new Label
        {
            Text = "就绪",
            Font = new Font(Theme.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Theme.TextMain,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        lblStateDetail = new Label
        {
            Text = "",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSub,
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent
        };
        lblReclaim = new Label
        {
            Text = "",
            Font = Theme.BodyFont,
            ForeColor = Theme.TextMain,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        lblSelected = new Label
        {
            Text = "已选择 0 B",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSub,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        chkPreview = new CheckBox
        {
            Text = "预览模式",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSub,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        chkPreview.CheckedChanged += (_, _) => AppSettings.PreviewMode = chkPreview.Checked;

        _statusArea.Controls.Add(lblStateDot);
        _statusArea.Controls.Add(lblStateTitle);
        _statusArea.Controls.Add(lblStateDetail);
        _statusArea.Controls.Add(lblReclaim);
        _statusArea.Controls.Add(lblSelected);
        _headerTitle.Controls.Add(chkPreview);
        _statusArea.Controls.Add(lblCount = new Label { AutoSize = true, Font = Theme.SmallFont, ForeColor = Theme.TextSub, BackColor = Color.Transparent });
        Controls.Add(_statusArea);

        // ---- 细进度条（仅扫描/清理期间出现）----
        progress = new ProgressLite { Visible = false };
        Controls.Add(progress);
    }

    private static GradientButton MakeButton(string text, char icon, ButtonStyle style)
    {
        return new GradientButton { Text = text, IconGlyph = icon, Style = style, Size = new Size(112, 36) };
    }

    /// <summary>按钮宽度按内容自适应（图标 16 + 间距 7 + 文字 + 左右内边距 15×2）</summary>
    private static void FitButton(GradientButton b)
    {
        var iconW = b.IconGlyph != default ? TextRenderer.MeasureText(b.IconGlyph.ToString(), Theme.IconFont).Width + 7 : 0;
        var textW = TextRenderer.MeasureText(b.Text, b.Font).Width;
        b.Width = iconW + textW + 34;
    }

    // ==================== 绝对布局（固定尺寸窗口）====================

    private void LayoutUI()
    {
        // OnResize 会在构造期间（控件尚未创建）触发，守卫避免 NRE
        if (!_layoutReady) return;
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        if (w < 100 || h < 100) return;
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);

        int y = 0;
        _titleBar.Bounds = new Rectangle(0, y, w, S(BarH));
        y += S(BarH);
        _headerTitle.Bounds = new Rectangle(0, y, w, S(TitleH));
        y += S(TitleH);
        _headerToolbar.Bounds = new Rectangle(0, y, w, S(ToolbarH));
        lblTitle.Location = new Point(S(20), S(5));
        lblSubTitle.Location = new Point(lblTitle.Right + S(14), S(17));
        chkPreview.Location = new Point(w - chkPreview.Width - S(24), S(18));

        // 操作栏：左主操作组（间距 8），趋势分析靠右
        int bx = S(Theme.SpaceXL);
        foreach (var button in new[] { btnScan, btnClean, btnSelectAll, btnSelectNone, btnCancel })
        {
            FitButton(button);
            button.Height = S(36);
            button.Location = new Point(bx, S(5));
            bx += button.Width + S(8);
        }
        FitButton(btnTrend);
        btnTrend.Height = S(36);
        btnTrend.Location = new Point(w - btnTrend.Width - S(Theme.SpaceXL), S(5));
        y += S(ToolbarH);

        // 内容区（弹性）
        int bottomReserve = S(StatusH + ProgressH + Theme.SpaceS * 2);
        contentPanel.Bounds = new Rectangle(S(Theme.SpaceXL), y + S(Theme.SpaceS),
            w - S(Theme.SpaceXL * 2), Math.Max(1, h - y - bottomReserve - S(Theme.SpaceS)));
        contentPanel.Padding = new Padding(0);

        // 进度条 + 状态区
        progress.Bounds = new Rectangle(S(Theme.SpaceXL), h - S(StatusH + Theme.SpaceM + ProgressH + 2),
            w - S(Theme.SpaceXL * 2), S(ProgressH));
        _statusArea.Bounds = new Rectangle(S(Theme.SpaceXL), h - S(StatusH + Theme.SpaceM),
            w - S(Theme.SpaceXL * 2), S(StatusH));
        LayoutStatusArea(_statusArea);

        // 标题栏子控件
        lblBarTitle.Location = new Point(S(Theme.SpaceL), S(10));
        btnMin.Bounds = new Rectangle(w - S(92), 0, S(46), S(BarH));
        btnClose.Bounds = new Rectangle(w - S(46), 0, S(46), S(BarH));
    }

    /// <summary>统计靠右，详情限制可用宽度；预览开关独立放在头部。</summary>
    private void LayoutStatusArea(Control sa)
    {
        if (!_layoutReady) return;
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        lblSelected.Location = new Point(sa.Width - lblSelected.Width - S(12), S(15));
        lblReclaim.Location = new Point(lblSelected.Left - lblReclaim.Width - S(16), S(14));
        lblStateDot.Location = new Point(S(12), S(16));
        lblStateTitle.Location = new Point(S(32), S(14));
        lblStateDetail.Bounds = new Rectangle(lblStateTitle.Right + S(12), S(15),
            Math.Max(0, lblReclaim.Left - lblStateTitle.Right - S(28)), S(24));
        lblCount.Location = new Point(sa.Width - lblCount.Width - S(12), 0);
    }

    /// <summary>统一设置操作按钮的启用状态</summary>
    private void SetControlsEnabled(bool enabled)
    {
        btnScan.Enabled = enabled;
        btnClean.Enabled = enabled && dgv.Rows.Count > 0;
        btnSelectAll.Enabled = enabled && dgv.Rows.Count > 0;
        btnSelectNone.Enabled = enabled && dgv.Rows.Count > 0;
        btnTrend.Enabled = enabled;
        chkPreview.Enabled = enabled;
        btnCancel.Enabled = !enabled;   // 取消常驻：仅扫描/清理期间可点
    }

    /// <summary>状态机驱动：白纱 / 状态圆点 / 状态文案 / 主按钮动态</summary>
    private void SetState(UiState state, string? title = null, string? detail = null)
    {
        _state = state;
        btnCancel.Text = state == UiState.Cleaning ? "取消清理" : "取消扫描";
        LayoutUI();
        contentPanel.VeilAlpha = state.VeilAlpha();
        contentPanel.Invalidate();

        lblStateDot.ForeColor = state switch
        {
            UiState.Idle => Theme.TextSub,
            UiState.Scanning or UiState.Cleaning => Theme.Primary,
            UiState.ScanCompleted or UiState.CleanCompleted => Theme.Success,
            UiState.Error => Theme.Risk,
            _ => Theme.Warn
        };
        if (title != null) lblStateTitle.Text = title;
        if (detail != null) lblStateDetail.Text = detail;
        LayoutStatusArea(_statusArea);

        // 动态主按钮：扫描前主按钮是「扫描缓存」，扫描完成后让位给「清理选中」
        if (state == UiState.ScanCompleted || state == UiState.CleanCompleted)
        {
            btnScan.Style = ButtonStyle.Secondary;
            btnClean.Style = ButtonStyle.Primary;
        }
        else
        {
            btnScan.Style = ButtonStyle.Primary;
            btnClean.Style = ButtonStyle.Secondary;
        }
        btnScan.Invalidate();
        btnClean.Invalidate();

        lblEmpty.Visible = dgv.Rows.Count == 0 && state == UiState.Idle;
    }

    private void Dgv_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && dgv.Columns[e.ColumnIndex].Name == "Checked")
        {
            dgv.EndEdit();
            UpdateSelected();
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

        if (dgv.Columns[e.ColumnIndex].Name == "Name")
        {
            var pathVal = dgv.Rows[e.RowIndex].Cells["Path"].Value?.ToString();
            if (string.IsNullOrEmpty(pathVal))
                e.CellStyle!.ForeColor = Theme.TextSub;
        }
    }

    /// <summary>状态列小型圆角标签（浅底深字，替代大色块文字）</summary>
    private void Dgv_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (dgv.Columns[e.ColumnIndex].Name != "Risk" || e.Value is not RiskLevel risk) return;
        var tag = risk switch { RiskLevel.Safe => "安全", RiskLevel.Warn => "注意", _ => "危险" };

        var (bg, fg) = tag switch
        {
            "安全" => (Color.FromArgb(0xE4, 0xF5, 0xEC), Color.FromArgb(0x17, 0x84, 0x5B)),
            "注意" => (Color.FromArgb(0xFD, 0xF0, 0xDC), Color.FromArgb(0xA9, 0x6A, 0x00)),
            "危险" => (Color.FromArgb(0xFB, 0xE4, 0xE4), Color.FromArgb(0xC0, 0x39, 0x2B)),
            _ => (Color.White, Theme.TextSub)
        };

        e.PaintBackground(e.ClipBounds, true);
        var r = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - 52) / 2, e.CellBounds.Y + 9, 52, 20);
        using (var path = Theme.Rounded(r, 6))
        using (var b = new SolidBrush(bg))
        {
            e.Graphics!.FillPath(b, path);
        }
        TextRenderer.DrawText(e.Graphics!, tag, Theme.SmallFont, r, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        e.Handled = true;
    }

    /// <summary>行悬停：淡蓝 #F4F8FF</summary>
    private void Dgv_CellMouseEnter(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= dgv.Rows.Count) return;
        _hoverRow = e.RowIndex;
        var row = dgv.Rows[e.RowIndex];
        if (row.Selected) return;
        row.DefaultCellStyle.BackColor = Color.FromArgb(0xF4, 0xF8, 0xFF);
    }

    private void Dgv_CellMouseLeave(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= dgv.Rows.Count) return;
        _hoverRow = -1;
        var row = dgv.Rows[e.RowIndex];
        if (row.Selected) return;
        row.DefaultCellStyle.BackColor = (e.RowIndex % 2 == 0) ? Color.White : Color.FromArgb(0xFA, 0xFC, 0xFF);
    }

    /// <summary>
    /// 扫描按钮点击（三阶段：已知缓存 + 自动发现 + 项目工件）
    /// </summary>
    private async void BtnScan_Click(object? sender, EventArgs e)
    {
        SetControlsEnabled(false);
        SetState(UiState.Scanning, "扫描中", "");
        progress.Indeterminate = true;
        progress.Visible = true;
        lblCount.Text = "";
        dgv.Rows.Clear();
        lblFound.Text = "";
        lblEmpty.Visible = false;
        LayoutStatusArea(_statusArea);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var knownProgress = new Progress<(int current, int total, string name)>(p =>
        {
            progress.Indeterminate = false;
            progress.Maximum = p.total;
            progress.Value = Math.Min(p.current, p.total);
            lblStateTitle.Text = "扫描中";
            lblStateDetail.Text = $"正在检查 {p.name}";
            lblCount.Text = $"{p.current} / {p.total}";
            LayoutStatusArea(_statusArea);
        });

        try
        {
            // Phase 1: 已知缓存（快速，1-2秒）
            var knownItems = await Task.Run(() => CacheScanner.ScanAll(knownProgress, token));

            foreach (var item in knownItems)
            {
                if (!item.Exists) continue;
                AddCacheRow(item, isKnown: true);
            }

            UpdateSelected();
            lblStateTitle.Text = "扫描中";
            lblStateDetail.Text = "自动发现更多缓存目录";
            progress.Indeterminate = true;

            // Phase 2+3: 自动发现 + 项目工件（较慢）
            var autoProgress = new Progress<(int dirsScanned, string currentPath)>(p =>
            {
                progress.Indeterminate = true;
                lblStateDetail.Text = $"自动发现缓存目录（已检查 {p.dirsScanned} 个目录）";
            });

            var autoItems = await Task.Run(() =>
                CacheScanner.ScanAutoDiscovered(knownItems, autoProgress, token));

            int autoCount = 0;
            foreach (var item in autoItems)
            {
                if (!item.Exists) continue;
                AddCacheRow(item, isKnown: false);
                autoCount++;
            }

            // 审计日志：记录本次扫描的全部条目
            CleanLog.LogScan(knownItems.Concat(autoItems).ToList());

            // 按大小降序排列：清理收益一目了然；无路径的命令式项（0 B）自然沉底
            dgv.Sort(dgv.Columns["Size"]!, ListSortDirection.Descending);

            long reclaimable = 0;
            for (int i = 0; i < dgv.Rows.Count; i++)
                reclaimable += dgv.Rows[i].Cells["Size"].Value as long? ?? 0;

            SetState(UiState.ScanCompleted, "扫描完成", $"发现 {dgv.Rows.Count} 个缓存项");
            lblReclaim.Text = $"可释放 {CacheScanner.FormatSize(reclaimable)}";
            lblFound.Text = $"已发现 {dgv.Rows.Count} 项";
            lblCount.Text = "";
            progress.Visible = false;
            progress.Indeterminate = false;
            lblEmpty.Visible = dgv.Rows.Count == 0;
            LayoutStatusArea(_statusArea);
            UpdateSelected();
        }
        catch (OperationCanceledException)
        {
            SetState(UiState.Cancelled, "已取消扫描", "");
            progress.Visible = false;
            progress.Indeterminate = false;
            lblCount.Text = "";
            lblEmpty.Visible = dgv.Rows.Count == 0;
        }
        catch (Exception ex)
        {
            SetState(UiState.Error, "扫描出错", ex.Message);
            progress.Visible = false;
            progress.Indeterminate = false;
            Debug.WriteLine($"扫描异常: {ex}");
        }
        finally
        {
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

        // 自动发现项文字降一档（信息权重：已知规则优先）
        if (!isKnown)
            dgv.Rows[rowIdx].Cells["Name"].Style.ForeColor = Theme.TextSub;

        // 默认勾选：有上次清理记录时按记录恢复；首次使用（无记录）按已知安全项自动勾选；
        // 自动发现按目录名匹配（≠纯缓存），一律交由用户逐项确认
        if (isKnown)
        {
            bool check = AppSettings.HasCheckedSnapshot
                ? AppSettings.CheckedNames.Contains(item.Name)
                : item.Risk == RiskLevel.Safe;
            dgv.Rows[rowIdx].Cells["Checked"].Value = check;
        }
    }

    /// <summary>
    /// 清理按钮点击
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

        bool dryRun = chkPreview.Checked;
        if (dryRun)
            warning = "【预览模式】不会删除任何文件，仅统计将释放的空间。\n" + warning;

        // 检测正在运行、可能占用缓存的程序（P1-1）
        var runningApps = CacheScanner.DetectRunningTargets();
        if (runningApps.Count > 0)
            warning += $"\n\n检测到以下程序正在运行，其缓存可能被占用：\n  {string.Join("、", runningApps)}\n建议先关闭后再清理，否则相关文件将被跳过或登记为重启删除。";

        var result = MessageBox.Show(
            $"确认清理 {selectedItems.Count} 个项目？{warning}",
            "确认清理",
            MessageBoxButtons.OKCancel,
            dangerItems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question
        );

        if (result != DialogResult.OK) return;

        // 设置持久化：本次勾选与预览开关（下次扫描按记录恢复勾选）
        var checkedNames = new List<string>();
        for (int i = 0; i < dgv.Rows.Count; i++)
            if (dgv.Rows[i].Cells["Checked"].Value is true)
                checkedNames.Add(dgv.Rows[i].Cells["Name"].Value?.ToString() ?? "");
        AppSettings.Save(chkPreview.Checked, checkedNames);

        // 释放量的诚实口径：以磁盘可用空间差为准（其他程序并发写入也会影响该差值）
        long freeBefore = CacheScanner.GetCFreeBytes();
        CleanLog.LogCleanStart(selectedItems.Count, freeBefore, dryRun);
        CacheScanner.DryRun = dryRun;

        SetControlsEnabled(false);
        SetState(UiState.Cleaning, "清理中", "");
        progress.Indeterminate = false;
        progress.Maximum = selectedItems.Count;
        progress.Value = 0;
        progress.Visible = true;
        lblCount.Text = $"0 / {selectedItems.Count}";
        LayoutStatusArea(_statusArea);

        _cts = new CancellationTokenSource();
        var progress2 = new Progress<string>(msg => lblStateDetail.Text = msg);
        var total = new CleanResult();

        try
        {
            for (int i = 0; i < selectedItems.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var (rowIdx, name, path, _) = selectedItems[i];

                lblStateDetail.Text = name;
                progress.Value = i + 1;
                lblCount.Text = $"{i + 1} / {selectedItems.Count}";
                dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(0xFD, 0xF3, 0xDC);

                var item = new CacheItem { Name = name, Path = path, Exists = true };
                var itemResult = await Task.Run(() => CacheScanner.CleanItem(item, progress2, _cts.Token));
                total += itemResult;
                CleanLog.LogCleanItem(name, path, itemResult);

                // 预览：仅高亮不改大小；FreedBytes>0 表示释放了空间；DeletedCount>0 覆盖命令式项
                if (dryRun)
                {
                    dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(0xFB, 0xF0, 0xD4);
                }
                else if (itemResult.FreedBytes > 0 || itemResult.DeletedCount > 0)
                {
                    dgv.Rows[rowIdx].Cells["Size"].Value = item.SizeBytes;
                    dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(0xE4, 0xF5, 0xEC);
                }
                else
                {
                    dgv.Rows[rowIdx].DefaultCellStyle.BackColor = Color.White;
                }
            }

            string freedStr = CacheScanner.FormatSize(total.FreedBytes);
            long freeAfter = CacheScanner.GetCFreeBytes();
            string statusDetail = total.TotalFailures > 0 ? $"（跳过 {total.TotalFailures} 个）" : "";
            SetState(dryRun ? UiState.ScanCompleted : UiState.CleanCompleted,
                dryRun ? "预览完成" : "清理完成",
                dryRun ? $"将释放 {freedStr}（未实际删除）" : $"释放 {freedStr}{statusDetail} · 磁盘净增 {CacheScanner.FormatSize(Math.Max(0, freeAfter - freeBefore))}");
            progress.Visible = false;
            lblCount.Text = "";
            LayoutStatusArea(_statusArea);

            MessageBox.Show(
                BuildCleanSummary(total, freedStr, freeBefore, freeAfter, dryRun),
                dryRun ? "预览完成（未删除任何文件）" : "清理完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

        }
        catch (OperationCanceledException)
        {
            SetState(UiState.Cancelled, "已取消清理", "");
            progress.Visible = false;
            lblCount.Text = "";
        }
        catch (Exception ex)
        {
            SetState(UiState.Error, "清理出错", ex.Message);
            progress.Visible = false;
            Debug.WriteLine($"清理异常: {ex}");
        }
        finally
        {
            CacheScanner.DryRun = false;
            SetControlsEnabled(true);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 构建清理完成汇总文案：释放量 + 删除文件数 + 失败/待处理明细 + 磁盘可用空间前后差。
    /// 预览模式改为「将释放」口径，不展示磁盘差值（未实际删除，差值无意义）。
    /// </summary>
    private static string BuildCleanSummary(CleanResult total, string freedStr, long freeBefore, long freeAfter, bool dryRun)
    {
        var summary = dryRun
            ? $"预览完成（未删除任何文件）！\n\n将释放: {freedStr}\n涉及 {total.DeletedCount} 个文件/命令"
            : $"清理完成！\n\n共释放: {freedStr}\n删除 {total.DeletedCount} 个文件";

        var detailParts = new List<string>();
        if (total.LockedCount > 0) detailParts.Add($"{total.LockedCount} 个被占用");
        if (total.PermissionDeniedCount > 0) detailParts.Add($"{total.PermissionDeniedCount} 个权限不足");
        if (total.OtherFailureCount > 0) detailParts.Add($"{total.OtherFailureCount} 个其它失败");
        if (total.PendingRebootCount > 0) detailParts.Add($"{total.PendingRebootCount} 个将于重启时删除");

        if (detailParts.Count > 0)
        {
            summary += $"\n\n跳过/待处理：{string.Join("，", detailParts)}";
            if (total.LockedCount > 0)
                summary += "\n\n提示：被占用的文件通常是相关程序正在运行，关闭程序后再次清理即可。";
        }

        if (dryRun)
        {
            summary += "\n\n关闭「预览模式」后重新执行即可实际清理。";
        }
        else
        {
            summary += $"\n\n磁盘可用空间: {CacheScanner.FormatSize(freeBefore)} → {CacheScanner.FormatSize(freeAfter)}" +
                       $"（净增 {CacheScanner.FormatSize(Math.Max(0, freeAfter - freeBefore))}）";
            summary += "\n（文件累计与磁盘差值可能不同：其他程序同时在写入，重启删除的空间在重启后才回收）";
        }

        return summary;
    }

    /// <summary>已选择体积（同步到状态区）</summary>
    private void UpdateSelected()
    {
        long total = 0;
        for (int i = 0; i < dgv.Rows.Count; i++)
        {
            if (dgv.Rows[i].Cells["Checked"].Value is true)
                total += dgv.Rows[i].Cells["Size"].Value as long? ?? 0;
        }
        lblSelected.Text = $"已选择 {CacheScanner.FormatSize(total)}";
        LayoutStatusArea(_statusArea);
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
        UpdateSelected();
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

    [DllImport("dwmapi.dll")]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 自定义 DataGridView：底图切片绘制（空闲时角色透出），行高 38、淡分隔、无纵线
    /// </summary>
    private class AnimeDataGridView : DataGridView
    {
        private readonly MainForm _owner;

        public AnimeDataGridView(MainForm owner)
        {
            _owner = owner;
            DoubleBuffered = true;
        }

        protected override void PaintBackground(Graphics graphics, Rectangle clipBounds, Rectangle gridBounds)
        {
            try
            {
                // 切片绘制窗体背景（空闲时角色透出；内容由白色主面板保证可读性）
                var gridLoc = _owner.PointToClient(PointToScreen(Point.Empty));
                Theme.DrawSlice(graphics, new Rectangle(gridLoc, Size), _owner.ClientSize);
                using var veil = new SolidBrush(Color.FromArgb(_owner._state == UiState.Idle ? 115 : 205, 255, 255, 255));
                graphics.FillRectangle(veil, clipBounds);
            }
            catch
            {
                base.PaintBackground(graphics, clipBounds, gridBounds);
            }
        }
    }

    /// <summary>标题栏按钮种类</summary>
    private enum CaptionKind { Minimize, Close }

    /// <summary>自绘标题栏按钮（最小化/关闭；hover 灰 / 关闭 hover 红）</summary>
    private sealed class CaptionButton : Control
    {
        private readonly char _glyph;
        private readonly CaptionKind _kind;
        private bool _hover;

        public CaptionButton(char glyph, CaptionKind kind)
        {
            _glyph = glyph;
            _kind = kind;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe MDL2 Assets", 10F);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme.DrawSliceUnder(this, e.Graphics);
            if (Parent is SoftPanel panel)
            {
                using var veil = new SolidBrush(Color.FromArgb(panel.VeilAlpha, 248, 251, 255));
                e.Graphics.FillRectangle(veil, ClientRectangle);
            }
            if (_hover)
            {
                using var b = new SolidBrush(_kind == CaptionKind.Close
                    ? Color.FromArgb(0xE8, 0x11, 0x23)
                    : Color.FromArgb(0xE2, 0xEA, 0xF6));
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var color = (_hover && _kind == CaptionKind.Close) ? Color.White : Theme.TextMain;
            TextRenderer.DrawText(e.Graphics, _glyph.ToString(), Font, ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_kind == CaptionKind.Minimize)
                (FindForm() as MainForm)?.MinimizeWindow();
            else
                (FindForm() as MainForm)?.Close();
        }
    }

    internal void MinimizeWindow() => WindowState = FormWindowState.Minimized;
}
