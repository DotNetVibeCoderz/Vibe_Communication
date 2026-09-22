<#
.SYNOPSIS
    Reads the benchmark results Criterion just wrote and compares them with the committed baseline.

.DESCRIPTION
    Criterion prints its numbers and forgets them, so a slow drift over months goes unnoticed. This
    script turns the results into a table (written to the GitHub step summary when running in CI) and
    flags anything markedly slower than `benchmarks/baseline.json`.

    Shared CI runners are noisy, so the threshold is deliberately loose: it catches an algorithm that
    got worse, not a machine that was busy. Refresh the baseline with -Update after a deliberate
    change, on a machine that was otherwise idle.

.PARAMETER Update
    Write the current results to the baseline file instead of comparing against it.

.PARAMETER Threshold
    How much slower than the baseline a benchmark may be before it is reported, as a ratio.

.PARAMETER Fail
    Exit with a non-zero code when a benchmark is over the threshold.
#>
[CmdletBinding()]
param(
    [switch]$Update,
    [double]$Threshold = 1.5,
    [switch]$Fail
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$criterion = Join-Path $root 'native/target/criterion'
$baselinePath = Join-Path $root 'benchmarks/baseline.json'

if (-not (Test-Path $criterion)) {
    Write-Error "No benchmark results in $criterion. Run: cd native; cargo bench --bench media"
}

# Criterion writes one estimates.json per benchmark, under <group>/<name>/new.
$results = [ordered]@{}
foreach ($file in Get-ChildItem -Path $criterion -Recurse -Filter 'estimates.json' | Where-Object { $_.Directory.Name -eq 'new' }) {
    $benchmark = Split-Path -Parent (Split-Path -Parent $file.FullName)
    $name = (Split-Path -Parent $benchmark | Split-Path -Leaf) + '/' + (Split-Path -Leaf $benchmark)
    $estimates = Get-Content $file.FullName -Raw | ConvertFrom-Json
    $results[$name] = [math]::Round($estimates.median.point_estimate, 1)
}

if ($results.Count -eq 0) {
    Write-Error "Found no estimates under $criterion."
}

if ($Update) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $baselinePath) | Out-Null
    $payload = [ordered]@{
        measured = (Get-Date).ToString('yyyy-MM-dd')
        machine  = "$([Environment]::OSVersion.Platform) $([Environment]::ProcessorCount) cores"
        note     = 'Median nanoseconds per iteration. Refresh with build/bench-report.ps1 -Update on an idle machine.'
        results  = $results
    }
    $payload | ConvertTo-Json -Depth 4 | Out-File $baselinePath -Encoding utf8
    Write-Host "Baseline written to $baselinePath ($($results.Count) benchmarks)."
    return
}

$baseline = $null
if (Test-Path $baselinePath) {
    $baseline = (Get-Content $baselinePath -Raw | ConvertFrom-Json).results
}

$lines = @('| Benchmark | Median | Baseline | Change |', '| --- | ---: | ---: | ---: |')
$regressions = @()
foreach ($name in $results.Keys) {
    $now = $results[$name]
    $was = if ($baseline -and $baseline.PSObject.Properties.Name -contains $name) { [double]$baseline.$name } else { $null }
    $change = '—'
    if ($null -ne $was -and $was -gt 0) {
        $ratio = $now / $was
        $change = '{0:+0.0%;-0.0%;0%}' -f ($ratio - 1)
        if ($ratio -gt $Threshold) {
            $regressions += "$name is $change slower than the baseline ($now ns vs $was ns)"
            $change = "⚠️ $change"
        }
    }

    $lines += '| {0} | {1:n0} ns | {2} | {3} |' -f $name, $now, $(if ($null -ne $was) { '{0:n0} ns' -f $was } else { '—' }), $change
}

$report = $lines -join "`n"
Write-Host $report
if ($env:GITHUB_STEP_SUMMARY) {
    "## Benchmarks`n`n$report`n" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

if ($regressions.Count -gt 0) {
    foreach ($regression in $regressions) {
        Write-Warning $regression
    }

    if ($Fail) {
        exit 1
    }
}
