$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SmokeWindow {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$exe = Join-Path $workspace 'publish\CacheCleaner.exe'
$oldCursor = [System.Windows.Forms.Cursor]::Position
$process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
try {
    $root = $null
    for ($i=0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw '发布程序启动后退出' }
        $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))
        if ($root) { break }
    }
    if (!$root) { throw '未找到发布程序窗口' }
    function Find-Control($name) {
        $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
    }
    function Click-Control($name) {
        $control = Find-Control $name
        if (!$control) { throw "缺少控件 $name" }
        $rect = $control.Current.BoundingRectangle
        [SmokeWindow]::SetCursorPos([int]($rect.X+$rect.Width/2),[int]($rect.Y+$rect.Height/2)) | Out-Null
        [SmokeWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
        [SmokeWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    }
    function Save-Screenshot($name) {
        [SmokeWindow]::SetWindowPos([IntPtr]$root.Current.NativeWindowHandle,[IntPtr](-1),0,0,0,0,0x43) | Out-Null
        $root.SetFocus(); Start-Sleep -Milliseconds 300
        $rect=$root.Current.BoundingRectangle
        $bitmap=[System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen([int]$rect.X,[int]$rect.Y,0,0,$bitmap.Size)
            $bitmap.Save((Join-Path $workspace "docs\screenshots\$name"),[System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    Write-Output "PASS 发布程序启动，版本 $((Get-Item $exe).VersionInfo.FileVersion)"
    Save-Screenshot 'main.png'
    Click-Control '扫描缓存'
    for ($i=0; $i -lt 160; $i++) {
        Start-Sleep -Milliseconds 250
        if (Find-Control '扫描完成') { break }
        if (Find-Control '扫描出错') { throw '扫描失败' }
    }
    if (!(Find-Control '扫描完成')) { Click-Control '取消扫描'; throw '扫描超时，已请求取消' }
    if (!(Find-Control '处理结果')) { throw '缺少处理结果列' }
    Save-Screenshot 'scan.png'
    Click-Control '全选'; Click-Control '取消选择'
    Write-Output 'PUBLISHED_SMOKE_OK：实际扫描完成，处理结果列可见，全选/取消选择成功；未执行清理'
} finally {
    $process.Refresh()
    # 只结束本脚本创建的进程，避免关闭事件保存扫描时的临时勾选。
    if (!$process.HasExited) { Stop-Process -Id $process.Id -Force }
    [SmokeWindow]::SetCursorPos($oldCursor.X,$oldCursor.Y) | Out-Null
}
