<#
.SYNOPSIS
  Builds the Rumble.Net Rust core for one or more targets and stages the binaries under
  src/Rumble.Net/runtimes/{rid}/native for packing.

.EXAMPLE
  ./build/build-native.ps1                       # host (win-x64)
  ./build/build-native.ps1 -Rids win-x64,win-arm64
#>
param(
    [string[]] $Rids = @('win-x64'),
    [switch] $NoMockServer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root 'native'
$runtimes = Join-Path $root 'src/Rumble.Net/runtimes'

$targets = @{
    'win-x64'     = @{ Triple = 'x86_64-pc-windows-msvc';     File = 'rumble_native.dll' }
    'win-arm64'   = @{ Triple = 'aarch64-pc-windows-msvc';    File = 'rumble_native.dll' }
    'linux-x64'   = @{ Triple = 'x86_64-unknown-linux-gnu';   File = 'librumble_native.so' }
    'linux-arm64' = @{ Triple = 'aarch64-unknown-linux-gnu';  File = 'librumble_native.so' }
    'osx-x64'     = @{ Triple = 'x86_64-apple-darwin';        File = 'librumble_native.dylib' }
    'osx-arm64'   = @{ Triple = 'aarch64-apple-darwin';       File = 'librumble_native.dylib' }
    'android-arm64' = @{ Triple = 'aarch64-linux-android';    File = 'librumble_native.so' }
    'ios-arm64'   = @{ Triple = 'aarch64-apple-ios';          File = 'librumble_native.a' }
}

foreach ($rid in $Rids) {
    $t = $targets[$rid]
    if (-not $t) { throw "Unknown RID '$rid'. Known: $($targets.Keys -join ', ')" }

    Write-Host "==> $rid ($($t.Triple))" -ForegroundColor Cyan
    rustup target add $t.Triple | Out-Null
    $features = if ($NoMockServer) { @('--no-default-features') } else { @() }
    Push-Location $native
    try {
        cargo build --release -p rumble-ffi --target $t.Triple @features
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed for $rid" }
    } finally { Pop-Location }

    $source = Join-Path $native "target/$($t.Triple)/release/$($t.File)"
    $dest = Join-Path $runtimes "$rid/native"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $source $dest -Force
    Write-Host "    staged $dest/$($t.File)"
}
