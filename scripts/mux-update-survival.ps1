#Requires -Version 7.2
<#
.SYNOPSIS
    Update-survival evidence for the local multiplexer (Phase 5 Task 24, spec R9/R10): a real Velopack update, applied
    while the daemon runs, in a sandboxed install, with the daemon's shells intact afterwards.

.DESCRIPTION
    Windows only. Everything happens under -Sandbox:
      1. AOT-publishes src/Ntilde.App as <VersionPrefix>.1 and .2, as release.yml's win-x64 lane does, with ntilde.com
         beside Ntilde.exe. -SkipBuild reuses publish\<version>\ from an earlier run.
      2. Packs both with vpk 1.2.0 (installed into <Sandbox>\tools, not globally) as packId NtildeSurvival, never
         NtildeApp, into feed\. The release notes carry the multiplexer protocol marker, and no shortcuts are made.
      3. Installs .1 silently into install\.
      4. Starts the installed GUI with NTILDE_APPDATA_ROOT=<Sandbox>\data and NTILDE_UPDATE_SOURCE_DIR=<Sandbox>\feed,
         and a settings file with SessionPersistence=KeepOnClose. The GUI starts the daemon from its copy under
         data\bin\<version>\.
      5. Starts two sessions with `ntilde mux spawn-for-test`. Each runs a heartbeat: a line every second into a file.
      6. Lists them with `ntilde mux ls`.
      7. Lets the GUI's own update check stage .2 from the local feed, closes the GUI (its sessions are kept), and
         starts it again. Velopack's startup auto-apply applies .2 and restarts the app as .2: a daemon running from
         its copy outside the install root does not veto the startup apply.
      8. Checks the result:
         - current\sq.version is .2;
         - the daemon's pid is unchanged, and its image is under data\bin\<.1>\;
         - both heartbeats kept advancing across the apply;
         - `ntilde mux ls` lists the same session ids;
         - whether the new GUI raised the "multiplexer is from the previous build" notice (Task 23).
      9. Prints the no-overlap path's manual steps. That path needs the in-app apply, which this script cannot drive.
     10. Uninstalls with the daemon and the new GUI running. Reports the order of Velopack's kill pass and our uninstall
         hook, and whether the hook stopped the daemon.

    Once the sandbox is confirmed as this script's own, a finally block always cleans up: it stops what the sandbox
    started, uninstalls, and puts the user PATH, the Uninstall key and shortcuts back as they were. It stops processes
    by pid only, and only those whose image is under the sandbox. A refusal before that (a sandbox that is not the
    script's, or one an earlier run left installed) exits without cleaning anything.

    SAFETY. This script never touches %LOCALAPPDATA%\NtildeApp or %LOCALAPPDATA%\ntilde, nor the user's own ntilde
    processes, daemon or pipes, nor the Windows Credential Manager.
    - The sandbox must be the script's own: a new or empty folder, which it marks with .ntilde-survival-sandbox on first
      use, or a folder carrying that marker. It must lie under %TEMP% unless -AllowAnyLocation is given, and it may
      never be, or contain, the user profile, %LOCALAPPDATA%, %APPDATA%, %TEMP%, Windows, Program Files or the
      repository, nor overlap the real install or data folder. Recursive deletes happen only inside a marked sandbox.
    - Every process it starts gets NTILDE_APPDATA_ROOT, and on Windows the processes Velopack starts inherit it: its
      install, update and uninstall hooks, and the restart after an apply (measured with Velopack 1.2.0).
    - The builds are packed as NtildeSurvival, the only install for which the app honours NTILDE_UPDATE_SOURCE_DIR.
    - The sandbox GUI's settings turn off the two things that are global per user and not keyed by NTILDE_APPDATA_ROOT:
      the agent host's pipe (ntilde-agent-<user>) and the quake-mode global hotkey. The multiplexer's pipe name is
      derived from the root.

.PARAMETER Sandbox
    The folder everything goes into. It must not contain whitespace (the heartbeat command line), must be new, empty or
    already marked as this script's sandbox, and must not hold an install from an earlier run (clean that up with
    -CleanupOnly).

.PARAMETER AllowAnyLocation
    Accept a sandbox outside %TEMP%. The other location rules still apply.

.PARAMETER VersionPrefix
    The builds are <VersionPrefix>.1 and <VersionPrefix>.2 (and <VersionPrefix>.3 with -LeaveRunning).

.PARAMETER SkipBuild
    Reuse <Sandbox>\publish\<version>\ from an earlier run instead of publishing again (each AOT publish takes about
    10 minutes).

.PARAMETER BuildOnly
    Publish and stop: no pack, no install.

.PARAMETER LeaveRunning
    For the no-overlap path's manual steps. After step 8, pack <VersionPrefix>.3 (the .2 build, with the marker 3-3),
    then stop without uninstalling, leaving the .2 GUI and the daemon running. Clean up afterwards with -CleanupOnly.

.PARAMETER CleanupOnly
    Only clean up a sandbox an earlier run left: stop its daemon, uninstall, and restore what the install changed.

.EXAMPLE
    scripts/mux-update-survival.ps1 -Sandbox C:\Temp\ntilde-survival
#>
[CmdletBinding()]
param(
    [string] $Sandbox = (Join-Path ([IO.Path]::GetTempPath()) 'ntilde-update-survival'),
    [string] $VersionPrefix = '0.12.0-survival',
    [switch] $SkipBuild,
    [switch] $BuildOnly,
    [switch] $LeaveRunning,
    [switch] $CleanupOnly,
    [switch] $AllowAnyLocation
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) { throw 'mux-update-survival.ps1 runs on Windows; scripts/mux-update-survival.sh is the macOS and Linux run.' }

# ---- names and folders ---------------------------------------------------------------------------------------------

