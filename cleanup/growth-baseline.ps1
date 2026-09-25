# growth-baseline.ps1 - disk growth baseline & diff for drive C: (read-only, deletes nothing)
#
# Usage (from the repo root):
#   powershell -NoProfile -ExecutionPolicy Bypass -File cleanup\growth-baseline.ps1
#   powershell ... growth-baseline.ps1 -Reset          # discard old baseline, start fresh
#   powershell ... growth-baseline.ps1 -BaselineDir D:\snapshots
#
# First run  : creates the baseline and prints current usage.
# Later runs : prints the per-location delta (growth in MB) and refreshes the baseline.
# Tip        : run weekly (elevated for full C:\Windows coverage). A location that keeps
#              growing >100 MB/day between runs is your leak - investigate that path.

param(
  [switch]$Reset,
  [string]$BaselineDir = (Join-Path $env:LOCALAPPDATA 'CacheCleaner')
)

$ErrorActionPreference = 'SilentlyContinue'
$baselineFile = Join-Path $BaselineDir 'growth-baseline.json'

# zh-CN robocopy labels the summary size line in CJK; build the regex from code points
# so this file stays ASCII-safe for Windows PowerShell 5.1 (no-BOM scripts read as ANSI)
$byteLabel = [string][char]0x5B57 + [char]0x8282
$rxBytes = '^\s*(?:Bytes|' + $byteLabel + ')\s*:\s*(\d+)'

# robocopy list-only mode: fast recursive size, junction-safe (/XJ), long-path safe.
# 'C:\__rcnull__' destination is never created because of /L.
function SZ([string]$p){
  if(-not (Test-Path -LiteralPath $p)){ return $null }
  $o = robocopy $p 'C:\__rcnull__' /L /E /NJH /NFL /NDL /NP /BYTES /R:0 /W:0 /XJ 2>$null
  $m = ($o | Select-String -Pattern $rxBytes)
  if($m -and $m.Matches.Count -gt 0){ [long]$m.Matches[0].Groups[1].Value } else { $null }
}

function Get-UwpLocalCacheTotal {
  $s = 0L
  Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -EA SilentlyContinue | ForEach-Object {
    $p = Join-Path $_.FullName 'LocalCache'
    if(Test-Path $p){ $x = SZ $p; if($x){ $s += $x } }
  }
  return $s
}

# tracked locations; 'PACKAGES_LOCALCACHE' aggregates every UWP app LocalCache dir.
# WinSxS is hardlink-inflated; deltas are still meaningful.
$targets = @(
  ,@('AppData\Local (total)',        "$env:LOCALAPPDATA")
  ,@('AppData\Roaming (total)',      "$env:APPDATA")
  ,@('WeChat4 data (xwechat_files)', "$env:USERPROFILE\xwechat_files")
  ,@('Tencent (Roaming)',            "$env:APPDATA\Tencent")
  ,@('Baidu (Roaming)',              "$env:APPDATA\baidu")
  ,@('VS Code VSIX cache',           "$env:APPDATA\Code\CachedExtensionVSIXs")
  ,@('VS Code (Roaming total)',      "$env:APPDATA\Code")
  ,@('~/.cache',                     "$env:USERPROFILE\.cache")
  ,@('~/.codex',                     "$env:USERPROFILE\.codex")
  ,@('~/.vscode',                    "$env:USERPROFILE\.vscode")
  ,@('OpenAI Codex (Local)',         "$env:LOCALAPPDATA\OpenAI")
  ,@('OpenCodex',                    "$env:LOCALAPPDATA\OpenCodex")
  ,@('Doubao',                       "$env:LOCALAPPDATA\Doubao")
  ,@('Feishu (LarkShell)',           "$env:APPDATA\LarkShell")
  ,@('UWP LocalCache (all apps)',    'PACKAGES_LOCALCACHE')
  ,@('CBS service logs',             'C:\Windows\Logs\CBS')
  ,@('WMI/ETW trace logs',           'C:\Windows\System32\LogFiles\WMI')
  ,@('Windows\Temp',                 'C:\Windows\Temp')
  ,@('User Temp',                    "$env:LOCALAPPDATA\Temp")
  ,@('VS/Package Cache',             'C:\ProgramData\Package Cache')
  ,@('WinSxS (inflated)',            'C:\Windows\WinSxS')
  ,@('Windows\Installer',            'C:\Windows\Installer')
  ,@('CrashDumps',                   "$env:LOCALAPPDATA\CrashDumps")
  ,@('WER reports',                  'C:\ProgramData\Microsoft\Windows\WER')
)

'Collecting snapshot ({0}) ...' -f (Get-Date)
$free = (Get-PSDrive C).Free
$sizes = [ordered]@{}
foreach($t in $targets){
  if($t[1] -eq 'PACKAGES_LOCALCACHE'){ $sizes[$t[0]] = Get-UwpLocalCacheTotal }
  else { $sizes[$t[0]] = SZ $t[1] }
}

$prev = $null
if(-not $Reset -and (Test-Path -LiteralPath $baselineFile)){
  try { $prev = Get-Content -LiteralPath $baselineFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $prev = $null }
}

''
'== C: free space =='
if($prev){
  $dFree = ($free - [long]$prev.freeBytesC)/1MB
  '{0,10:N1} GB   (delta {1:+#,0;-#,0;0} MB since {2})' -f ($free/1GB), $dFree, $prev.capturedAt
} else {
  '{0,10:N1} GB   (first run - baseline created below)' -f ($free/1GB)
}

''
'== Per-location delta (sorted by growth) =='
if($prev){
  $rows = @()
  foreach($k in $sizes.Keys){
    $new = $sizes[$k]; if($null -eq $new){ $new = -1 }
    $old = $prev.sizes.$k; if($null -eq $old){ $old = -1 }
    if($new -lt 0 -and $old -lt 0){ continue }
    $rows += [pscustomobject]@{ N=$k; New=$new; Delta=($new - $old) }
  }
  $rows | Sort-Object Delta -Descending | ForEach-Object {
    $newStr = if($_.New -ge 0){ '{0,10:N1} MB' -f ($_.New/1MB) } else { '     (gone)' }
    if($_.Delta -gt 1MB){ '{0}  {1}  GREW {2:N1} MB' -f $newStr, $_.N, ($_.Delta/1MB) }
    elseif($_.Delta -lt -1MB){ '{0}  {1}  shrank {2:N1} MB' -f $newStr, $_.N, (-$_.Delta/1MB) }
    else { '{0}  {1}  ~unchanged' -f $newStr, $_.N }
  }
} else {
  foreach($k in $sizes.Keys){
    if($null -ne $sizes[$k]){ '{0,10:N1} MB  {1}' -f ($sizes[$k]/1MB), $k }
  }
}

if(-not (Test-Path -LiteralPath $BaselineDir)){
  New-Item -ItemType Directory -Path $BaselineDir -Force | Out-Null
}
$snap = [ordered]@{ capturedAt = (Get-Date).ToString('s'); freeBytesC = $free; sizes = $sizes }
$snap | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $baselineFile -Encoding UTF8
''
('Baseline saved to {0}' -f $baselineFile)
