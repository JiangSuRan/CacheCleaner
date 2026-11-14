using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CacheCleaner;

/// <summary>
/// 增长分析对话框（两个视图）：
/// ① 最近大文件：按「最后写入时间」找出最近 N 天内新写入的大文件（&gt;50 MB）排行，
///    直接回答「谁在吃 C 盘」。这是 2026-09-25 定位 WPR 遗留追踪 / CBS 日志增长源所用
///    诊断脚本的产品化。
/// ② 位置增长对比：与 cleanup/growth-baseline.ps1 共用同一基线 JSON
///    （%LOCALAPPDATA%\CacheCleaner\growth-baseline.json），按固定位置清单对比上次/本次
///    占用差，持续 &gt;100 MB/天即泄漏点。每次对比后基线刷新为本次，与 PS 脚本互认。
/// 两个视图均只读，绝不删除任何文件。
/// </summary>
public class GrowthDialog : Form
{
    private static readonly Font FontNormal = new("微软雅黑", 9.5F);
    private static readonly Font FontSmall = new("微软雅黑", 9F);

    private ComboBox cboDays = null!;
    private Button btnStart = null!;
    private Label lblStatus = null!;
    private Label lblAdvice = null!;
    private ListView lvFiles = null!;
    private CancellationTokenSource? _cts;

    private Button btnBaseline = null!;
    private Label lblBaselineStatus = null!;
    private ListView lvLocations = null!;
    private CancellationTokenSource? _ctsBaseline;

    // 迁盘顾问：结果中命中即给出官方迁盘命令（只提示不执行）——对小系统盘，官方迁盘比反复清理更根本
    private static readonly (string Marker, string Hint)[] RelocationHints =
    [
        ("\\npm-cache", "npm：npm config set cache D:\\dev-cache\\npm --global"),
        ("\\pnpm", "pnpm：pnpm config set store-dir D:\\dev-cache\\pnpm-store"),
        ("\\uv\\cache", "uv：setx UV_CACHE_DIR D:\\dev-cache\\uv"),
        ("\\pip\\cache", "pip：setx PIP_CACHE_DIR D:\\dev-cache\\pip"),
        ("\\go-build", "Go 构建缓存：setx GOCACHE D:\\dev-cache\\go-build"),
        ("\\go\\pkg\\mod", "Go 模块：setx GOPATH D:\\dev-cache\\go"),
        ("\\.cargo\\registry", "Rust：setx CARGO_HOME D:\\dev-cache\\cargo"),
        ("\\.gradle\\caches", "Gradle：setx GRADLE_USER_HOME D:\\dev-cache\\gradle"),
        ("\\.ollama", "Ollama 模型：setx OLLAMA_MODELS D:\\dev-models\\ollama"),
        ("\\.cache\\huggingface", "HuggingFace 缓存：setx HF_HOME D:\\dev-models\\huggingface"),
        ("\\Docker\\wsl", "Docker Desktop：设置 → Resources → 磁盘镜像位置移到其他盘"),
        ("\\ms-playwright", "Playwright 浏览器：setx PLAYWRIGHT_BROWSERS_PATH D:\\dev-cache\\playwright")
    ];

