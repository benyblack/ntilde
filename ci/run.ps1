# Local full-CI rehearsal. Mirrors the GitHub workflow closely enough to catch
# breakage before pushing, without needing a runner.
#
# Every dotnet invocation goes through scripts/build.ps1 rather than calling `dotnet`
# directly. Raw `dotnet build` spawns MSBuild worker nodes and a build server that
# inherit this script's stdout/stderr; when a parent captures those pipes the handles
# outlive the build and the reader never sees EOF, so the whole thing hangs. The wrapper
# encodes -nodeReuse:false and DOTNET_CLI_USE_MSBUILD_SERVER=0. See CLAUDE.md and
# Directory.Build.props. (#174)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$build = Join-Path $PSScriptRoot "..\scripts\build.ps1"

Write-Host "=== TOOLING ==="
# `restore`/`--info` do no compilation, so the wrapper deliberately leaves them alone;
# calling dotnet directly here is safe and keeps the output honest about the real SDK.
dotnet --info
rustc --version
cargo --version

Write-Host "=== CLEAN ==="
& $build clean

Write-Host "=== RESTORE ==="
& $build restore

# No -warnaserror here: Directory.Build.props sets TreatWarningsAsErrors, with
# AnalysisLevel latest-recommended and EnforceCodeStyleInBuild, for every project (#108).
# So this build runs the analyzers and fails on their warnings, as CI's Analyzers job does.
# (CI's Build job passes -p:RunAnalyzers=false to compile faster and leaves them to that job.)
Write-Host "=== BUILD RELEASE ==="
& $build build -c Release

# The same tests as ci.yml's Unit Tests jobs. CI splits App.Tests across three shard jobs and
# runs its Category=SdkBuild tests in the Build job, to save wall-clock time; here they run in
# the two lanes below. A bare `test` would run App.Tests in one process, and its PlatformBoot
# tests must not share a process with the rest: mixing them makes later headless tests time
# out and the testhost hang (see the PlatformBoot comment in ci.yml).
Write-Host "=== TEST ==="
$filter = "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng"
$tests = Join-Path $PSScriptRoot "..\tests"
foreach ($proj in "VT", "Rendering", "Architecture", "Platform", "McpServer", "Mux") {
    & $build test (Join-Path $tests "Ntilde.$proj.Tests") -c Release --no-build --filter $filter
}
& $build test (Join-Path $tests "Ntilde.App.Tests") -c Release --no-build --blame-hang-timeout 5m --filter "$filter&Lane!=PlatformBoot"
& $build test (Join-Path $tests "Ntilde.App.Tests") -c Release --no-build --blame-hang-timeout 5m --filter "Lane=PlatformBoot&Category!=GoldenSharedPng"

Write-Host "=== REPLAY TESTS ==="
& $build test -c Release --filter Category=Replay

Write-Host "=== AOT PUBLISH ==="
& $build publish src/Ntilde.App/Ntilde.App.csproj `
    -c Release `
    -r win-x64 `
    -p:PublishAot=true `
    -p:StripSymbols=true

# Enforced as of #216: the 674 pre-existing whitespace violations that made this
# report-only have been swept, and `.gitattributes` now pins line endings so the check
# reaches the same verdict on Windows and Linux (it did not before - `.editorconfig`
# wants CRLF while git stores LF, so the answer depended on core.autocrlf).
#
# Scoped to `whitespace` deliberately. A bare `dotnet format` also runs the style and
# analyzer passes, whose diagnostics are #108's territory; mixing them in here would
# make this gate fail for reasons that have nothing to do with formatting.
Write-Host "=== FORMAT CHECK ==="
dotnet format whitespace --verify-no-changes
if ($LASTEXITCODE -ne 0) {
    Write-Host "FORMAT CHECK FAILED: run 'dotnet format whitespace' and commit the result."
    exit 1
}

Write-Host "CI SUCCESS"
