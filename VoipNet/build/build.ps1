<#
.SYNOPSIS
  Builds, tests and packs Voip.NET.

.EXAMPLE
  ./build/build.ps1                 # native engine + .NET solution + tests
  ./build/build.ps1 -Pack           # also create NuGet packages in artifacts/packages
  ./build/build.ps1 -SkipTests
  ./build/build.ps1 -Targets x86_64-pc-windows-msvc,aarch64-pc-windows-msvc -Pack
#>
param(
    [switch] $Pack,
    [switch] $SkipTests,
    [string] $Configuration = "Release",
    [string[]] $Targets = @()
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    Write-Host "==> Native engine (Rust)" -ForegroundColor Cyan
    Push-Location native
    cargo build --release
    if ($LASTEXITCODE) { throw "cargo build failed" }
    if (-not $SkipTests) {
        cargo test --lib
        if ($LASTEXITCODE) { throw "cargo test failed" }
    }

    # Extra runtime identifiers go to artifacts/native/<rid>/native/ and are packed into VoipNet.Core.
    $ridMap = @{
        "x86_64-pc-windows-msvc"     = @("win-x64", "voipnet_core.dll")
        "aarch64-pc-windows-msvc"    = @("win-arm64", "voipnet_core.dll")
        "x86_64-unknown-linux-gnu"   = @("linux-x64", "libvoipnet_core.so")
        "aarch64-unknown-linux-gnu"  = @("linux-arm64", "libvoipnet_core.so")
        "x86_64-apple-darwin"        = @("osx-x64", "libvoipnet_core.dylib")
        "aarch64-apple-darwin"       = @("osx-arm64", "libvoipnet_core.dylib")
    }
    foreach ($target in $Targets) {
        cargo build --release --target $target
        if ($LASTEXITCODE) { throw "cargo build for $target failed" }
        $rid, $file = $ridMap[$target]
        $dest = Join-Path $root "artifacts/native/$rid/native"
        New-Item -ItemType Directory -Force $dest | Out-Null
        Copy-Item "target/$target/release/$file" $dest -Force
    }
    Pop-Location

    Write-Host "==> .NET solution" -ForegroundColor Cyan
    dotnet build Voip.Net.slnx -c $Configuration
    if ($LASTEXITCODE) { throw "dotnet build failed" }

    if (-not $SkipTests) {
        Write-Host "==> .NET tests" -ForegroundColor Cyan
        dotnet run --project tests/VoipNet.Tests -c $Configuration --no-build
        if ($LASTEXITCODE) { throw "tests failed" }
    }

    if ($Pack) {
        Write-Host "==> NuGet packages" -ForegroundColor Cyan
        $out = Join-Path $root "artifacts/packages"
        foreach ($project in "src/VoipNet.Core", "src/VoipNet.Audio", "src/VoipNet.Video", "src/VoipNet.AI", "src/VoipNet.Enterprise", "tools/VoipNet.Cli") {
            dotnet pack $project -c $Configuration --no-build -o $out
            if ($LASTEXITCODE) { throw "pack $project failed" }
        }
        Get-ChildItem $out -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
    }
}
finally {
    Pop-Location
}
