using System.ComponentModel;
using System.Diagnostics;

namespace CacheCleaner;

/// <summary>
/// 增长分析对话框：按「最后写入时间」找出最近 N 天内新写入的大文件（&gt;50 MB）排行，
/// 直接回答「谁在吃 C 盘」。这是 2026-09-25 定位 WPR 遗留追踪 / CBS 日志增长源所用
/// 诊断脚本的产品化。只读，绝不删除任何文件。
/// </summary>
public class GrowthDialog : Form
{
    private static readonly Font FontNormal = new("微软雅黑", 9.5F);
    private static readonly Font FontSmall = new("微软雅黑", 9F);

    private ComboBox cboDays = null!;
    private Button btnStart = null!;
    private Label lblStatus = null!;
    private ListView lvFiles = null!;
    private CancellationTokenSource? _cts;

    // 扫描根：用户可写区域 + 常见系统日志区；枚举跳过 reparse point 防止 junction 环
    private static readonly string[] ScanRoots =
    [
        "%APPDATA%", "%LOCALAPPDATA%",
        "%USERPROFILE%\\xwechat_files", "%USERPROFILE%\\Documents",
        "%USERPROFILE%\\Downloads", "%USERPROFILE%\\Desktop",
        "C:\\ProgramData",
        "C:\\Windows\\Temp", "C:\\Windows\\Logs",
        "C:\\Windows\\System32\\LogFiles",
        "C:\\Windows\\SoftwareDistribution",
        "C:\\Windows\\LiveKernelReports", "C:\\Windows\\Minidump"
    ];

    private const long MinFileSize = 50L * 1024 * 1024;
    private const int MaxResults = 50;

    public GrowthDialog()
    {
        SetupUi();
    }

    private void SetupUi()
    {
        Text = "增长分析 — 最近新写入的大文件";
        Size = new Size(960, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 480);
        BackColor = Color.FromArgb(230, 240, 250);

        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(12, 8, 12, 4),
            BackColor = Color.FromArgb(180, 230, 240, 250)
        };

        var lblDesc = new Label
        {
            Text = "找出最近新写入的大文件（>50 MB），排行即当前的增长源。非管理员运行会漏读部分系统目录；分析过程只读，不删除任何文件。",
            Dock = DockStyle.Fill,
            Font = FontSmall,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };

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

        // 先加 Fill 再加 Bottom：WinForms 按加入的逆序布局，Bottom 条先占位，描述占其余空间
        top.Controls.Add(lblDesc);
        top.Controls.Add(row);
        Controls.Add(top);

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
        Controls.Add(lvFiles);
        Controls.SetChildIndex(lvFiles, 0);

        FormClosed += (_, _) => _cts?.Cancel();
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

        foreach (var root in ScanRoots)
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
        }
        base.Dispose(disposing);
    }
}
