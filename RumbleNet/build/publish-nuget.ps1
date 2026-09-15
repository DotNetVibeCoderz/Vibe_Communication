<#
.SYNOPSIS
  Pushes packages from ./artifacts to nuget.org.

.DESCRIPTION
  The API key is read from a credentials file kept OUTSIDE the repository (never commit it).
  The file may contain just the key, or a line such as "NuGet: <key>" / "nuget=<key>".

.EXAMPLE
  ./build/publish-nuget.ps1 -CredentialsFile ..\PackageCredentials.txt
#>
param(
    [string] $CredentialsFile = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'PackageCredentials.txt'),
    [string] $Source = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path $CredentialsFile)) { throw "Credentials file not found: $CredentialsFile" }

$content = Get-Content $CredentialsFile -Raw
$match = [regex]::Match($content, '(?im)nuget[^\r\n]*?[:=]\s*(?<key>[A-Za-z0-9]{30,})')
$key = if ($match.Success) { $match.Groups['key'].Value } else { ([regex]::Match($content, '[A-Za-z0-9]{40,}')).Value }
if (-not $key) { throw 'No NuGet API key found in the credentials file.' }

$packages = Get-ChildItem (Join-Path $root 'artifacts') -Filter *.nupkg | Where-Object { $_.Name -notlike '*.symbols.nupkg' }
if (-not $packages) { throw 'No packages in ./artifacts. Run build/pack.ps1 first.' }

foreach ($p in $packages) {
    Write-Host "Pushing $($p.Name)" -ForegroundColor Cyan
    dotnet nuget push $p.FullName --api-key $key --source $Source --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "push failed: $($p.Name)" }
}
