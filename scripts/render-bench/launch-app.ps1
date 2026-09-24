<#
.SYNOPSIS
  Launch a checkout's Release Ntilde build with per-frame render metrics on.

.DESCRIPTION
  Sets NTILDE_RENDER_METRICS=1 and NTILDE_RENDER_METRICS_OUT so the real app
  (GPU backend) appends one JSON line per frame. In the window, run
  scripts/render-bench/workload.ps1, then close the window. Repeat for the other
  build, then compare with:
    python scripts/render-bench/compare-app.py <A.jsonl> <B.jsonl>
  FrameTimeMs is the CPU time TerminalDrawOperation spends issuing the frame;
  GPU execution time is not included.

.EXAMPLE
  scripts/render-bench/launch-app.ps1 -Checkout ..\baseline -Out $env:TEMP\app-skia3.jsonl
#>
param(
    [Parameter(Mandatory)] [string] $Checkout,
    [Parameter(Mandatory)] [string] $Out,
    [switch] $Build
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $Checkout).Path
if ($Build) {
    Push-Location $root
    try {
        & ./scripts/build.ps1 build -c Release src/Ntilde.App
        if ($LASTEXITCODE -ne 0) { throw "build failed in $root" }
    } finally { Pop-Location }
}
$exe = Join-Path $root 'src/Ntilde.App/bin/Release/net10.0/Ntilde.exe'
if (-not (Test-Path $exe)) { throw "no Release build at $exe (pass -Build)" }

Remove-Item $Out -ErrorAction SilentlyContinue
$psi = [Diagnostics.ProcessStartInfo]::new($exe)
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $root
$psi.Environment['NTILDE_RENDER_METRICS'] = '1'
$psi.Environment['NTILDE_RENDER_METRICS_OUT'] = (Join-Path (Resolve-Path (Split-Path -Parent $Out)).Path (Split-Path -Leaf $Out))
$p = [Diagnostics.Process]::Start($psi)
Write-Host "launched $exe (pid $($p.Id)); metrics -> $Out"
Write-Host "in the window run:  & '$PSScriptRoot\workload.ps1'"
