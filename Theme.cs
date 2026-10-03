using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace CacheCleaner;

/// <summary>
/// 设计系统（单一定义点）：色彩 / 字体 / 间距 / 圆角 / 背景层 / 按钮层级 / 进度条。
/// 背景 = 整窗浅色底 + 右侧等比插画与渐变遮罩，内容面板保证数据可读性。
/// </summary>
internal static class Theme
{
    // ---- 色彩系统（规范第八节）----
    public static readonly Color PageBg = Color.FromArgb(0xF6, 0xF9, 0xFE);
    public static readonly Color PanelBg = Color.White;
    public static readonly Color Primary = Color.FromArgb(0x6E, 0xA8, 0xFE);
    public static readonly Color PrimaryHover = Color.FromArgb(0x5D, 0x99, 0xF6);
    public static readonly Color PrimaryPressed = Color.FromArgb(0x4F, 0x8A, 0xE8);
    public static readonly Color Secondary = Color.FromArgb(0x91, 0xA7, 0xFF);
    public static readonly Color Accent = Color.FromArgb(0x68, 0xC5, 0xD8);
    public static readonly Color TextMain = Color.FromArgb(0x24, 0x32, 0x4A);
    public static readonly Color TextSub = Color.FromArgb(0x70, 0x80, 0x99);
    public static readonly Color Border = Color.FromArgb(0xDD, 0xE7, 0xF2);
    public static readonly Color HoverBg = Color.FromArgb(0xF2, 0xF7, 0xFD);
    public static readonly Color SecondaryBg = Color.FromArgb(0xEE, 0xF4, 0xFF);
    public static readonly Color Success = Color.FromArgb(0x3B, 0xB4, 0x81);
    public static readonly Color Warn = Color.FromArgb(0xD6, 0x8A, 0x2A);
    public static readonly Color Risk = Color.FromArgb(0xE0, 0x6C, 0x6C);
    public static readonly Color Selection = Color.FromArgb(0xDC, 0xEA, 0xFB);
    public static readonly Color SelectionText = Color.FromArgb(0x1F, 0x3A, 0x5F);

    // ---- 字体系统（规范第十六节）----
    public const string FontFamily = "Microsoft YaHei UI";
    public static readonly Font TitleFont = new(FontFamily, 18F, FontStyle.Bold);          // 主标题
    public static readonly Font SubTitleFont = new(FontFamily, 11.5F);                     // 副标题
    public static readonly Font SectionFont = new(FontFamily, 14F, FontStyle.Bold);        // 区块标题
    public static readonly Font BodyFont = new(FontFamily, 12.5F);                         // 正文/按钮
    public static readonly Font BodyBoldFont = new(FontFamily, 12.5F, FontStyle.Bold);     // 表头/强调
    public static readonly Font SmallFont = new(FontFamily, 11.5F);                        // 次级/状态
    public static readonly Font IconFont = new("Segoe MDL2 Assets", 12F);                  // 线性图标（Fluent）

    // ---- 间距体系（规范第二十二节）----
    public const int SpaceXS = 4, SpaceS = 8, SpaceM = 12, SpaceL = 16, SpaceXL = 20, SpaceXXL = 32;
    public const int RadiusButton = 9, RadiusPanel = 12;

