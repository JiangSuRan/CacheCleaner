param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference='Stop'
function Median($values) {
    $ordered=@($values | Sort-Object)
    if (!$ordered.Count) { throw '缺少样本' }
    $middle=[int][Math]::Floor($ordered.Count/2)
    if ($ordered.Count%2) { return [double]$ordered[$middle] }
    return ([double]$ordered[$middle-1]+[double]$ordered[$middle])/2
}
function Stats($values) {
    $samples=@($values)
    return [ordered]@{ Samples=$samples.Count; Median=(Median $samples); Minimum=($samples|Measure-Object -Minimum).Minimum; Maximum=($samples|Measure-Object -Maximum).Maximum }
}
$summary=[ordered]@{}
$identities=@()
foreach($version in @('before','after')) {
    $live=@(Get-ChildItem -LiteralPath $ResultsDirectory -Filter "live-$version-*.json" | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
    $identities+=@($live.Measurement.Result.IdentityHash)
    $fixture=@(Get-ChildItem -LiteralPath $ResultsDirectory -Filter "fixture-$version-*.json" | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Measurements })
    $operations=[ordered]@{}
    foreach($group in ($fixture|Group-Object Name)) {
        $operations[$group.Name]=[ordered]@{ WallMs=(Stats $group.Group.WallMs); AllocatedBytesMedian=(Median $group.Group.AllocatedBytes) }
        if ($group.Name -eq 'Measure' -and @($group.Group.Result | Where-Object { $_.Bytes -ne 49922048 -or !$_.Complete }).Count) { throw '目录统计结果发生变化' }
        if ($group.Name -eq 'SumFiles' -and @($group.Group.Result | Where-Object { $_ -ne 49922048 }).Count) { throw 'SumFiles 结果发生变化' }
        if ($group.Name -eq 'Preview' -and @($group.Group.Result | Where-Object { $_.FreedBytes -ne 49922048 -or $_.DeletedCount -ne 24576 }).Count) { throw '预览结果发生变化' }
    }
    $stages=[ordered]@{}
    foreach($group in ($live.Diagnostics.Stages|Group-Object Name)) { $stages[$group.Name]=Stats $group.Group.ElapsedMs }
    $summary[$version]=[ordered]@{
        LiveWallMs=(Stats $live.Measurement.WallMs)
        LiveCpuMs=(Stats $live.Measurement.CpuMs)
        LiveAllocatedBytes=(Stats $live.Measurement.AllocatedBytes)
        Stages=$stages; Fixture=$operations
    }
}
$rust=@(Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'rust-*.json' | ForEach-Object {(Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json).Measurements})
if (@($rust|Where-Object { $_.Bytes -ne 49922048 -or $_.Files -ne 24576 -or !$_.Complete }).Count) { throw 'Rust 与固定目录结果不一致' }
$summary.RustWallMs=Stats $rust.WallMs
$summary.LiveIdentityConsistent=@($identities|Select-Object -Unique).Count -eq 1
$summary.LiveIdentityHash=@($identities|Select-Object -Unique)
$summary.Fixture=[ordered]@{Files=24576;Bytes=49922048;Rounds=3;SamplesPerOperation=21;Cache='warm filesystem; Measure/SumFiles JIT warmup; Preview/Projects first sample may include initialization; alternating implementation order'}
$summary.SourceRevision=(& git rev-parse HEAD)
$summary|ConvertTo-Json -Depth 12
