<#
.SYNOPSIS
  Runs the test suites and produces NuGet packages in ./artifacts.
  Native binaries for every RID staged in src/Rumble.Net/runtimes are included.
#>
param(
    [string] $Configuration = 'Release',
    [string[]] $Rids = @('win-x64'),
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

& (Join-Path $PSScriptRoot 'build-native.ps1') -Rids $Rids

if (-not $SkipTests) {
    Push-Location (Join-Path $root 'native')
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # cargo writes progress to stderr
    try { cargo test --workspace 2>&1 | ForEach-Object { "$_" }; if ($LASTEXITCODE -ne 0) { throw 'cargo test failed' } }
    finally { $ErrorActionPreference = $previous; Pop-Location }
    dotnet test --project (Join-Path $root 'tests/Rumble.Net.Tests/Rumble.Net.Tests.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
}

New-Item -ItemType Directory -Force $artifacts | Out-Null
foreach ($project in 'src/Rumble.Net/Rumble.Net.csproj', 'src/Rumble.Net.Bots/Rumble.Net.Bots.csproj') {
    dotnet pack (Join-Path $root $project) -c $Configuration -o $artifacts -p:RumbleSkipNativeBuild=true
    if ($LASTEXITCODE -ne 0) { throw "pack failed: $project" }
}

Get-ChildItem $artifacts -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Green }