    // 系统相关扫描根在运行时按实际 Windows/ProgramData 位置构建（通用性：不假设系统在 C 盘）
    private static string[] BuildScanRoots()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return
        [
            "%APPDATA%", "%LOCALAPPDATA%",
            "%USERPROFILE%\\xwechat_files", "%USERPROFILE%\\Documents",
            "%USERPROFILE%\\Downloads", "%USERPROFILE%\\Desktop",
            programData,
            Path.Combine(windows, "Temp"), Path.Combine(windows, "Logs"),
            Path.Combine(windows, "System32", "LogFiles"),
            Path.Combine(windows, "SoftwareDistribution"),
            Path.Combine(windows, "LiveKernelReports"), Path.Combine(windows, "Minidump")
        ];
    }

    private const long MinFileSize = 50L * 1024 * 1024;
    private const int MaxResults = 50;

    // === 位置增长对比：位置清单与 cleanup/growth-baseline.ps1 的 $targets 保持一致 ===
    // PathOrToken 为特殊 token PACKAGES_LOCALCACHE 时聚合全部 UWP 包的 LocalCache
    private const string PackagesLocalCacheToken = "PACKAGES_LOCALCACHE";

    private static readonly (string Label, string PathOrToken)[] BaselineTargets =
    [
        ("AppData\\Local (total)", "%LOCALAPPDATA%"),
        ("AppData\\Roaming (total)", "%APPDATA%"),
        ("WeChat4 data (xwechat_files)", "%USERPROFILE%\\xwechat_files"),
        ("Tencent (Roaming)", "%APPDATA%\\Tencent"),
        ("Baidu (Roaming)", "%APPDATA%\\baidu"),
        ("VS Code VSIX cache", "%APPDATA%\\Code\\CachedExtensionVSIXs"),
        ("VS Code (Roaming total)", "%APPDATA%\\Code"),
        ("~/.cache", "%USERPROFILE%\\.cache"),
        ("~/.codex", "%USERPROFILE%\\.codex"),
        ("~/.vscode", "%USERPROFILE%\\.vscode"),
        ("OpenAI Codex (Local)", "%LOCALAPPDATA%\\OpenAI"),
        ("OpenCodex", "%LOCALAPPDATA%\\OpenCodex"),
        ("Doubao", "%LOCALAPPDATA%\\Doubao"),
        ("Feishu (LarkShell)", "%APPDATA%\\LarkShell"),
        ("UWP LocalCache (all apps)", PackagesLocalCacheToken),
        ("CBS service logs", "%WINDIR%\\Logs\\CBS"),
        ("WMI/ETW trace logs", "%WINDIR%\\System32\\LogFiles\\WMI"),
        ("Windows\\Temp", "%WINDIR%\\Temp"),
        ("User Temp", "%LOCALAPPDATA%\\Temp"),
        ("VS/Package Cache", "%PROGRAMDATA%\\Package Cache"),
        ("WinSxS (inflated)", "%WINDIR%\\WinSxS"),
        ("Windows\\Installer", "%WINDIR%\\Installer"),
        ("CrashDumps", "%LOCALAPPDATA%\\CrashDumps"),
        ("WER reports", "%PROGRAMDATA%\\Microsoft\\Windows\\WER"),
    ];

    // 泄漏启发线：两次运行间持续 >100 MB/天增长的位置视为泄漏点（与 PS 脚本口径一致）
    private const double LeakMbPerDay = 100;

    private static string BaselinePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CacheCleaner", "growth-baseline.json");

    // zh-CN 下 robocopy 汇总行是「字节」，正则按码点写保持源文件 ASCII 兼容
    private static readonly Regex RxBytes =
        new(@"^\s*(?:Bytes|\u5b57\u8282)\s*:\s*(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// robocopy list-only 求目录总字节：junction 安全（/XJ）、长路径安全；
    /// 目标 C:\__rcnull__ 因 /L 永不创建。目录不存在返回 -1，测量失败亦 -1。
    /// </summary>
    private static long MeasureLocation(string dir, CancellationToken token)
    {
        try
        {
            if (!Directory.Exists(dir)) return -1;
            var (_, output) = CommandRunner.Run("robocopy",
                $"\"{dir}\" \"C:\\__rcnull__\" /L /E /NJH /NFL /NDL /NP /BYTES /R:0 /W:0 /XJ",
                600_000, token);
            var m = RxBytes.Match(output);
            return m.Success ? long.Parse(m.Groups[1].Value) : -1;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"测量 {dir} 失败: {ex.Message}");
            return -1;
        }
    }

    private static long MeasureUwpLocalCacheTotal(CancellationToken token)
    {
        var packagesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packagesRoot)) return -1;

        long sum = 0;
        bool any = false;
        try
        {
            foreach (var pkg in Directory.EnumerateDirectories(packagesRoot))
            {
                token.ThrowIfCancellationRequested();
                var localCache = Path.Combine(pkg, "LocalCache");
                if (!Directory.Exists(localCache)) continue;
                var size = MeasureLocation(localCache, token);
                if (size >= 0) { sum += size; any = true; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"测量 UWP LocalCache 失败: {ex.Message}");
        }
        return any ? sum : -1;
    }

    private static long MeasureFreeC()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").AvailableFreeSpace; }
        catch { return 0; }
    }

    private static (DateTime CapturedAt, long FreeBytes, Dictionary<string, long> Sizes)? LoadBaseline()
    {
        try
        {
            if (!File.Exists(BaselinePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(BaselinePath));
            var root = doc.RootElement;
            var sizes = new Dictionary<string, long>();
            foreach (var prop in root.GetProperty("sizes").EnumerateObject())
            {
                // PS 端写 null 表示「测不到」，读侧归一为 -1
                sizes[prop.Name] = prop.Value.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetInt64(out var v) ? v : -1;
            }
            return (root.GetProperty("capturedAt").GetDateTime(),
                    root.GetProperty("freeBytesC").GetInt64(), sizes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取增长基线失败: {ex.Message}");
            return null;
        }
    }

    private static void SaveBaseline(DateTime capturedAt, long freeBytes, Dictionary<string, long?> sizes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        var dto = new BaselineDto(capturedAt.ToString("s"), freeBytes, sizes);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true,
            // 位置标签含中文，避免 \u 转义影响 PS 脚本可读性
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        // UTF-8 BOM：与 PS 5.1 Set-Content -Encoding UTF8 的输出保持一致
        File.WriteAllText(BaselinePath, json, new UTF8Encoding(true));
    }

    private sealed record BaselineDto(string capturedAt, long freeBytesC, Dictionary<string, long?> sizes);

    public GrowthDialog()
    {
        SetupUi();
    }

    private void SetupUi()
    {
        Text = "增长分析 — 谁在吃 C 盘";
        Size = new Size(960, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 480);
        BackColor = Color.FromArgb(230, 240, 250);

        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(12, 6, 12, 4),
            BackColor = Color.FromArgb(180, 230, 240, 250)
        };
        var lblDesc = new Label
        {
            Text = "两个视图定位增长源：最近大文件看「谁在写」，位置对比看「哪里涨」。非管理员运行会漏读部分系统目录；全程只读，不删除任何文件。",
            Dock = DockStyle.Fill,
            Font = FontSmall,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };
        top.Controls.Add(lblDesc);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabFiles = new TabPage("最近大文件");
        var tabLocations = new TabPage("位置增长对比");
        tabs.TabPages.Add(tabFiles);
        tabs.TabPages.Add(tabLocations);

        // --- Tab 1：最近大文件（原有视图） ---
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent
        };
        row.Controls.Add(new Label
        {
            Text = "时间范围：",
            AutoSize = true,
            Font = FontNormal,
            Margin = new Padding(3, 7, 3, 0),
            BackColor = Color.Transparent
        });
        cboDays = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 72,
            Font = FontNormal
        };
        cboDays.Items.AddRange(["3 天", "7 天", "14 天"]);
        cboDays.SelectedIndex = 1;
        row.Controls.Add(cboDays);
        btnStart = new Button
        {
            Text = "开始分析",
            Width = 92,
            Height = 28,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = FontNormal,
            Cursor = Cursors.Hand
        };
        btnStart.Click += BtnStart_Click;
        row.Controls.Add(btnStart);
        lblStatus = new Label
        {
            Text = "",
            AutoSize = true,
            Font = FontSmall,
            Margin = new Padding(12, 8, 3, 0),
            BackColor = Color.Transparent
        };
        row.Controls.Add(lblStatus);

        lvFiles = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Font = FontNormal,
            BackColor = Color.FromArgb(240, 245, 255)
        };
        lvFiles.Columns.Add("大小", 110);
        lvFiles.Columns.Add("最后写入", 140);
        lvFiles.Columns.Add("文件路径", 640);
        lblAdvice = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            Font = FontSmall,
            ForeColor = Color.FromArgb(0, 90, 160),
            BackColor = Color.FromArgb(222, 239, 250),
            Visible = false,
            Padding = new Padding(12, 6, 12, 4)
        };
        // Dock 布局按加入逆序：Bottom 条先占位，Fill 占其余空间
        tabFiles.Controls.Add(lvFiles);
        tabFiles.Controls.Add(lblAdvice);
        tabFiles.Controls.Add(row);

        // --- Tab 2：位置增长对比 ---
        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent
        };
        btnBaseline = new Button
        {
            Text = "对比并刷新基线",
            Width = 128,
            Height = 28,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = FontNormal,
            Cursor = Cursors.Hand
        };
        btnBaseline.Click += BtnBaseline_Click;
        row2.Controls.Add(btnBaseline);
        lblBaselineStatus = new Label
        {
            Text = "",
            AutoSize = true,
            Font = FontSmall,
            Margin = new Padding(12, 8, 3, 0),
            BackColor = Color.Transparent
        };
        row2.Controls.Add(lblBaselineStatus);

        lvLocations = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Font = FontNormal,
            BackColor = Color.FromArgb(240, 245, 255),
            GridLines = true
        };
        lvLocations.Columns.Add("位置", 230);
        lvLocations.Columns.Add("上次", 100, HorizontalAlignment.Right);
        lvLocations.Columns.Add("本次", 100, HorizontalAlignment.Right);
        lvLocations.Columns.Add("增量", 110, HorizontalAlignment.Right);
        lvLocations.Columns.Add("日增速", 100, HorizontalAlignment.Right);
        tabLocations.Controls.Add(lvLocations);
        tabLocations.Controls.Add(row2);

        Controls.Add(tabs);
        Controls.Add(top);

        FormClosed += (_, _) => { _cts?.Cancel(); _ctsBaseline?.Cancel(); };
    }

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        int days = cboDays.SelectedIndex switch { 0 => 3, 2 => 14, _ => 7 };

        btnStart.Enabled = false;
        lvFiles.Items.Clear();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        lblStatus.Text = "正在扫描……";

        try
        {
            var results = await Task.Run(() => CollectRecentBigFiles(days, token), token);

            results.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            foreach (var (bytes, lastWrite, filePath) in results)
            {
                var item = new ListViewItem(CacheScanner.FormatSize(bytes));
                item.SubItems.Add(lastWrite.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(filePath);
                lvFiles.Items.Add(item);
            }

            long totalBytes = results.Sum(r => r.Bytes);
            lblStatus.Text = results.Count == 0
                ? $"最近 {days} 天没有 >50 MB 的新写入文件"
                : $"共 {results.Count} 个大文件，合计 {CacheScanner.FormatSize(totalBytes)}";

            // 迁盘顾问：命中已知可迁盘的大缓存时，给出官方迁盘命令
            var hints = RelocationHints
                .Where(h => results.Any(r => r.FilePath.Contains(h.Marker, StringComparison.OrdinalIgnoreCase)))
                .Select(h => "• " + h.Hint)
                .Distinct()
                .Take(3)
                .ToList();
            lblAdvice.Visible = hints.Count > 0;
            if (hints.Count > 0)
            {
                lblAdvice.Text = "💡 迁盘顾问（对小系统盘，官方迁盘比反复清理更根本）：\n" + string.Join("\n", hints);
                lblAdvice.Height = 34 + hints.Count * 18;
            }
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"分析失败: {ex.Message}";
            Debug.WriteLine($"增长分析异常: {ex}");
        }
        finally
        {
            btnStart.Enabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void BtnBaseline_Click(object? sender, EventArgs e)
    {
        btnBaseline.Enabled = false;
        lvLocations.Items.Clear();
        _ctsBaseline = new CancellationTokenSource();
        var token = _ctsBaseline.Token;
        lblBaselineStatus.Text = "正在测量各位置（robocopy 统计，WinSxS 较慢，请稍候）……";

        try
        {
            var prev = await Task.Run(() => LoadBaseline(), token);
            var current = await Task.Run(() =>
            {
                var sizes = new Dictionary<string, long>();
                foreach (var (label, pathOrToken) in BaselineTargets)
                {
                    token.ThrowIfCancellationRequested();
                    sizes[label] = pathOrToken == PackagesLocalCacheToken
                        ? MeasureUwpLocalCacheTotal(token)
                        : MeasureLocation(Environment.ExpandEnvironmentVariables(pathOrToken), token);
                }
                return sizes;
            }, token);
            var freeNow = await Task.Run(MeasureFreeC, token);
            currentSizes = current;

            long freeDelta = 0;
            if (prev != null)
            {
                freeDelta = freeNow - prev.Value.FreeBytes;
                lblBaselineStatus.Text = $"C 盘可用 {CacheScanner.FormatSize(prev.Value.FreeBytes)} → {CacheScanner.FormatSize(freeNow)}" +
                    $"（净增 {DeltaStr(freeDelta)}） · 基线时间 {prev.Value.CapturedAt:yyyy-MM-dd HH:mm} · 对比完成";
            }
            else
            {
                lblBaselineStatus.Text = $"首次运行，已建立基线（C 盘可用 {CacheScanner.FormatSize(freeNow)}）。下次对比将显示各位置增量。";
            }

            double days = prev != null ? Math.Max(0.5, (DateTime.Now - prev.Value.CapturedAt).TotalDays) : 0;
            var rows = new List<(string Label, long Prev, long Cur)>();
            foreach (var (label, _) in BaselineTargets)
            {
                current.TryGetValue(label, out var cur);
                var p = prev?.Sizes.TryGetValue(label, out var pv) == true ? pv : -1;
                rows.Add((label, p, cur));
            }

            rows.Sort((a, b) => ((b.Cur < 0 ? 0 : b.Cur) - (b.Prev < 0 ? 0 : b.Prev))
                .CompareTo((a.Cur < 0 ? 0 : a.Cur) - (a.Prev < 0 ? 0 : a.Prev)));

            foreach (var (label, p, cur) in rows)
            {
                string prevStr = p < 0 ? "（无记录）" : CacheScanner.FormatSize(p);
                string curStr = cur < 0 ? "（测不到）" : CacheScanner.FormatSize(cur);
                string deltaStr = p < 0 || cur < 0 ? "—" : DeltaStr(cur - p);
                string rateStr;
                if (p < 0 || cur < 0 || days <= 0) rateStr = "—";
                else
                {
                    var mbPerDay = (cur - p) / 1024.0 / 1024.0 / days;
                    rateStr = $"{mbPerDay:+0.#;-0.#;0} MB/天";
                }
                var item = new ListViewItem(label) { ForeColor = Color.FromArgb(60, 60, 60) };
                item.SubItems.Add(prevStr);
                item.SubItems.Add(curStr);
                item.SubItems.Add(deltaStr);
                item.SubItems.Add(rateStr);
                // 泄漏启发线：持续 >100 MB/天 标红提示（与 PS 脚本口径一致）
                if (p >= 0 && cur >= 0 && days > 0 && (cur - p) / 1024.0 / 1024.0 / days > LeakMbPerDay)
                    item.ForeColor = Color.Firebrick;
                lvLocations.Items.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            lblBaselineStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            lblBaselineStatus.Text = $"对比失败: {ex.Message}";
            Debug.WriteLine($"位置增长对比异常: {ex}");
        }
        finally
        {
            btnBaseline.Enabled = true;
            _ctsBaseline?.Dispose();
            _ctsBaseline = null;
        }

        // 对比完成后把基线刷新为本次（失败不影响显示）
        try
        {
            var freeForSave = MeasureFreeC();
            var sizesForSave = BaselineTargets.ToDictionary(
                t => t.Label, t => currentSizes.TryGetValue(t.Label, out var v) && v >= 0 ? (long?)v : null);
            SaveBaseline(DateTime.Now, freeForSave, sizesForSave);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存增长基线失败: {ex.Message}");
        }
    }

    private Dictionary<string, long> currentSizes = [];

    private static string DeltaStr(long bytes) =>
        bytes > 0 ? $"+{CacheScanner.FormatSize(bytes)}"
        : bytes < 0 ? $"-{CacheScanner.FormatSize(-bytes)}"
        : "~不变";

    private static List<(long Bytes, DateTime LastWrite, string FilePath)> CollectRecentBigFiles(
        int days, CancellationToken token)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        var results = new List<(long Bytes, DateTime LastWrite, string FilePath)>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            // 跳过 junction/符号链接：AppData 里存在指向祖先的兼容 junction，跟随会环形重复计数
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var root in BuildScanRoots())
        {
            token.ThrowIfCancellationRequested();
            var expanded = Environment.ExpandEnvironmentVariables(root);
            if (!Directory.Exists(expanded)) continue;

            try
            {
                foreach (var file in new DirectoryInfo(expanded).EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (file.Length < MinFileSize) continue;
                    if (file.LastWriteTime < cutoff) continue;
                    results.Add((file.Length, file.LastWriteTime, file.FullName));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"增长分析跳过 {expanded}: {ex.Message}");
            }
        }

        // 只保留前 MaxResults 大，避免长列表卡 UI
        results.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        if (results.Count > MaxResults) results.RemoveRange(MaxResults, results.Count - MaxResults);
        return results;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _ctsBaseline?.Cancel();
            _ctsBaseline?.Dispose();
        }
        base.Dispose(disposing);
    }
}