$PackId = 'NtildeSurvival'   # never NtildeApp: that is the real install's identity, and its folder under %LOCALAPPDATA%
$PackTitle = 'Ntilde Survival'
$V1 = "$VersionPrefix.1"
$V2 = "$VersionPrefix.2"
$V3 = "$VersionPrefix.3"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$S = [IO.Path]::GetFullPath($Sandbox).TrimEnd('\')
$SandboxPrefix = $S + '\'
$Publish = Join-Path $S 'publish'
$Feed = Join-Path $S 'feed'
$SetupDir = Join-Path $S "setup-$V1"
$Install = Join-Path $S 'install'
$CurrentDir = Join-Path $Install 'current'
$CurrentExe = Join-Path $CurrentDir 'Ntilde.exe'
$UpdateExe = Join-Path $Install 'Update.exe'
$Data = Join-Path $S 'data'
$Tools = Join-Path $S 'tools'
$Shells = Join-Path $S 'shells'
$Evidence = Join-Path $S 'evidence'
$RunLog = Join-Path $Evidence ('survival-run-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))
$SnapshotFile = Join-Path $Evidence 'side-effects-before.json'

$RealInstall = Join-Path $env:LOCALAPPDATA 'NtildeApp'
$RealData = Join-Path $env:LOCALAPPDATA 'ntilde'
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$PackId"
$VelopackAppLog = Join-Path $env:LOCALAPPDATA "velopack\velopack_$PackId.log"
$VelopackSharedLog = Join-Path $env:LOCALAPPDATA 'velopack\velopack.log'
$VelopackTemp = Join-Path $env:TEMP "velopack_$PackId"

$Marker = Join-Path $S '.ntilde-survival-sandbox'

# Whether $Child is $Parent or lies under it.
function Test-Inside([string] $Child, [string] $Parent) {
    return ($Child.TrimEnd('\') + '\').StartsWith($Parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

# ---- the sandbox must be this script's own (ruling R4) -------------------------------------------------------------
# Everything here runs before anything is written and before the cleanup is armed: a refusal changes nothing.

# Never in or around the real install or data; and never a folder that is, or holds, the profile, the system, the
# programs or the repository, since every process whose image is under the sandbox may be stopped.
foreach ($real in @($RealInstall, $RealData)) {
    if ((Test-Inside $S $real) -or (Test-Inside $real $S)) { throw "The sandbox $S overlaps $real, which this script must never touch." }
}
$tempRoots = @([IO.Path]::GetTempPath(), $env:TEMP) | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
foreach ($wide in @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $RepoRoot) + $tempRoots) {
    if ($wide -and (Test-Inside $wide $S)) { throw "The sandbox $S is, or contains, $wide. Choose a folder of its own (for example under %TEMP%)." }
}
# An allow-list of places, not only a deny-list: under %TEMP%, unless the caller says otherwise.
if (-not $AllowAnyLocation -and -not @($tempRoots | Where-Object { (Test-Inside $S $_) -and $S -ne $_ }).Count) {
    throw "The sandbox $S is not under %TEMP% ($($tempRoots -join ', ')). Choose a folder there, or pass -AllowAnyLocation."
}
if ($S -match '\s') { throw "The sandbox path must not contain whitespace (it is in the heartbeat sessions' command line): $S" }

# Ours: a new or empty folder (marked now), or one carrying the marker. Anything else is someone's folder.
if (Test-Path -LiteralPath $S -PathType Leaf) { throw "The sandbox $S is a file." }
$sandboxIsNew = -not (Test-Path -LiteralPath $S) -or -not @(Get-ChildItem -LiteralPath $S -Force).Count
if (-not $sandboxIsNew -and -not (Test-Path -LiteralPath $Marker -PathType Leaf)) {
    throw "$S is not empty and does not carry $([IO.Path]::GetFileName($Marker)): it is not this script's sandbox. Choose a new or empty folder."
}
if ($sandboxIsNew -and $CleanupOnly) {
    [Console]::Out.WriteLine("$S is not a sandbox of this script's yet: there is nothing to clean up.")
    exit 0
}
if ($sandboxIsNew) {
    New-Item -ItemType Directory -Force -Path $S | Out-Null
    [IO.File]::WriteAllText($Marker, "A sandbox of scripts/mux-update-survival.ps1. Anything in this folder may be deleted by it.`r`n")
}

# Recursive deletes of the script's fixed-name folders: only inside a marked sandbox, and never the sandbox itself.
function Remove-SandboxFolder([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $Marker -PathType Leaf)) { throw "Refusing to delete $full`: $S is not a marked sandbox." }
    if (-not (Test-Inside $full $S) -or $full -eq $S) { throw "Refusing to delete $full`: it is not inside the sandbox $S." }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

New-Item -ItemType Directory -Force -Path $Evidence | Out-Null

# ---- output ----------------------------------------------------------------------------------------------------------

function Say([string] $Text = '') {
    $line = '[{0:HH:mm:ss.fff}] {1}' -f (Get-Date), $Text
    [Console]::Out.WriteLine($line)
    Add-Content -LiteralPath $RunLog -Value $line -Encoding utf8
}

function Section([string] $Title) {
    Say ''
    Say "=== $Title ==="
}

$Checks = [Collections.Generic.List[object]]::new()
function Check([string] $Name, [bool] $Ok, [string] $Detail = '') {
    $Checks.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    $verdict = if ($Ok) { 'PASS' } else { 'FAIL' }
    if ($Detail) { Say "$verdict  $Name - $Detail" } else { Say "$verdict  $Name" }
}

# ---- processes -------------------------------------------------------------------------------------------------------

function Test-UnderSandbox([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try { return [IO.Path]::GetFullPath($Path).StartsWith($SandboxPrefix, [StringComparison]::OrdinalIgnoreCase) } catch { return $false }
}

function Get-ProcInfo([int] $Id) {
    return Get-CimInstance Win32_Process -Filter "ProcessId=$Id" -ErrorAction SilentlyContinue
}

function Get-SandboxProcesses {
    return @(Get-CimInstance Win32_Process | Where-Object { Test-UnderSandbox $_.ExecutablePath })
}

# By pid, and only a process whose image is under the sandbox.
function Stop-SandboxProcess([int] $Id, [string] $Why) {
    $proc = Get-ProcInfo $Id
    if (-not $proc) { return }
    if (-not (Test-UnderSandbox $proc.ExecutablePath)) {
        Say "REFUSED to stop pid $Id ($($proc.ExecutablePath)): its image is not under the sandbox"
        return
    }
    Say "stopping pid $Id ($($proc.ExecutablePath)): $Why"
    Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue
}

function Test-Alive([int] $Id) {
    return $null -ne (Get-Process -Id $Id -ErrorAction SilentlyContinue)
}

# Runs a console-style command and captures its output; ProcessStartInfo.ArgumentList quotes each argument.
function Invoke-Exe([string] $File, [string[]] $Arguments, [int] $TimeoutSeconds = 60) {
    $psi = [Diagnostics.ProcessStartInfo]::new($File)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $S
    $proc = [Diagnostics.Process]::Start($psi)
    $out = $proc.StandardOutput.ReadToEndAsync()
    $err = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-SandboxProcess $proc.Id "it did not finish within $TimeoutSeconds s"
        throw "$File $($Arguments -join ' ') did not finish within $TimeoutSeconds s"
    }
    $proc.WaitForExit()
    # A child that inherited the pipes (Update.exe's scheduled rmdir) can hold them a little longer.
    [void] [Threading.Tasks.Task]::WaitAll(@($out, $err), 15000)
    $stdout = if ($out.IsCompleted) { $out.Result } else { '' }
    $stderr = if ($err.IsCompleted) { $err.Result } else { '' }
    return [pscustomobject]@{ Code = $proc.ExitCode; Out = $stdout; Err = $stderr }
}

# The GUI, started as Velopack starts it: no arguments, working directory current\.
function Start-Gui {
    $psi = [Diagnostics.ProcessStartInfo]::new($CurrentExe)
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $CurrentDir
    return [Diagnostics.Process]::Start($psi)
}

function Get-GuiProcesses {
    return @(Get-CimInstance Win32_Process -Filter "Name='Ntilde.exe'" | Where-Object {
        $_.ExecutablePath -and ($_.ExecutablePath -ieq $CurrentExe) -and ($_.CommandLine -notmatch '\s(mux|--veloapp-)')
    })
}

function Get-ImageVersion([int] $Id) {
    try { return (Get-Process -Id $Id -ErrorAction Stop).MainModule.FileVersionInfo.ProductVersion } catch { return $null }
}

# ---- files -----------------------------------------------------------------------------------------------------------

# Read with every share flag, as the app reads its descriptor, so a writer's atomic replace never fails on us.
function Read-SharedText([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $fs = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try { return [IO.StreamReader]::new($fs).ReadToEnd() } finally { $fs.Dispose() }
}

function Read-Descriptor {
    $text = Read-SharedText (Join-Path $Data 'mux\mux-endpoint.json')
    if (-not $text) { return $null }
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Get-SqVersion {
    $sq = Join-Path $CurrentDir 'sq.version'
    if (-not (Test-Path -LiteralPath $sq)) { return $null }
    try { return ([xml](Read-SharedText $sq)).package.metadata.version } catch { return $null }
}

function Get-FileLength([string] $Path) {
    $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($item) { return $item.Length } else { return 0 }
}

# What a file gained since $Offset bytes.
function Read-Since([string] $Path, [long] $Offset) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $fs = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($Offset -ge $fs.Length) { return '' }
        [void] $fs.Seek($Offset, [IO.SeekOrigin]::Begin)
        return [IO.StreamReader]::new($fs, [Text.UTF8Encoding]::new($false)).ReadToEnd()
    }
    finally { $fs.Dispose() }
}

function Wait-Until([scriptblock] $Condition, [int] $TimeoutSeconds, [int] $PollMilliseconds = 500) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    return $null
}

function Find-LogLine([string] $Path, [string] $Pattern) {
    $text = Read-SharedText $Path
    if (-not $text) { return $null }
    return ($text -split "`r?`n" | Where-Object { $_ -match $Pattern } | Select-Object -First 1)
}

# ---- heartbeats ------------------------------------------------------------------------------------------------------

# A heartbeat line is "<n> <time>", the time as cmd's %TIME% writes it (" 9:03:05.12", or with a comma in some locales).
function Read-Beats([string] $File) {
    $text = Read-SharedText $File
    $beats = [Collections.Generic.List[datetime]]::new()
    if (-not $text) { return , $beats }
    $day = (Get-Item -LiteralPath $File).CreationTime.Date
    $previous = $null
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -notmatch '^\s*\d+\s+(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,3})') { continue }
        $t = $day.AddHours([int]$Matches[1]).AddMinutes([int]$Matches[2]).AddSeconds([int]$Matches[3] + [double]('0.' + $Matches[4]))
        if ($null -ne $previous -and $t -lt $previous.AddHours(-12)) { $day = $day.AddDays(1); $t = $t.AddDays(1) }   # past midnight
        $beats.Add($t)
        $previous = $t
    }
    return , $beats
}

# The longest silence between consecutive beats overlapping [From, To]; and whether beats exist before From and after To.
function Measure-Beats($Beats, [datetime] $From, [datetime] $To) {
    $max = [TimeSpan]::Zero
    $before = 0
    $after = 0
    for ($i = 0; $i -lt $Beats.Count; $i++) {
        if ($Beats[$i] -lt $From) { $before++ }
        if ($Beats[$i] -gt $To) { $after++ }
        if ($i -eq 0) { continue }
        if ($Beats[$i] -lt $From -or $Beats[$i - 1] -gt $To) { continue }
        $gap = $Beats[$i] - $Beats[$i - 1]
        if ($gap -gt $max) { $max = $gap }
    }
    return [pscustomobject]@{ MaxGap = $max; Before = $before; After = $after; Count = $Beats.Count }
}

# ---- side effects on the user profile --------------------------------------------------------------------------------

function Read-UserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
    try {
        if (-not $key) { return $null }
        return $key.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    finally { if ($key) { $key.Dispose() } }
}

# The user PATH's value kind (ExpandString as Windows writes it, or String), as a name; null when there is no value.
function Read-UserPathKind {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
    try {
        if (-not $key -or $null -eq $key.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)) { return $null }
        return $key.GetValueKind('Path').ToString()
    }
    finally { if ($key) { $key.Dispose() } }
}

