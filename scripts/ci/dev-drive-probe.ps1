# TEMPORARY - measures whether a Dev Drive would speed up the windows jobs. Not for merge.
#
# Times the same work on C: (the OS disk: the NuGet cache, TEMP and the SDK live there), D: (the
# workspace's disk) and a ReFS Dev Drive ($env:DEV_DRIVE). Each location gets its own copy of the
# NuGet packages and the source tree, then a `dotnet build` (twice, in opposite orders, so warm
# caches favour no location) and three script self-test files with TEMP on that drive.
$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
function Time([string]$what, [string]$where, [scriptblock]$work) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  & $work
  if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$what on $where exited $LASTEXITCODE" }
  $sw.Stop()
  $s = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  Write-Host ("{0,-28} {1,-3} {2,7} s" -f $what, $where, $s)
  $results.Add([pscustomobject]@{ What = $what; Where = $where; Seconds = $s })
}

$drives = [ordered]@{ C = 'C:\probe'; D = 'D:\probe'; E = (Join-Path $env:DEV_DRIVE 'probe') }
$packages = Join-Path $env:USERPROFILE '.nuget\packages'
$source = $env:GITHUB_WORKSPACE
$count = (Get-ChildItem $packages -Recurse -File | Measure-Object).Count
Write-Host "NuGet packages: $count files; source: $source"

# Copies: the package tree from C: (where the cache step restored it), and the source tree with its
# untracked native libraries but without .git.
foreach ($d in $drives.Keys) {
  $root = $drives[$d]
  New-Item -ItemType Directory -Force -Path $root, "$root\tmp" | Out-Null
  Time 'copy NuGet packages' $d { robocopy $packages "$root\nuget" /E /MT:8 /NFL /NDL /NJH /NJS /NP | Out-Null; if ($LASTEXITCODE -lt 8) { $global:LASTEXITCODE = 0 } }
  Time 'copy source tree' $d { robocopy $source "$root\src" /E /MT:8 /XD .git /NFL /NDL /NJH /NJS /NP | Out-Null; if ($LASTEXITCODE -lt 8) { $global:LASTEXITCODE = 0 } }
}

function Build([string]$d, [string]$tag = '') {
  $root = $drives[$d]
  $env:NUGET_PACKAGES = "$root\nuget"; $env:TEMP = "$root\tmp"; $env:TMP = "$root\tmp"
  Push-Location "$root\src"
  try {
    Get-ChildItem -Directory -Recurse -Include bin, obj -Path src, tests -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    Time "dotnet restore$tag" $d { dotnet restore -nodeReuse:false *> "$root\log.txt"; if ($LASTEXITCODE) { Get-Content "$root\log.txt" -Tail 60 | Write-Host } }
    Time "dotnet build$tag" $d { dotnet build -c Release --no-restore -nodeReuse:false -p:RunAnalyzers=false *> "$root\log.txt"; if ($LASTEXITCODE) { Get-Content "$root\log.txt" -Tail 60 | Write-Host } }
  } finally { Pop-Location }
}

$env:SKIP_RUST_NATIVE_BUILD = '1'
# Warm-up on the workspace's own drive, so the first measured build does not pay for loading
# the SDK and compiler from disk.
Build 'D' ' (warm-up)'
foreach ($d in @('C', 'D', 'E', 'E', 'D', 'C')) { Build $d }

$tests = 'scripts/terminfo/input-receipt.test.mjs', 'scripts/terminfo/line-edit-grading.test.mjs', 'scripts/terminfo/charsets-receipt.test.mjs'
foreach ($d in @('C', 'D', 'E', 'E', 'D', 'C')) {
  $root = $drives[$d]
  $env:TEMP = "$root\tmp"; $env:TMP = "$root\tmp"
  Push-Location "$root\src"
  try { Time 'node --test (3 files)' $d { node --test @tests *> "$root\log.txt"; if ($LASTEXITCODE) { Get-Content "$root\log.txt" -Tail 60 | Write-Host } } } finally { Pop-Location }
}

Write-Host ''
Write-Host 'Summary (seconds; builds and tests are the mean of their runs):'
$results | Group-Object What, Where | ForEach-Object {
  [pscustomobject]@{ What = $_.Group[0].What; Where = $_.Group[0].Where; Runs = $_.Count; Seconds = [math]::Round(($_.Group | Measure-Object Seconds -Average).Average, 1) }
} | Sort-Object What, Where | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
