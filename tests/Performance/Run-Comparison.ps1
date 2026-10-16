param(
    [Parameter(Mandatory)][string]$BaselineAssembly,
    [Parameter(Mandatory)][string]$CandidateAssembly,
    [Parameter(Mandatory)][string]$Fixture,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Live,
    [int]$Rounds = 3
)
$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Performance.dll'
if (Test-Path -LiteralPath $OutputDirectory) { throw '输出目录已存在，请使用新路径' }
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
$implementations = @{ before=$BaselineAssembly; after=$CandidateAssembly }
$rust = Join-Path $PSScriptRoot 'rust-scan\target\release\cachecleaner-scan-prototype.exe'
for ($round=0; $round -lt $Rounds; $round++) {
    $order = if ($round % 2 -eq 0) { @('before','after') } else { @('after','before') }
    foreach ($version in $order) {
        & dotnet $runner fixture $implementations[$version] (Join-Path $OutputDirectory "fixture-$version-$round.json") $Fixture
        if ($LASTEXITCODE -ne 0) { throw "固定数据基准失败 $version" }
        if ($Live) {
            & dotnet $runner live $implementations[$version] (Join-Path $OutputDirectory "live-$version-$round.json")
            if ($LASTEXITCODE -ne 0) { throw "只读扫描基准失败 $version" }
        }
    }
    if (Test-Path -LiteralPath $rust) {
        $rustResult = & $rust ([IO.Path]::GetFullPath($Fixture)) 7
        if ($LASTEXITCODE -ne 0) { throw 'Rust 原型失败' }
        [IO.File]::WriteAllText([IO.Path]::GetFullPath((Join-Path $OutputDirectory "rust-$round.json")), ($rustResult -join "`n"))
    }
}