function Get-ShortcutFolders {
    return @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('Programs'),
        [Environment]::GetFolderPath('Startup'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
}

function Get-NtildeShortcuts {
    return @(Get-ShortcutFolders | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Filter '*.lnk' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Ntilde*' } | ForEach-Object { $_.FullName }
    } | Sort-Object)
}

function Get-SideEffects {
    return [ordered]@{
        UserPath = Read-UserPath
        UserPathKind = Read-UserPathKind
        UserPathHasSandbox = Test-PathNamesSandbox (Read-UserPath)
        UninstallKey = Test-Path $UninstallKey
        Shortcuts = @(Get-NtildeShortcuts)
        VelopackAppLog = Test-Path -LiteralPath $VelopackAppLog
        VelopackTemp = Test-Path -LiteralPath $VelopackTemp
        VelopackSharedLogBytes = Get-FileLength $VelopackSharedLog
        DefaultInstallFolder = Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA $PackId)
    }
}

# A PATH entry as UserPathRegistration compares it: trimmed of blanks and quotes, expanded, no trailing separator.
function ConvertTo-PathEntryKey([string] $Entry) {
    $p = [Environment]::ExpandEnvironmentVariables($Entry.Trim().Trim('"').Trim())
    while ($p.Length -gt 3 -and ($p.EndsWith('\') -or $p.EndsWith('/'))) { $p = $p.Substring(0, $p.Length - 1) }
    return $p.ToUpperInvariant()
}

function Test-PathNamesSandbox([string] $Value) {
    if (-not $Value) { return $false }
    $mine = ConvertTo-PathEntryKey $CurrentDir
    return [bool] (@($Value -split ';' | Where-Object { $_.Trim() -and (ConvertTo-PathEntryKey $_) -eq $mine }).Count)
}

function Format-SideEffects($Snapshot) {
    Say "  user PATH names the sandbox install: $($Snapshot.UserPathHasSandbox)"
    Say "  user PATH: $($Snapshot.UserPath.Length) characters, $($Snapshot.UserPathKind)"
    Say "  HKCU Uninstall\$PackId exists: $($Snapshot.UninstallKey)"
    Say "  *Ntilde* shortcuts (Desktop, Start Menu, Startup, pinned): $(if ($Snapshot.Shortcuts.Count) { $Snapshot.Shortcuts -join '; ' } else { 'none' })"
    Say "  $VelopackAppLog exists: $($Snapshot.VelopackAppLog)"
    Say "  $VelopackTemp exists: $($Snapshot.VelopackTemp)"
    Say "  shared $VelopackSharedLog`: $($Snapshot.VelopackSharedLogBytes) bytes"
    Say "  $(Join-Path $env:LOCALAPPDATA $PackId) exists: $($Snapshot.DefaultInstallFolder)"
}

# ---- build and pack --------------------------------------------------------------------------------------------------

function Invoke-Publish([string] $Version) {
    $out = Join-Path $Publish $Version
    Remove-SandboxFolder $out
    $log = Join-Path $Evidence "publish-$Version.log"
    Say "AOT-publishing src/Ntilde.App as $Version into $out (about 10 minutes; log $log)"
    & (Join-Path $RepoRoot 'scripts\build.ps1') publish (Join-Path $RepoRoot 'src\Ntilde.App\Ntilde.App.csproj') -c Release -r win-x64 --self-contained true "-p:PublishAot=true;SkipCliShim=true;Version=$Version;InformationalVersion=$Version" -o $out *> $log
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $out 'Ntilde.exe'))) { throw "The AOT publish of $Version failed (exit $LASTEXITCODE); see $log" }
    $trim = @(Select-String -LiteralPath $log -Pattern 'warning IL(2026|3050)')
    Say "  Ntilde.exe: $((Get-Item (Join-Path $out 'Ntilde.exe')).Length) bytes; IL2026/IL3050 warnings in the log: $($trim.Count)"

    $launcherOut = Join-Path $S "launcher\$Version"
    $launcherLog = Join-Path $Evidence "publish-launcher-$Version.log"
    Say "Publishing ntilde.com (src/Ntilde.Launcher) as $Version (log $launcherLog)"
    & (Join-Path $RepoRoot 'scripts\build.ps1') publish (Join-Path $RepoRoot 'src\Ntilde.Launcher\Ntilde.Launcher.csproj') -c Release -r win-x64 "-p:Version=$Version;InformationalVersion=$Version" -o $launcherOut *> $launcherLog
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $launcherOut 'Ntilde.Launcher.exe'))) { throw "The launcher publish of $Version failed; see $launcherLog" }
    Copy-Item -LiteralPath (Join-Path $launcherOut 'Ntilde.Launcher.exe') -Destination (Join-Path $out 'ntilde.com') -Force
    Say "  ntilde.com is in the bundle beside Ntilde.exe"
}

