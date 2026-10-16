$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cargo = Get-Command cargo -ErrorAction SilentlyContinue
if ($cargo) { $cargoPath = $cargo.Source }
else {
    $env:RUSTUP_HOME = Join-Path $workspace '.toolchains\rustup'
    $env:CARGO_HOME = Join-Path $workspace '.toolchains\cargo'
    $cargoPath = Join-Path $env:CARGO_HOME 'bin\cargo.exe'
}
if (!(Test-Path -LiteralPath $cargoPath)) { throw '需要已安装的 Rust MSVC 工具链；也支持项目内 .toolchains 安装' }
& $cargoPath build --offline --release --manifest-path (Join-Path $PSScriptRoot 'rust-scan\Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Rust 原型构建失败' }
& $cargoPath test --offline --manifest-path (Join-Path $PSScriptRoot 'rust-scan\Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Rust 原型测试失败' }
