<#
.SYNOPSIS
  Interleaved A/B run of the opt-in headless render benchmark
  (tests/Ntilde.App.Tests/Performance/RenderBenchmark.cs) across two checkouts.

.DESCRIPTION
  Builds App.Tests (Release) in both checkouts, then runs the RenderBench
  category -Rounds times per side, alternating which side goes first each round
  so thermal/drift effects hit both equally. Results append to -Out as JSONL;
  compare with: python scripts/render-bench/compare-headless.py <Out>

  Both checkouts must contain the same RenderBenchmark.cs. Keep the machine
  idle while it runs; treat deltas under ~5% (or with overlapping run ranges)
  as noise.

.EXAMPLE
  scripts/render-bench/ab-headless.ps1 -A ..\baseline -ALabel skia3 -B . -BLabel skia4
#>
param(
    [Parameter(Mandatory)] [string] $A,
    [Parameter(Mandatory)] [string] $B,
    [string] $ALabel = 'A',
    [string] $BLabel = 'B',
    [int] $Rounds = 6,
    [string] $Out = (Join-Path ([IO.Path]::GetTempPath()) 'ntilde-render-bench.jsonl')
)

$ErrorActionPreference = 'Stop'
$sides = @(
    @{ Path = (Resolve-Path $A).Path; Label = $ALabel },
    @{ Path = (Resolve-Path $B).Path; Label = $BLabel }
)
$logDir = Join-Path ([IO.Path]::GetTempPath()) 'ntilde-render-bench-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
Remove-Item $Out -ErrorAction SilentlyContinue

foreach ($s in $sides) {
    Push-Location $s.Path
    try {
        & ./scripts/build.ps1 build -c Release tests/Ntilde.App.Tests *> (Join-Path $logDir "build-$($s.Label).log")
        if ($LASTEXITCODE -ne 0) { throw "build failed for $($s.Label) ($($s.Path)); see $logDir" }
    } finally { Pop-Location }
}

$env:NTILDE_RENDER_BENCH = '1'
$env:NTILDE_RENDER_BENCH_OUT = $Out
try {
    for ($i = 1; $i -le $Rounds; $i++) {
        $order = if ($i % 2) { $sides } else { $sides[1], $sides[0] }
        foreach ($s in $order) {
            $env:NTILDE_RENDER_BENCH_LABEL = $s.Label
            Push-Location $s.Path
            try {
                & ./scripts/build.ps1 test -c Release --no-build tests/Ntilde.App.Tests --filter "Category=RenderBench" *> (Join-Path $logDir "run-$i-$($s.Label).log")
                Write-Host "round $i $($s.Label) exit=$LASTEXITCODE"
            } finally { Pop-Location }
        }
    }
} finally {
    Remove-Item Env:NTILDE_RENDER_BENCH, Env:NTILDE_RENDER_BENCH_OUT, Env:NTILDE_RENDER_BENCH_LABEL -ErrorAction SilentlyContinue
}
Write-Host "results: $Out"