function Get-Vpk {
    $vpk = Join-Path $Tools 'vpk.exe'
    if (-not (Test-Path -LiteralPath $vpk)) {
        Say "Installing vpk 1.2.0 into $Tools (a tool path, not a global tool), as release.yml pins it"
        & dotnet tool install vpk --version 1.2.0 --tool-path $Tools *> (Join-Path $Evidence 'vpk-install.log')
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool install vpk failed; see $(Join-Path $Evidence 'vpk-install.log')" }
    }
    $banner = (& $vpk --help 2>&1 | Select-Object -First 3) -join ' '
    if ($banner -notmatch 'Velopack CLI 1\.2\.0') { throw "$vpk is not vpk 1.2.0: $banner" }
    return $vpk
}

# release.yml reads the range with scripts/ci/mux-protocol-range.sh; so does this, through Git for Windows' bash
# (the `bash` on PATH can be WSL's).
function Get-ProtocolRange {
    $git = (Get-Command git -ErrorAction Stop).Source
    $bash = @((Join-Path (Split-Path (Split-Path $git)) 'bin\bash.exe'), (Join-Path (Split-Path (Split-Path (Split-Path $git))) 'bin\bash.exe')) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $bash) { throw "Git for Windows' bash.exe was not found near $git" }
    $range = & $bash (Join-Path $RepoRoot 'scripts\ci\mux-protocol-range.sh').Replace('\', '/')
    if ($LASTEXITCODE -ne 0 -or "$range" -notmatch '^\d+-\d+$') { throw "scripts/ci/mux-protocol-range.sh failed: $range" }
    return "$range".Trim()
}

function Write-Notes([string] $Version, [string] $Range) {
    $notes = Join-Path $S "release-notes-$Version.md"
    [IO.File]::WriteAllText($notes, "<!-- ntilde-mux-protocol: $Range -->`n", [Text.UTF8Encoding]::new($false))
    return $notes
}

function Invoke-Pack([string] $Vpk, [string] $Version, [string] $PackDir, [string] $Notes) {
    $log = Join-Path $Evidence "vpk-pack-$Version.log"
    $packArgs = @(
        'pack', '--packId', $PackId, '--packVersion', $Version, '--packDir', $PackDir, '--mainExe', 'Ntilde.exe',
        '--packTitle', $PackTitle, '--packAuthors', 'benyblack', '--icon', (Join-Path $RepoRoot 'src\Ntilde.App\Assets\ntilde_icon.ico'),
        '--releaseNotes', $Notes, '--outputDir', $Feed, '--noPortable', '--shortcuts', 'None')
    Say "vpk $($packArgs -join ' ')"
    & $Vpk @packArgs *> $log
    if ($LASTEXITCODE -ne 0) { throw "vpk pack $Version failed; see $log" }
    if (-not (Test-Path -LiteralPath (Join-Path $Feed "$PackId-$Version-full.nupkg"))) { throw "vpk pack produced no $PackId-$Version-full.nupkg; see $log" }
}

# ---- the sandbox's own profile -----------------------------------------------------------------------------------------