    /// <summary>加载内嵌主题插画</summary>
    public static Image? LoadThemeImage()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CacheCleaner.theme_bg.png");
            if (stream == null) return null;
            using var source = Image.FromStream(stream);
            return new Bitmap(source);
        }
        catch { return null; }
    }

    // ---- 背景层（Layer 0-1）：整窗底色 + 右侧插画 ----
    private static readonly object Gate = new();
    private static Bitmap? _bg;
    private static Size _bgSize;

    public static void EnsureBackground(Size clientSize)
    {
        lock (Gate)
        {
            if (_bg != null && _bgSize == clientSize) return;
            var old = _bg;
            _bg = BuildBackground(clientSize);
            _bgSize = clientSize;
            old?.Dispose();
        }
    }

    public static Bitmap? Background => _bg;

    /// <summary>
    /// 插画按高度等比放在右侧，渐变遮罩将左侧平滑过渡到页面底色。
    /// </summary>
    private static Bitmap BuildBackground(Size size)
    {
        using var src = LoadThemeImage();
        var w = Math.Max(1, size.Width);
        var h = Math.Max(1, size.Height);
        var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(PageBg);
            if (src != null)
            {
                // 原素材是圆角图标；只采样内部插画，避免将外侧深色边角铺进窗口。
                var crop = new RectangleF(src.Width * .17f, src.Height * .13f, src.Width * .70f, src.Height * .76f);
                var scale = h / crop.Height;
                var dw = crop.Width * scale;
                var dh = crop.Height * scale;
                var dst = new RectangleF(w - dw, (h - dh) / 2, dw, dh);
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.72f });
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, Rectangle.Round(dst), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attrs);
            }

            // 左侧保持干净，右侧保留角色；遮住图标素材裁剪区的硬边。
            using var veil = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                Color.FromArgb(225, 246, 249, 254), Color.FromArgb(85, 246, 249, 254), LinearGradientMode.Horizontal);
            veil.InterpolationColors = new ColorBlend
            {
                Positions = [0f, .35f, .58f, 1f],
                Colors = [PageBg, PageBg, Color.FromArgb(120, PageBg), Color.FromArgb(85, PageBg)]
            };
            g.FillRectangle(veil, 0, 0, w, h);
        }
        return bmp;
    }

    /// <summary>把子控件位置对应的背景切片绘制出来（Layer 2 面板的基础）</summary>
    public static void DrawSliceUnder(Control control, Graphics g)
    {
        var form = control.FindForm();
        if (form == null || control.Parent == null) return;
        var loc = form.PointToClient(control.Parent.PointToScreen(control.Location));
        DrawSlice(g, new Rectangle(loc, control.Size), form.ClientSize);
    }

    /// <summary>从整窗背景切出目标矩形（客户区坐标）</summary>
    public static void DrawSlice(Graphics g, Rectangle bounds, Size clientSize)
    {
        EnsureBackground(clientSize);
        if (_bg == null) return;
        g.DrawImage(_bg,
            new Rectangle(0, 0, bounds.Width, bounds.Height),
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            GraphicsUnit.Pixel);
    }

    /// <summary>圆角矩形路径</summary>
    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}

/// <summary>应用 UI 状态（规范第二十节）：驱动白纱强度 / 状态圆点 / 空状态</summary>
internal enum UiState { Idle, Scanning, ScanCompleted, Cleaning, CleanCompleted, Cancelled, Error }

internal static class UiStateExtensions
{
    /// <summary>内容区白纱透明度：空闲时角色最明显，扫描/清理时内容可读性优先</summary>
    public static int VeilAlpha(this UiState state) => state switch
    {
        UiState.Idle => 96,            // 角色明显（空状态）
        UiState.Scanning => 158,
        UiState.ScanCompleted => 132,
        UiState.Cleaning => 158,
        UiState.CleanCompleted => 132,
        UiState.Cancelled => 110,
        UiState.Error => 120,
        _ => 132
    };
}

/// <summary>
/// 半透明内容面板（Layer 2）：切片绘制背景 + 按状态可调的白纱。
/// </summary>
internal sealed class SoftPanel : Panel
{
    /// <summary>白纱 alpha（0-255），由主窗体按 UiState 驱动</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int VeilAlpha { get; set; } = 96;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowTopLine { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowBottomLine { get; set; }

