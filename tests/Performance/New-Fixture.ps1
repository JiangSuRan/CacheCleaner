param([Parameter(Mandatory)][string]$Root, [int]$Directories = 128, [int]$FilesPerDirectory = 192)
$ErrorActionPreference = 'Stop'
$fixtureRoot = [IO.Path]::GetFullPath($Root)
if (Test-Path -LiteralPath $fixtureRoot) { throw '测试目录已存在，请使用新路径' }
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
[long]$bytes = 0
for ($directoryIndex=0; $directoryIndex -lt $Directories; $directoryIndex++) {
    $directory = Join-Path $fixtureRoot ("分组 {0}\level-{1}" -f ($directoryIndex % 8),[int][Math]::Floor($directoryIndex/8))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    for ($fileIndex=0; $fileIndex -lt $FilesPerDirectory; $fileIndex++) {
        $length = ($directoryIndex*17 + $fileIndex*31) % 4096 + 1
        [IO.File]::WriteAllBytes((Join-Path $directory ("cache-{0:D4}.bin" -f $fileIndex)),[byte[]]::new($length))
        $bytes += $length
    }
}
[pscustomobject]@{ Root=$fixtureRoot; Files=$Directories*$FilesPerDirectory; Bytes=$bytes }