function Write-SandboxProfile([string] $TabShell) {
    New-Item -ItemType Directory -Force -Path $Data | Out-Null
    $profileId = [guid]::NewGuid().ToString('D')
    $settings = [ordered]@{
        SessionPersistence = 'KeepOnClose'
        AutomaticUpdateChecks = $true
        # Global per user, not keyed by NTILDE_APPDATA_ROOT: the agent host's pipe and the global hotkey.
        AgentAccessObserveEnabled = $false
        QuakeModeEnabled = $false
        # The tab's shell is a copy of cmd.exe under the sandbox, so every process the daemon hosts has its image here.
        Profiles = @([ordered]@{ Id = $profileId; Name = 'Sandbox shell'; Command = $TabShell; Arguments = '' })
        DefaultProfileId = $profileId
    }
    [IO.File]::WriteAllText((Join-Path $Data 'settings.json'), ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    # The first-close dialog's remembered answer (R1): keep the shells, without asking.
    [IO.File]::WriteAllText((Join-Path $Data 'mux-close-choice'), 'keep', [Text.UTF8Encoding]::new($false))
}

function Write-Heartbeat {
    New-Item -ItemType Directory -Force -Path $Shells | Out-Null
    # Copies of cmd.exe under the sandbox, so every process the daemon hosts has its image here. Not named *cmd.exe:
    # RustPtySession prefixes "/k chcp 65001" to the arguments of any command whose name ends so, which would keep the
    # heartbeat's cmd at a prompt instead of running /c.
    $cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
    Copy-Item -LiteralPath $cmd -Destination (Join-Path $Shells 'hb-shell.exe') -Force
    Copy-Item -LiteralPath $cmd -Destination (Join-Path $Shells 'tab-shell.exe') -Force
    $lines = @(
        '@echo off',
        'rem A heartbeat: one line a second, "<n> <time>", into the file named by %1. Started by ntilde mux spawn-for-test.',
        'setlocal EnableDelayedExpansion',
        'set /a n=0',
        ':beat',
        'set /a n+=1',
        '>>"%~1" echo !n! !time!',
        'ping -n 2 -w 1000 127.0.0.1 >nul',
        'goto beat')
    [IO.File]::WriteAllText((Join-Path $Shells 'heartbeat.cmd'), (($lines -join "`r`n") + "`r`n"), [Text.Encoding]::ASCII)
}

function Get-MuxSessions {
    $r = Invoke-Exe $CurrentExe @('mux', 'ls', '--json') 60
    if ($r.Code -ne 0) { throw "ntilde mux ls --json exited $($r.Code): $($r.Err.Trim())" }
    return @(($r.Out | ConvertFrom-Json).sessions)
}

# ---- the no-overlap path, by hand -------------------------------------------------------------------------------------

function Show-NoOverlapSteps {
    Section '9. No-overlap path: NOT RUN by this script (manual steps for the maintainer)'
    Say 'Only the in-app apply reads the staged release notes (MuxUpdateCompatibility.KeepsDaemon), so only it takes the'
    Say 'confirm-and-shutdown path for a build whose marker shares no protocol version with the daemon. Velopack''s startup'
    Say 'auto-apply never reads the marker. The in-app apply is a click (the update toast''s Restart button, or the palette''s'
    Say '"Restart to update"), which this script does not automate, so this path was not run. To run it by hand, on Windows'
    Say 'or Linux only: the in-app apply restarts the app itself, and on macOS Velopack 1.2.0 restarts it with `open -n`,'
    Say 'which drops the sandbox''s environment, so the restarted GUI would run against the real data and daemon.'
    Say "  1. scripts/mux-update-survival.ps1 -Sandbox $S -SkipBuild -LeaveRunning"
    Say "     Runs steps 1-8, packs $V3 (the $V2 build, its marker 'ntilde-mux-protocol: 3-3') into the feed, and leaves the"
    Say "     $V2 GUI and the daemon running."
    Say "  2. In the sandbox's Ntilde window, open the palette and run 'Check for updates'. A toast says $V3 is downloaded."
    Say "  3. Run 'Restart to update' (or the toast's button)."
    Say '     Expected: a question naming the running multiplexed sessions, "... will be closed by the update (the new version'
    Say '     cannot keep them)." Confirm it.'
    Say "  4. Expected: the daemon's pid exits; the heartbeat files in $Shells stop growing; current\sq.version becomes $V3;"
    Say "     the restarted GUI starts a fresh daemon (a new pid, its image under $Data\bin\). $Data\logs\debug.log has"
    Say "     '[MainWindow] the update closes the multiplexer: protocol 1-2, the new build's 3-3; ...'."
    Say "  5. scripts/mux-update-survival.ps1 -Sandbox $S -CleanupOnly"
    Say 'The decision itself is covered by MuxUpdateCompatibility''s unit tests (Task 22).'
}

# ---- cleanup -----------------------------------------------------------------------------------------------------------

function Invoke-Cleanup($Before) {
    Section 'Cleanup'
    if (-not (Test-Path -LiteralPath $Marker -PathType Leaf)) {
        Say "$S carries no $([IO.Path]::GetFileName($Marker)): not this script's sandbox, so nothing is cleaned up"
        return
    }

    # Every process started from here on must see the sandbox root: the uninstall hook stops the daemon of whatever
    # root it is given.
    $env:NTILDE_APPDATA_ROOT = $Data

    $d = Read-Descriptor
    if ($d -and (Test-Alive ([int]$d.pid))) {
        $info = Get-ProcInfo ([int]$d.pid)
        if ($info -and (Test-UnderSandbox $info.ExecutablePath)) {
            if (Test-Path -LiteralPath $CurrentExe) {
                Say "the sandbox daemon (pid $($d.pid)) is still running: ntilde mux kill-server --force"
                try { $r = Invoke-Exe $CurrentExe @('mux', 'kill-server', '--force') 30; Say "  exit $($r.Code): $(($r.Out + $r.Err).Trim())" }
                catch { Say "  kill-server failed: $($_.Exception.Message)" }
            }
            if (Test-Alive ([int]$d.pid)) { Stop-SandboxProcess ([int]$d.pid) 'the sandbox daemon outlived kill-server' }
        }
    }

    if (Test-Path -LiteralPath $UpdateExe) {
        Say "the sandbox install is still there: $UpdateExe --uninstall --silent"
        try { $r = Invoke-Exe $UpdateExe @('--uninstall', '--silent') 180; Say "  exit $($r.Code)" }
        catch { Say "  the uninstall failed: $($_.Exception.Message)" }
    }

    for ($i = 0; $i -lt 3; $i++) {
        $left = Get-SandboxProcesses
        if (-not $left.Count) { break }
        foreach ($p in $left) { Stop-SandboxProcess $p.ProcessId 'left running by the run' }
        Start-Sleep -Seconds 1
    }
    $left = Get-SandboxProcesses
    Say "processes whose image is under the sandbox: $($left.Count)"

    [void] (Wait-Until { -not (Test-Path -LiteralPath $Install) } 15)
    if (Test-Path -LiteralPath $Install) {
        Say "removing the leftover $Install"
        try { Remove-SandboxFolder $Install } catch { Say "  could not remove it: $($_.Exception.Message)" }
    }

    if ((Test-Path $UninstallKey) -and -not ($Before -and $Before.UninstallKey)) {
        Say "removing the leftover HKCU Uninstall\$PackId key"
        Remove-Item -Path $UninstallKey -Recurse -Force
    }

    # The user PATH: take off the sandbox's entry; then, when what is left differs from the snapshot only in blank
    # entries (UserPathRegistration drops them when it rewrites the PATH), put the snapshot back as it was, in its
    # value kind (REG_EXPAND_SZ or REG_SZ) as well as its text.
    $now = Read-UserPath
    if ($null -ne $now) {
        $nowKind = Read-UserPathKind
        $mine = ConvertTo-PathEntryKey $CurrentDir
        $kept = @($now -split ';' | Where-Object { -not ($_.Trim() -and (ConvertTo-PathEntryKey $_) -eq $mine) })
        $without = $kept -join ';'
        $target = $without
        $targetKind = $nowKind
        if ($Before -and $null -ne $Before.UserPath) {
            $normal = { param($v) (@($v -split ';' | Where-Object { $_.Trim() }) -join ';') }
            if ((& $normal $without) -ceq (& $normal $Before.UserPath)) {
                $target = $Before.UserPath
                if ($Before.UserPathKind) { $targetKind = [string] $Before.UserPathKind }
            }
            elseif ($without -cne $Before.UserPath) { Say 'the user PATH changed during the run in entries other than the sandbox''s: only the sandbox''s entry is removed' }
        }
        if ($target -cne $now -or $targetKind -ne $nowKind) {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
            try {
                if ($Before -and $null -eq $Before.UserPath -and -not $target.Trim(';').Trim()) { $key.DeleteValue('Path') }   # there was none
                else { $key.SetValue('Path', $target, [Microsoft.Win32.RegistryValueKind] $targetKind) }
            }
            finally { $key.Dispose() }
            Say "the user PATH was put back ($targetKind)"
        }
    }

    if ($Before) {
        foreach ($lnk in @(Get-NtildeShortcuts | Where-Object { $Before.Shortcuts -notcontains $_ })) {
            if ((Split-Path -Leaf $lnk) -match 'Ntilde ?Survival') { Say "removing the shortcut $lnk"; Remove-Item -LiteralPath $lnk -Force }
            else { Say "a new shortcut appeared that this run does not own; left alone: $lnk" }
        }
        if ((Test-Path -LiteralPath $VelopackTemp) -and -not $Before.VelopackTemp) { Remove-Item -LiteralPath $VelopackTemp -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # Velopack's per-app log: removed only when this run made it; one that was there before is copied and left.
    if (Test-Path -LiteralPath $VelopackAppLog) {
        Copy-Item -LiteralPath $VelopackAppLog -Destination (Join-Path $Evidence "velopack_$PackId.log") -Force
        if ($Before -and -not $Before.VelopackAppLog) {
            Remove-Item -LiteralPath $VelopackAppLog -Force -ErrorAction SilentlyContinue
            Say "Velopack's log for $PackId is kept as $(Join-Path $Evidence "velopack_$PackId.log") and removed from $env:LOCALAPPDATA\velopack (this run made it)"
        }
        else {
            Say "Velopack's log for $PackId is copied to $(Join-Path $Evidence "velopack_$PackId.log") and left in place (it was there before this run, or there is no snapshot)"
        }
    }
    if ($Before) {
        $grew = (Get-FileLength $VelopackSharedLog) - $Before.VelopackSharedLogBytes
        if ($grew -gt 0) {
            [IO.File]::WriteAllText((Join-Path $Evidence 'velopack-shared-log-appended.txt'), (Read-Since $VelopackSharedLog $Before.VelopackSharedLogBytes))
            Say "the shared $VelopackSharedLog grew by $grew bytes (non-installed copies' VelopackApp.Run: the daemon's copy); left in place, the appended text is in evidence\velopack-shared-log-appended.txt"
        }
    }
}

# ---- the run -------------------------------------------------------------------------------------------------------------

$saved = @{ Root = $env:NTILDE_APPDATA_ROOT; Source = $env:NTILDE_UPDATE_SOURCE_DIR; Path = $env:PATH }

# What the finally block needs from the run: the snapshot to put back, the user's own processes to check, and whether
# the sandbox is deliberately left running (-LeaveRunning).
$Run = @{ Before = $null; UserProcesses = @(); LeaveRunning = $false }

Say "mux-update-survival: sandbox $S ($(if ($sandboxIsNew) { 'new; marked now' } else { 'marked' })), builds $V1 and $V2, packId $PackId"

# Refusals that must change nothing: before the try/finally below, so no cleanup is armed for them (ruling R4).
if (-not $CleanupOnly) {
    $leftover = @()
    if (Test-Path $UninstallKey) { $leftover += "HKCU Uninstall\$PackId exists" }
    if (Test-Path -LiteralPath $Install) { $leftover += "$Install exists" }
    if ((Get-SandboxProcesses).Count) { $leftover += 'processes are running from under the sandbox' }
    if ($leftover.Count) {
        Say "REFUSED: an earlier run left an install ($($leftover -join '; ')). Clean it up with -CleanupOnly first; nothing was changed."
        exit 2
    }
}

function Invoke-Run {
    if ($CleanupOnly) {
        if (Test-Path -LiteralPath $SnapshotFile) { $Run.Before = Get-Content -LiteralPath $SnapshotFile -Raw | ConvertFrom-Json }
        return
    }

    Section 'Safety audit'
    $Run.UserProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(Ntilde|ntilde-mux)(\.exe)?$' -and -not (Test-UnderSandbox $_.ExecutablePath) })
    Say "the sandbox carries $([IO.Path]::GetFileName($Marker)); it lies under %TEMP%: $(@($tempRoots | Where-Object { Test-Inside $S $_ }).Count -gt 0)"
    Say "real install $RealInstall exists: $(Test-Path -LiteralPath $RealInstall) (never touched)"
    Say "real data $RealData exists: $(Test-Path -LiteralPath $RealData) (never touched)"
    Say "the user's own ntilde processes (left alone; checked again at the end): $(if ($Run.UserProcesses.Count) { ($Run.UserProcesses | ForEach-Object { "$($_.ProcessId) $($_.ExecutablePath)" }) -join '; ' } else { 'none' })"
    Say "isolation: the multiplexer's pipe is ntilde-mux-<user>-<hash of the root>, and its descriptor and copies live under $Data"
    Say 'isolation: the agent host''s pipe, ntilde-agent-<user>, is global per user: the sandbox settings keep it off (AgentAccessObserveEnabled=false, the default)'
    Say 'isolation: the quake-mode hotkey is a global hotkey: the sandbox settings turn it off (QuakeModeEnabled=false)'
    Say 'isolation: no single-instance mutex exists; the shells are copies of cmd.exe under the sandbox; no SSH, so no Credential Manager'

    if (-not $SkipBuild) {
        Section '1. Build (as release.yml''s win-x64 lane)'
        $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        if ((Test-Path -LiteralPath $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH = "$vsInstaller;$env:PATH" }   # vswhere, for ILC's link step
        Invoke-Publish $V1
        Invoke-Publish $V2
    }
    foreach ($v in $V1, $V2) { if (-not (Test-Path -LiteralPath (Join-Path $Publish "$v\Ntilde.exe"))) { throw "$(Join-Path $Publish "$v\Ntilde.exe") is missing; run without -SkipBuild." } }
    if ($BuildOnly) { return }

    Section '2. Pack (vpk 1.2.0, packId NtildeSurvival)'
    $vpk = Get-Vpk
    $range = Get-ProtocolRange
    Say "protocol range from scripts/ci/mux-protocol-range.sh: $range"
    foreach ($dir in $Feed, $SetupDir) { Remove-SandboxFolder $dir }
    New-Item -ItemType Directory -Force -Path $Feed, $SetupDir | Out-Null
    Invoke-Pack $vpk $V1 (Join-Path $Publish $V1) (Write-Notes $V1 $range)
    $setup = Get-ChildItem -LiteralPath $Feed -Filter '*Setup.exe' | Select-Object -First 1
    if (-not $setup) { throw 'vpk pack produced no Setup.exe' }
    Copy-Item -LiteralPath $setup.FullName -Destination $SetupDir   # the next pack overwrites the feed's Setup.exe
    Invoke-Pack $vpk $V2 (Join-Path $Publish $V2) (Write-Notes $V2 $range)
    $feedJson = Read-SharedText (Join-Path $Feed 'releases.win.json')
    Say "feed: $((Get-ChildItem -LiteralPath $Feed -File | ForEach-Object { $_.Name }) -join ', ')"
    Check 'the feed lists both builds with the protocol marker in their notes' (($feedJson -match [regex]::Escape($V1)) -and ($feedJson -match [regex]::Escape($V2)) -and ($feedJson -match "ntilde-mux-protocol: $range")) "releases.win.json"

    Section 'Side effects before the install'
    $Run.Before = Get-SideEffects
    $Run.Before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $SnapshotFile -Encoding utf8
    Format-SideEffects $Run.Before

    Section '3. Install the first build'
    $env:NTILDE_APPDATA_ROOT = $Data
    $env:NTILDE_UPDATE_SOURCE_DIR = $Feed
    Say "NTILDE_APPDATA_ROOT=$Data"
    Say "NTILDE_UPDATE_SOURCE_DIR=$Feed"
    foreach ($dir in $Data, $Shells) { Remove-SandboxFolder $dir }   # an earlier run's
    Write-Heartbeat
    Write-SandboxProfile (Join-Path $Shells 'tab-shell.exe')
    $r = Invoke-Exe (Join-Path $SetupDir $setup.Name) @('--silent', '--log', (Join-Path $Evidence 'setup.log'), '--installto', $Install) 300
    Check 'Setup.exe --silent installed the first build' ($r.Code -eq 0 -and (Get-SqVersion) -eq $V1) "exit $($r.Code), current\sq.version $(Get-SqVersion)"
    Check 'the install hook put the install''s current\ on the user PATH' (Test-PathNamesSandbox (Read-UserPath)) $CurrentDir
    Check 'no shortcut was made' (-not @(Get-NtildeShortcuts | Where-Object { $Run.Before.Shortcuts -notcontains $_ }).Count)

    Section '4. Start the first build''s GUI'
    $gui1 = Start-Gui
    Say "GUI pid $($gui1.Id): $CurrentExe"
    $daemon = Wait-Until {
        $d = Read-Descriptor
        if ($d -and (Test-Alive ([int]$d.pid))) {
            $info = Get-ProcInfo ([int]$d.pid)
            if ($info -and $info.ExecutablePath) { return [pscustomobject]@{ Pid = [int]$d.pid; Image = $info.ExecutablePath; CommandLine = $info.CommandLine; Descriptor = $d } }
        }
        return $null
    } 90
    if (-not $daemon) { throw "No daemon came up under $Data within 90 s" }
    $copyDir = Join-Path $Data "bin\$V1"
    Say "daemon pid $($daemon.Pid): $($daemon.CommandLine)"
    Check 'the daemon runs from its own copy outside the install root' ((Split-Path $daemon.Image) -ieq $copyDir) $daemon.Image
    $copyExisted = Test-Path -LiteralPath (Join-Path $copyDir 'Ntilde.exe')
    # The hook is honoured only for an install packed under the verification id (ruling R1): this line proves it is active.
    $source = Wait-Until { Find-LogLine (Join-Path $Data 'logs\debug.log') 'Update source: the local directory' } 30
    Check "the GUI logged at startup that the update-source hook is active for the $PackId install" ([bool]$source) "$source"

    Section '5. Start two heartbeat sessions (ntilde mux spawn-for-test)'
    $hbExe = Join-Path $Shells 'hb-shell.exe'
    $hbScript = Join-Path $Shells 'heartbeat.cmd'
    $beatFiles = @((Join-Path $Shells 'hb1.txt'), (Join-Path $Shells 'hb2.txt'))
    $heartbeatIds = @()
    foreach ($file in $beatFiles) {
        $r = Invoke-Exe $CurrentExe @('mux', 'spawn-for-test', $hbExe, "/d /c $hbScript $file") 60
        Say "ntilde mux spawn-for-test $hbExe `"/d /c $hbScript $file`" -> exit $($r.Code) $($r.Out.Trim()) $($r.Err.Trim())"
        if ($r.Code -ne 0) { throw "spawn-for-test exited $($r.Code)" }
        $heartbeatIds += $r.Out.Trim()
    }
    $beating = Wait-Until { ((Read-Beats $beatFiles[0]).Count -ge 3) -and ((Read-Beats $beatFiles[1]).Count -ge 3) } 30
    Check 'both heartbeats are writing' ([bool]$beating) "$((Read-Beats $beatFiles[0]).Count) and $((Read-Beats $beatFiles[1]).Count) beats"

    Section '6. ntilde mux ls'
    $r = Invoke-Exe $CurrentExe @('mux', 'ls') 60
    foreach ($line in ($r.Out.TrimEnd() -split "`r?`n")) { Say "  $line" }
    $sessionsBefore = Get-MuxSessions
    $idsBefore = @($sessionsBefore | ForEach-Object { "$($_.sessionId)" } | Sort-Object)
    Check 'ntilde mux ls lists both heartbeat sessions, running' (@($heartbeatIds | Where-Object { $idsBefore -contains $_ }).Count -eq 2 -and @($sessionsBefore | Where-Object { $heartbeatIds -contains "$($_.sessionId)" -and $_.running }).Count -eq 2) "$($idsBefore.Count) sessions: $($idsBefore -join ', ')"
    $hbProcs = @(Get-CimInstance Win32_Process -Filter "Name='hb-shell.exe'" | Where-Object { Test-UnderSandbox $_.ExecutablePath })
    $hosts = @(Get-CimInstance Win32_Process -Filter "Name='OpenConsole.exe'" | Where-Object { Test-UnderSandbox $_.ExecutablePath })
    Say "heartbeat shells: $(($hbProcs | ForEach-Object { "$($_.ProcessId) (parent $($_.ParentProcessId))" }) -join ', ')"
    Say "console hosts: $(($hosts | ForEach-Object { "$($_.ProcessId) $($_.ExecutablePath)" }) -join '; ')"

    Section '7. Stage the second build, then restart into it'
    $staged = Wait-Until { Find-LogLine (Join-Path $Data 'logs\debug.log') "Update $([regex]::Escape($V2)) downloaded" } 120
    Check "the GUI's own update check staged $V2 from the local feed" ([bool]$staged) "$staged"
    if (-not $staged) { throw "$V2 was not staged within 120 s" }
    Say "closing the GUI (pid $($gui1.Id)); its sessions are kept (SessionPersistence=KeepOnClose, mux-close-choice=keep)"
    [void] $gui1.CloseMainWindow()
    if (-not $gui1.WaitForExit(30000)) { Stop-SandboxProcess $gui1.Id 'the GUI did not close within 30 s' }
    # Moved aside, so every line read from debug.log from now on is the second build's.
    Move-Item -LiteralPath (Join-Path $Data 'logs\debug.log') -Destination (Join-Path $Evidence "debug-$V1.log") -Force
    Check 'the daemon outlived the GUI''s close' (Test-Alive $daemon.Pid) "pid $($daemon.Pid)"

    $vpOffset = Get-FileLength $VelopackAppLog
    $t0 = Get-Date
    $gui2 = Start-Gui
    Say "started the first build's GUI again (pid $($gui2.Id)): Velopack's startup auto-apply should apply $V2"
    $daemonSeenDown = $false
    $samples = 0
    $newGui = $null
    $deadline = $t0.AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        $samples++
        if (-not (Test-Alive $daemon.Pid)) { $daemonSeenDown = $true }
        if ((Get-SqVersion) -eq $V2) {
            $newGui = Get-GuiProcesses | Where-Object { (Get-ImageVersion $_.ProcessId) -like "$V2*" } | Select-Object -First 1
            if ($newGui) { break }
        }
        Start-Sleep -Milliseconds 250
    }
    $t1 = Get-Date
    Say "the apply took $([int]($t1 - $t0).TotalMilliseconds) ms, from the restart to the second build's GUI (pid $(if ($newGui) { $newGui.ProcessId } else { 'none' })); $samples samples, the daemon $(if ($daemonSeenDown) { 'WAS' } else { 'was never' }) seen down"
    Say "the first build's relaunch (pid $($gui2.Id)) $(if ($gui2.HasExited) { "exited with $($gui2.ExitCode)" } else { 'is still running' })"
    Section "Velopack's log during the apply"
    $applyLog = @((Read-Since $VelopackAppLog $vpOffset) -split "`r?`n" | Where-Object { $_ })
    foreach ($line in $applyLog) { Say "  $line" }

    Section '8. Verify'
    # The apply came from the relaunch's startup auto-apply, not from anything else: that very process decided to apply,
    # and Update.exe was started to wait for it. Whether the wait itself worked is Velopack's business (here it cannot
    # open the pid: "Failed to wait ... Access is denied"), so that line is reported, not checked.
    $relaunchId = $gui2.Id
    $autoApply = $applyLog | Where-Object { $_ -match "\[lib-csharp:$relaunchId\].*Auto apply is true" } | Select-Object -First 1
    $waitedFor = $applyLog | Where-Object { $_ -match "Update\.exe apply .*--waitPid $relaunchId(\s|$)" -or $_ -match "Wait: WaitPid\($relaunchId\)" } | Select-Object -First 1
    $waitFailed = $applyLog | Where-Object { $_ -match 'Failed to wait for process' } | Select-Object -First 1
    Check "the first build's relaunch (pid $relaunchId) decided the startup auto-apply" ([bool]$autoApply) "$autoApply"
    Check "Update.exe was started with --waitPid $relaunchId" ([bool]$waitedFor) "$waitedFor"
    if ($waitFailed) { Say "observation (not a check): $waitFailed" }
    Check "current\sq.version is $V2" ((Get-SqVersion) -eq $V2) "$(Get-SqVersion)"
    Check "the second build's GUI runs" ([bool]$newGui) "$(if ($newGui) { "pid $($newGui.ProcessId), image version $(Get-ImageVersion $newGui.ProcessId)" })"
    $after = Get-ProcInfo $daemon.Pid
    Check 'the daemon''s pid is unchanged, and it never went down' (($null -ne $after) -and -not $daemonSeenDown) "pid $($daemon.Pid)"
    Check "its image is still under $copyDir" ($after -and ((Split-Path $after.ExecutablePath) -ieq $copyDir)) "$(if ($after) { $after.ExecutablePath })"
    $hbAfter = @($hbProcs | Where-Object { Test-Alive $_.ProcessId })
    Check 'the heartbeat shells are the same processes' ($hbAfter.Count -eq $hbProcs.Count -and $hbProcs.Count -eq 2) "$(($hbProcs | ForEach-Object { $_.ProcessId }) -join ', ')"
    Start-Sleep -Seconds 6   # so beats after the apply can be counted
    $n = 0
    foreach ($file in $beatFiles) {
        $n++
        $m = Measure-Beats (Read-Beats $file) $t0.AddSeconds(-5) $t1.AddSeconds(2)
        Check "heartbeat $n kept advancing across the apply" ($m.Before -gt 0 -and $m.After -ge 3 -and $m.MaxGap -le [TimeSpan]::FromSeconds(3)) ("{0} beats; the longest gap from 5 s before the restart to 2 s after the new GUI: {1:N0} ms; {2} beats after it" -f $m.Count, $m.MaxGap.TotalMilliseconds, $m.After)
    }
    $sessionsAfter = Get-MuxSessions
    $idsAfter = @($sessionsAfter | ForEach-Object { "$($_.sessionId)" } | Sort-Object)
    $missing = @($idsBefore | Where-Object { $idsAfter -notcontains $_ })
    $extra = @($idsAfter | Where-Object { $idsBefore -notcontains $_ })
    # Self-contained: an empty list before, or one without both heartbeats, fails rather than passing vacuously.
    $sameIds = $idsBefore.Count -ge 2 -and @($heartbeatIds | Where-Object { $idsBefore -contains $_ }).Count -eq 2 -and -not $missing.Count
    Check 'after the restart, ntilde mux ls lists the same session ids' $sameIds "before: $($idsBefore -join ', '); after: $($idsAfter -join ', ')$(if ($extra.Count) { "; new since: $($extra -join ', ')" })"
    $r = Invoke-Exe $CurrentExe @('mux', 'ls') 60
    foreach ($line in ($r.Out.TrimEnd() -split "`r?`n")) { Say "  $line" }
    # debug.log is the second build's only (the first's was moved aside), and the line must name this build as $V2.
    $notice = Wait-Until { Find-LogLine (Join-Path $Data 'logs\debug.log') 'is from another build' } 60
    Say "Task 23 notice ('Multiplexer is from the previous build'): $(if ($notice) { $notice } else { 'NOT raised within 60 s' })"
    Check 'the second build''s GUI offered to restart the previous build''s multiplexer' ([bool]$notice -and $notice -match ([regex]::Escape("this is $V2") + '\)')) "$notice"
    Copy-Item -LiteralPath (Join-Path $Data 'logs\debug.log') -Destination (Join-Path $Evidence "debug-$V2.log") -Force

    if ($LeaveRunning) {
        Section "Packing $V3 for the manual no-overlap steps"
        Invoke-Pack $vpk $V3 (Join-Path $Publish $V2) (Write-Notes $V3 '3-3')
        Show-NoOverlapSteps
        Say "LEFT RUNNING: the $V2 GUI and the daemon (pid $($daemon.Pid)). When done: scripts/mux-update-survival.ps1 -Sandbox $S -CleanupOnly"
        $Run.LeaveRunning = $true
        return
    }
    Show-NoOverlapSteps

    Section '10. Uninstall, with the daemon and the second build''s GUI running'
    $vpOffset = Get-FileLength $VelopackAppLog
    $guiPid = if ($newGui) { $newGui.ProcessId } else { 0 }
    $r = Invoke-Exe $UpdateExe @('--uninstall', '--silent') 180
    [IO.File]::WriteAllText((Join-Path $Evidence 'uninstall-stdout.txt'), $r.Out + $r.Err)
    Say "Update.exe --uninstall --silent exited $($r.Code)"
    $uninstallLog = @((Read-Since $VelopackAppLog $vpOffset) -split "`r?`n" | Where-Object { $_ })
    Section "Velopack's log during the uninstall"
    foreach ($line in $uninstallLog) { Say "  $line" }
    Section 'Uninstall order'
    $i = 0
    foreach ($line in $uninstallLog) {
        $i++
        if ($line -match 'Checking for running processes|Killing process|Running --veloapp-uninstall hook|\[Mux\] uninstall|Hook executed|PATH: removed|Removing directory|Removing uninstall registry') { Say ("  {0,3}: {1}" -f $i, ($line -replace '^.*?\] \[[A-Z]+\] ', '')) }
    }
    $firstKill = ($uninstallLog | Select-String -Pattern 'Checking for running processes' | Select-Object -First 1).LineNumber
    $hookAt = ($uninstallLog | Select-String -Pattern 'Running --veloapp-uninstall hook' | Select-Object -First 1).LineNumber
    $guiKilled = [bool]($uninstallLog | Select-String -Pattern "Killing process: .*\($guiPid\)")
    Say "Velopack's first kill pass is line $firstKill; our uninstall hook starts at line ${hookAt}: the kill pass runs $(if ($firstKill -and $hookAt -and $firstKill -lt $hookAt) { 'BEFORE' } else { 'after' }) the hook. The second build's GUI (pid $guiPid) was $(if ($guiKilled) { 'killed by it' } else { 'not named in it' })."
    $stopped = Wait-Until { -not (Test-Alive $daemon.Pid) } 15
    Check 'the uninstall stopped the daemon, which runs outside the install root' ([bool]$stopped) "pid $($daemon.Pid)"
    $hookLine = $uninstallLog | Where-Object { $_ -match '\[Mux\] uninstall: ' } | Select-Object -Last 1
    Check 'our uninstall hook is what stopped it' ([bool]($hookLine -match '\[Mux\] uninstall: (stopped|terminated) the multiplexer daemon')) "$hookLine"
    # Self-contained: both heartbeat shells were seen, and the copy existed, so an empty "before" cannot pass these.
    Check 'the heartbeat shells ended with it' ($hbProcs.Count -eq 2 -and -not @($hbProcs | Where-Object { Test-Alive $_.ProcessId }).Count) "$(($hbProcs | ForEach-Object { $_.ProcessId }) -join ', ')"
    $copiesLeft = @(Get-ChildItem -LiteralPath (Join-Path $Data 'bin') -Directory -Force -ErrorAction SilentlyContinue)
    Check 'the daemon''s copies are gone' ($copyExisted -and -not (Test-Path -LiteralPath $copyDir) -and -not $copiesLeft.Count) "$(if ($copiesLeft.Count) { ($copiesLeft | ForEach-Object { $_.Name }) -join ', ' } else { "$copyDir existed during the run; $Data\bin is now empty or gone" })"
    $gone = Wait-Until { -not (Test-Path -LiteralPath $Install) } 20
    Check 'the install folder is gone' ([bool]$gone) $Install
    Check 'the Uninstall key is gone' (-not (Test-Path $UninstallKey))
    Check 'the uninstall hook took the install off the user PATH' (-not (Test-PathNamesSandbox (Read-UserPath)))
}

try {
    Invoke-Run
}
catch {
    Say "ERROR: $($_.Exception.Message)"
    Say "$($_.ScriptStackTrace)"
    Check 'the run completed' $false $_.Exception.Message
}
finally {
    if (-not $Run.LeaveRunning -and ($Run.Before -or $CleanupOnly -or (Test-Path -LiteralPath $UpdateExe) -or (Get-SandboxProcesses).Count)) {
        try { Invoke-Cleanup $Run.Before } catch { Say "CLEANUP ERROR: $($_.Exception.Message)"; Check 'the cleanup completed' $false $_.Exception.Message }
    }
    $env:NTILDE_APPDATA_ROOT = $saved.Root
    $env:NTILDE_UPDATE_SOURCE_DIR = $saved.Source
    $env:PATH = $saved.Path

    if ($Run.Before -and -not $Run.LeaveRunning) {
        Section 'Side effects after the cleanup'
        $afterFx = Get-SideEffects
        Format-SideEffects $afterFx
        Check 'the user PATH is exactly as before, text and value kind' (($afterFx.UserPath -ceq $Run.Before.UserPath) -and ("$($afterFx.UserPathKind)" -eq "$($Run.Before.UserPathKind)")) "$($afterFx.UserPathKind)"
        Check 'no HKCU Uninstall key is left' ($afterFx.UninstallKey -eq $Run.Before.UninstallKey)
        Check 'no shortcut is left' ((@($afterFx.Shortcuts) -join '|') -eq (@($Run.Before.Shortcuts) -join '|'))
        Check "Velopack's per-app log and temp folder are gone" (($afterFx.VelopackAppLog -eq $Run.Before.VelopackAppLog) -and ($afterFx.VelopackTemp -eq $Run.Before.VelopackTemp))
        Check 'no process runs from under the sandbox' (-not (Get-SandboxProcesses).Count)
    }
    if ($Run.UserProcesses.Count) {
        $intact = @($Run.UserProcesses | Where-Object { $p = Get-ProcInfo $_.ProcessId; $p -and $p.CreationDate -eq $_.CreationDate })
        Check 'the user''s own ntilde processes are untouched' ($intact.Count -eq $Run.UserProcesses.Count) "$($intact.Count) of $($Run.UserProcesses.Count) still running"
    }

    Section 'Summary'
    foreach ($c in $Checks) { Say ("  {0}  {1}" -f $(if ($c.Ok) { 'PASS' } else { 'FAIL' }), $c.Name) }
    $failed = @($Checks | Where-Object { -not $_.Ok }).Count
    Say "$($Checks.Count - $failed) of $($Checks.Count) checks passed. Evidence: $Evidence"
}
exit $(if (@($Checks | Where-Object { -not $_.Ok }).Count) { 1 } else { 0 })