    public SoftPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Theme.DrawSliceUnder(this, e.Graphics);
        using var veil = new SolidBrush(Color.FromArgb(VeilAlpha, 248, 251, 255));
        e.Graphics.FillRectangle(veil, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var line = new Pen(Theme.Border);
        if (ShowTopLine) e.Graphics.DrawLine(line, 0, 0, Width, 0);
        if (ShowBottomLine) e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>
/// 圆角内容容器（Panel 圆角 12px + 边框 #DDE7F2）：主内容区的白色半透明底。
/// </summary>
internal sealed class RoundedContainer : Panel
{
    public RoundedContainer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Padding = new Padding(1);
    }

    protected override void OnResize(EventArgs e)
    {
        using var path = Theme.Rounded(new Rectangle(0, 0, Width, Height), Theme.RadiusPanel);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
        Invalidate();
        base.OnResize(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 白色面板底（Layer 2），圆角外露出背景层
        Theme.DrawSliceUnder(this, e.Graphics);
        using var veil = new SolidBrush(Color.FromArgb(234, 255, 255, 255));
        e.Graphics.FillRectangle(veil, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        using var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusPanel);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>按钮视觉层级（规范第十节）</summary>
internal enum ButtonStyle { Primary, Secondary, Ghost }

/// <summary>
/// 圆角按钮（8-10px 圆角，高 36，图标 16-18 + 文字 6-8px 间距）：
/// Primary 实心蓝 + 轻阴影；Secondary 浅蓝底蓝字轻边框；Ghost 透明、hover 浅蓝灰。
/// hover/pressed 120-200ms 过渡动画（规范第二十六节），线性图标 Segoe MDL2。
/// </summary>
internal sealed class GradientButton : Control
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ButtonStyle Style { get; set; } = ButtonStyle.Primary;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal char IconGlyph { get; set; }

    private float _anim;      // 0=常态 1=悬停
    private bool _down;
    private System.Windows.Forms.Timer? _timer;
    private bool _hover;

    public GradientButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.PushButton;
        Font = new Font(Theme.FontFamily, 10.5F);
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(116, 36);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        AccessibleName = Text;   // 自绘控件的 UIA 可见性
        base.OnTextChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; StartAnim(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; StartAnim(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _down = false; Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    public void PerformClick()
    {
        if (Enabled && Visible) OnClick(EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { _down = true; Invalidate(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_down && (e.KeyCode is Keys.Space or Keys.Enter))
        {
            _down = false; Invalidate(); PerformClick(); e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessibility(this);

    private sealed class ButtonAccessibility(GradientButton owner) : ControlAccessibleObject(owner)
    {
        public override string DefaultAction => "按下";
        public override void DoDefaultAction() => owner.PerformClick();
    }

    private void StartAnim()
    {
        if (_timer == null)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 15 };
            _timer.Tick += (_, _) =>
            {
                var target = _hover ? 1f : 0f;
                _anim += Math.Sign(target - _anim) * 0.125f;
                if (Math.Abs(target - _anim) < 0.02f || _anim is < 0f or > 1f)
                {
                    _anim = target;
                    _timer.Stop();
                }
                Invalidate();
            };
        }
        _timer.Start();
    }

    private (Color fill, Color border, Color text) Palette()
    {
        var t = _anim;
        if (!Enabled) return (Theme.SecondaryBg, Theme.Border, Theme.TextSub);
        switch (Style)
        {
            case ButtonStyle.Primary:
                var pFill = _down ? Theme.PrimaryPressed : Theme.Lerp(Theme.Primary, Theme.PrimaryHover, t);
                return (pFill, pFill, Color.White);
            case ButtonStyle.Secondary:
                var sFill = Theme.Lerp(Theme.SecondaryBg, Theme.HoverBg, t);
                return (sFill, Theme.Border, Theme.PrimaryPressed);
            case ButtonStyle.Ghost:
                var gFill = Color.FromArgb((int)(150 * t) + (_down ? 40 : 0), Theme.HoverBg);
                return (gFill, Color.FromArgb((int)(160 * t), Theme.Border), Theme.TextSub);
            default:
                return (Theme.Primary, Theme.Primary, Color.White);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Ghost/Secondary 的圆角外露出背景层（插画延伸）
        Theme.DrawSliceUnder(this, e.Graphics);
        if (Parent is SoftPanel panel)
        {
            using var veil = new SolidBrush(Color.FromArgb(panel.VeilAlpha, 248, 251, 255));
            e.Graphics.FillRectangle(veil, ClientRectangle);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(rect, 9);
        var (fill, border, text) = Palette();

        if (Style == ButtonStyle.Primary)
        {
            // 轻阴影（仅 Primary，规范第十节）
            using var shadowPath = Theme.Rounded(new Rectangle(0, 2, Width - 1, Height - 1), 9);
            using var shadow = new SolidBrush(Color.FromArgb(36, 80, 110, 180));
            g.FillPath(shadow, shadowPath);
        }

        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }
        using (var pen = new Pen(border))
        {
            g.DrawPath(pen, path);
        }

        // 线性图标 + 文字（图标 16-18px，间距 6-8px，垂直居中）
        using var textBrush = new SolidBrush(text);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        var textSize = g.MeasureString(Text, Font);
        float textX = 0, textW = Width;
        if (IconGlyph != default)
        {
            var iconSize = g.MeasureString(IconGlyph.ToString(), Theme.IconFont);
            var total = iconSize.Width + 7 + textSize.Width;
            var iconX = (Width - total) / 2;
            g.DrawString(IconGlyph.ToString(), Theme.IconFont, textBrush,
                new RectangleF(iconX, 0, iconSize.Width, Height), format);
            textX = iconX + iconSize.Width + 7;
            textW = textSize.Width + 6;
        }
        g.DrawString(Text, Font, textBrush, new RectangleF(textX, 0, textW, Height), format);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -5, -5), text, fill);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// 细圆角进度条（规范第十九节）：高 6px、圆角、主色蓝填充、浅蓝灰轨道；
/// 支持确定值与不确定（扫描自动发现阶段滑动块）两种形态。不使用系统默认绿条。
/// </summary>
internal sealed class ProgressLite : Control
{
    private int _value;
    private int _maximum = 100;
    private bool _indeterminate;
    private float _marquee;
    private System.Windows.Forms.Timer? _timer;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }

    public ProgressLite()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 6;
    }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Value { get => _value; set { _value = Math.Clamp(value, 0, Maximum); Invalidate(); } }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Maximum { get => _maximum; set { _maximum = Math.Max(1, value); Invalidate(); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            _indeterminate = value;
            if (Visible && _indeterminate) _timer?.Start();
            else _timer?.Stop();
            Invalidate();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _indeterminate) _timer?.Start();
        else _timer?.Stop();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) =>
        {
            _marquee += 0.035f;
            if (_marquee > 1.35f) _marquee = -0.35f;
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, (Height - 6) / 2, Width - 1, 6);
        using (var path = Theme.Rounded(track, 3))
        using (var b = new SolidBrush(Color.FromArgb(0xE4, 0xEC, 0xF7)))
        {
            g.FillPath(b, path);
        }

        if (_indeterminate)
        {
            var w = (int)(Width * 0.22f);
            var x = (int)((Width - w) * _marquee);
            var seg = new Rectangle(Math.Max(0, x), track.Y, w, 6);
            using var path = Theme.Rounded(seg, 3);
            using var b = new LinearGradientBrush(track, Theme.Primary, Theme.Accent, 0f);
            g.FillPath(b, path);
            return;
        }

        var frac = Maximum <= 0 ? 0 : (double)_value / Maximum;
        var fw = (int)((Width - 1) * frac);
        if (fw < 6 && _value > 0) fw = 6;
        if (fw > 0)
        {
            var fill = new Rectangle(0, track.Y, Math.Min(fw, Width - 1), 6);
            using var path = Theme.Rounded(fill, 3);
            using var b = new LinearGradientBrush(track, Theme.Primary, Theme.Accent, 0f);
            g.FillPath(b, path);
        }
    }
}
