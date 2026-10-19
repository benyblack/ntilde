#Requires -Version 7.2
<#
.SYNOPSIS
    Phase 5 manual checklist, steps 9, 10 and 48 on Windows: the `ntilde mux` verbs through ntilde.com.

.DESCRIPTION
    ls, ls --json, kill, kill-server [--force] and attach's error paths, against a daemon started as the product starts
    it; `ntilde mux version` is not a verb of the app (step 10; ntilde-mux does not ship for Windows); ntilde.com
    forwards exit codes and passes a quoted argument through as one (step 48). No GUI, no SSH, no credential store.

    The App folder is copied to <Sandbox>\cli\app with ntilde.com beside Ntilde.exe, so every process the script starts
    runs from the sandbox and is told apart from a real Ntilde by its path. Every process gets
    NTILDE_APPDATA_ROOT=<Sandbox>\cli\data. Sessions are started with the hidden `ntilde mux spawn-for-test` (setup
    only), in a copy of cmd.exe named t30shell.exe. Exits 1 when any check fails.

.PARAMETER App
    The App build folder, the one holding Ntilde.exe (a dev build, or CI's test output).

.PARAMETER Launcher
    ntilde.com, or the Ntilde.Launcher.exe published from src/Ntilde.Launcher (-c Release -r win-x64). Optional when
    the App folder holds ntilde.com already.

.PARAMETER Sandbox
    A folder of the script's own; it writes only <Sandbox>\cli.

.EXAMPLE
    scripts/mux-smoke/win-cli-cells.ps1 -App src\Ntilde.App\bin\Debug\net10.0 -Launcher artifacts\launcher\Ntilde.Launcher.exe -Sandbox $env:TEMP\ntilde-smoke
#>
param(
    [Parameter(Mandatory)] [string] $App,
    [string] $Launcher,
    [Parameter(Mandatory)] [string] $Sandbox
)
$ErrorActionPreference = 'Stop'
# A non-zero exit is what most cells check: it must reach $LASTEXITCODE, not throw.
$PSNativeCommandUseErrorActionPreference = $false

function Say([string] $line) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $line) }
function IsGuid([string] $s) { $g = [guid]::Empty; return [guid]::TryParse($s, [ref]$g) }

$script:results = [System.Collections.Generic.List[string]]::new()
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    $verdict = if ($ok) { 'PASS' } else { 'FAIL' }
    $script:results.Add("$verdict  $name")
    Say "$verdict  $name$(if ($detail) { " - $detail" })"
}

# Runs an exe with the arguments given; captures stdout+stderr and the exit code.
function Run([string] $exe, [string[]] $argv) {
    $out = & $exe @argv 2>&1 | Out-String
    $code = $LASTEXITCODE
    Say "> $(Split-Path $exe -Leaf) $($argv -join ' ')"
    foreach ($l in ($out.TrimEnd() -split "`r?`n")) { Say "  $l" }
    Say "  (exit $code)"
    return [pscustomobject]@{ Out = $out; Code = $code }
}

# ---- the sandbox ---------------------------------------------------------------------------------------------------
$AppSrc = (Resolve-Path -LiteralPath $App).Path
if (-not (Test-Path (Join-Path $AppSrc 'Ntilde.exe'))) { throw "no Ntilde.exe in $AppSrc" }
if (-not $Launcher) { $Launcher = Join-Path $AppSrc 'ntilde.com' }
if (-not (Test-Path -LiteralPath $Launcher)) { throw "no launcher at ${Launcher}: pass -Launcher (Ntilde.Launcher.exe from src/Ntilde.Launcher)" }
$Launcher = (Resolve-Path -LiteralPath $Launcher).Path
New-Item -ItemType Directory -Force $Sandbox | Out-Null
$SandboxRoot = (Resolve-Path -LiteralPath $Sandbox).Path.TrimEnd('\')
if ($SandboxRoot -eq [IO.Path]::GetPathRoot($SandboxRoot).TrimEnd('\') -or $SandboxRoot -eq $env:USERPROFILE.TrimEnd('\')) {
    throw "refusing $SandboxRoot as a sandbox: give a folder of its own"
}
$Sp = Join-Path $SandboxRoot 'cli'
$Data = Join-Path $Sp 'data'
$AppDir = Join-Path $Sp 'app'
$Shells = Join-Path $Sp 'shells'
$Com = Join-Path $AppDir 'ntilde.com'
$Exe = Join-Path $AppDir 'Ntilde.exe'
$Shell = Join-Path $Shells 't30shell.exe'
$Descriptor = Join-Path $Data 'mux\mux-endpoint.json'

function SandboxProcesses {
    @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Sp + '\', [StringComparison]::OrdinalIgnoreCase) })
}

$env:NTILDE_APPDATA_ROOT = $Data
Say "=== Phase 5 checklist steps 9, 10 and 48: Windows CLI cells, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') ==="
Say "NTILDE_APPDATA_ROOT=$env:NTILDE_APPDATA_ROOT (every process below inherits it)"
$userBefore = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(ntilde|ntilde-mux)(\.exe|\.com)$' -and -not ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($Sp + '\', [StringComparison]::OrdinalIgnoreCase)) })
foreach ($p in $userBefore) { Say "a process of the user's own (left alone): pid $($p.ProcessId) $($p.ExecutablePath) started $($p.CreationDate)" }
if ((SandboxProcesses).Count) { throw "processes are already running from $Sp; stop them first" }

foreach ($d in $AppDir, $Data, $Shells) { if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
# robocopy: 0-7 is success. The GUI's natives for other OSes and the symbols stay behind.
robocopy $AppSrc $AppDir /E /XD osx* linux* /XF *.pdb /NJH /NJS /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy $AppSrc -> $AppDir failed ($LASTEXITCODE)" }
Copy-Item -LiteralPath $Launcher -Destination $Com -Force
New-Item -ItemType Directory -Force $Data, $Shells | Out-Null
Copy-Item -LiteralPath "$env:SystemRoot\System32\cmd.exe" -Destination $Shell -Force
$settings = [ordered]@{
    SessionPersistence = 'KeepOnClose'
    AgentAccessObserveEnabled = $false   # the agent pipe ntilde-agent-<user> is not keyed by the root
    QuakeModeEnabled = $false            # a global hotkey
    AutomaticUpdateChecks = $false
}
[IO.File]::WriteAllText((Join-Path $Data 'settings.json'), ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Say "app: $AppSrc copied to $AppDir; ntilde.com = $Launcher ($((Get-Item $Com).Length) bytes)"

function Start-Daemon {
    # The product starts the daemon as `<exe> mux serve`, detached from the caller's stdio (ProcessMuxDaemonSpawner);
    # Start-Process without redirection goes through ShellExecute, which inherits no handles.
    $p = Start-Process -FilePath $Exe -ArgumentList 'mux', 'serve' -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $Descriptor)) { Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 300
    $d = if (Test-Path $Descriptor) { Get-Content $Descriptor -Raw | ConvertFrom-Json } else { $null }
    Say "started: Ntilde.exe mux serve (pid $($p.Id)); descriptor: $(if ($d) { "pid $($d.pid), endpoint $($d.endpoint), startTime $($d.startTime)" } else { 'MISSING' })"
    return $p
}

try {
    # ---- C1: no daemon ---------------------------------------------------------------------------------------------
    Say ''
    Say '=== C1. With no multiplexer running ==='
    $r = Run $Com @('mux', 'ls')
    Check 'ls with no daemon: "No multiplexer is running.", exit 1 (through ntilde.com)' ($r.Code -eq 1 -and $r.Out -match 'No multiplexer is running\.')
    $r = Run $Com @('mux', 'ls', '--json')
    Check 'ls --json with no daemon: exit 1' ($r.Code -eq 1 -and $r.Out -match 'No multiplexer is running\.')
    $r = Run $Com @('mux', 'kill-server')
    Check 'kill-server with no daemon: "No multiplexer is running.", exit 1' ($r.Code -eq 1 -and $r.Out -match 'No multiplexer is running\.')
    $r = Run $Com @('mux', 'attach', 'abcd1234')
    Check 'attach with no daemon: exit 2 (its code for any connection error)' ($r.Code -eq 2 -and $r.Out -match 'No multiplexer is running\.')
    Check 'no daemon was started by those verbs' (-not (Test-Path $Descriptor) -and (SandboxProcesses).Count -eq 0)
    $r = Run $Com @('mux', 'version')
    Check '`ntilde mux version` is not an App verb: usage, exit 2 (the version verb is ntilde-mux --version)' ($r.Code -eq 2 -and $r.Out -match 'Usage:' -and $r.Out -notmatch 'version')

    # ---- C2: a daemon, two sessions ----------------------------------------------------------------------------------
    Say ''
    Say '=== C2. Start the daemon the way the product does, then two sessions (spawn-for-test: setup only) ==='
    $daemon = Start-Daemon
    $desc = if (Test-Path $Descriptor) { Get-Content $Descriptor -Raw | ConvertFrom-Json } else { $null }
    Check 'the daemon wrote its descriptor under the sandbox root, and its endpoint is the root-keyed pipe' ($desc -and $desc.pid -eq $daemon.Id -and $desc.endpoint -match '^ntilde-mux-')
    # PR #512 H8: the kill path (kill-server --force against another protocol version) terminates only a process whose
    # start time matches the one the descriptor recorded, so the descriptor must record one.
    Check 'the descriptor records the daemon''s start time (H8: the kill path needs it)' ($desc -and $desc.startTime -gt 0)
    $r = Run $Com @('mux', 'ls')
    Check 'ls with an empty daemon: "No sessions.", exit 0' ($r.Code -eq 0 -and $r.Out -match 'No sessions\.')
    $a = (Run $Com @('mux', 'spawn-for-test', $Shell, '/q /k echo t30-session-A')).Out.Trim()
    $b = (Run $Com @('mux', 'spawn-for-test', $Shell, '/q /k echo t30-session-B')).Out.Trim()
    Check 'spawn-for-test (setup) printed two session ids' ((IsGuid $a) -and (IsGuid $b))
    Start-Sleep -Seconds 1
    $shellProcs = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Shell })
    Say "shell processes: $(($shellProcs | ForEach-Object { "pid $($_.ProcessId) parent $($_.ParentProcessId)" }) -join '; ')"

    Say ''
    Say '=== C3. ntilde mux ls / ls --json ==='
    $r = Run $Com @('mux', 'ls')
    $rows = @($r.Out -split "`r?`n" | Where-Object { $_ -match '^[0-9a-f]{8}-' })
    Check 'ls lists both sessions as running, 0 attached, with the header' ($r.Code -eq 0 -and $r.Out -match '(?m)^ID\s+STATE\s+ATTACHED\s+SIZE\s+TITLE' -and $rows.Count -eq 2 -and @($rows | Where-Object { $_ -match "^($a|$b)\s+running\s+0\s" }).Count -eq 2)
    $r = Run $Com @('mux', 'ls', '--json')
    $json = $r.Out | ConvertFrom-Json
    $ids = @($json.sessions | ForEach-Object { $_.sessionId })
    Check 'ls --json: {"sessions":[...]} with both ids, running, attachedClients 0' ($r.Code -eq 0 -and $ids.Count -eq 2 -and $ids -contains $a -and $ids -contains $b -and -not ($json.sessions | Where-Object { -not $_.running -or $_.attachedClients -ne 0 }))
    # Step 48: a quoted argument with spaces arrives as one argument.
    $argsB = @($json.sessions | Where-Object { $_.sessionId -eq $b } | ForEach-Object { $_.arguments })
    Check 'ntilde.com passed "/q /k echo t30-session-B" through as one argument (step 48)' ($argsB.Count -eq 1 -and $argsB[0] -eq '/q /k echo t30-session-B')
    $r2 = Run $Exe @('mux', 'ls')
    Check 'Ntilde.exe mux ls straight (output captured) gives the same table' ($r2.Code -eq 0 -and $r2.Out -match [regex]::Escape($a) -and $r2.Out -match [regex]::Escape($b))

    Say ''
    Say '=== C4. ntilde mux kill ==='
    $shellA = $shellProcs | Where-Object { $_.CommandLine -match 't30-session-A' } | Select-Object -First 1
    $r = Run $Com @('mux', 'kill', $a)
    Check 'kill <id>: "Killed <id>.", exit 0' ($r.Code -eq 0 -and $r.Out -match "Killed $a\.")
    Start-Sleep -Seconds 1
    $r = Run $Com @('mux', 'ls')
    Check 'ls no longer lists the killed session; the other stays running' ($r.Out -notmatch [regex]::Escape($a) -and $r.Out -match "$b\s+running")
    Check "the killed session's shell process is gone" ($shellA -and -not (Get-Process -Id $shellA.ProcessId -ErrorAction SilentlyContinue)) "pid $($shellA.ProcessId)"
    $bogus = [guid]::NewGuid().ToString()
    $r = Run $Com @('mux', 'kill', $bogus)
    Check 'kill of an unknown id: "No session <id>.", exit 1' ($r.Code -eq 1 -and $r.Out -match "No session $bogus\.")
    $r = Run $Com @('mux', 'kill', 'not-a-guid')
    Check 'kill with a malformed id: usage, exit 2' ($r.Code -eq 2 -and $r.Out -match 'Usage:')

    Say ''
    Say '=== C5. attach error paths (the interactive attach itself is a maintainer step, 47) ==='
    $r = Run $Com @('mux', 'attach', 'zzzz')
    Check 'attach to a prefix nothing matches: exit 2' ($r.Code -eq 2 -and $r.Out -match "No session starts with 'zzzz'")
    $r = Run $Com @('mux', 'attach', 'ab')
    Check 'attach with a prefix under 4 characters: exit 2' ($r.Code -eq 2 -and $r.Out -match 'at least 4 characters')
    $r = Run $Com @('mux', 'attach', '--help')
    Check 'attach --help: usage text naming Ctrl+\ then d, exit 0, no cmd /c' ($r.Code -eq 0 -and $r.Out -match 'Ctrl\+\\ then d' -and $r.Out -notmatch 'cmd /c')

    Say ''
    Say '=== C6. ntilde mux kill-server ==='
    $shellB = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Shell })
    $r = Run $Com @('mux', 'kill-server')
    Check 'kill-server: "Multiplexer stopped.", exit 0' ($r.Code -eq 0 -and $r.Out -match 'Multiplexer stopped\.')
    Start-Sleep -Milliseconds 500
    Check "the daemon (pid $($daemon.Id)) has exited" (-not (Get-Process -Id $daemon.Id -ErrorAction SilentlyContinue))
    Check 'its remaining shell has exited' (-not ($shellB | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }))
    $r = Run $Com @('mux', 'ls')
    Check 'ls afterwards: "No multiplexer is running.", exit 1' ($r.Code -eq 1 -and $r.Out -match 'No multiplexer is running\.')
    $r = Run $Com @('mux', 'kill-server')
    Check 'kill-server again: exit 1' ($r.Code -eq 1)

    Say ''
    Say '=== C7. kill-server --force against a daemon this build talks to ==='
    $daemon2 = Start-Daemon
    $null = Run $Com @('mux', 'spawn-for-test', $Shell, '/q /k echo t30-session-C')
    $r = Run $Com @('mux', 'kill-server', '--force')
    Check 'kill-server --force on a compatible daemon stops it as plain kill-server does: "Multiplexer stopped.", exit 0' ($r.Code -eq 0 -and $r.Out -match 'Multiplexer stopped\.')
    Start-Sleep -Milliseconds 500
    Check "the second daemon (pid $($daemon2.Id)) and its shell have exited" (-not (Get-Process -Id $daemon2.Id -ErrorAction SilentlyContinue) -and -not (Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Shell }))
    $r = Run $Com @('mux', 'kill-server', '--bogus')
    Check 'kill-server with an unknown option: usage, exit 2' ($r.Code -eq 2 -and $r.Out -match 'Usage:')

    Say ''
    Say '=== C8 (step 48). ntilde.com passes arguments through unchanged and forwards exit codes ==='
    $r = Run $Com @('mux', 'ls', '--bogus')
    Check 'ntilde.com forwards exit 2 for a usage error' ($r.Code -eq 2)
    $r = Run $Com @('mux')
    Check 'ntilde.com mux (no verb): usage listing serve, ls [--json] [--all], kill, kill-server [--force], attach; exit 2' ($r.Code -eq 2 -and $r.Out -match 'ls \[--json\] \[--all\]' -and $r.Out -match 'kill-server \[--force\]' -and $r.Out -match 'attach <sessionId\|prefix> \[--read-only\]')
}
catch {
    $script:results.Add("FAIL  the script stopped: $_")
    Say "FAIL  the script stopped: $_"
}
finally {
    Say ''
    Say '=== Cleanup ==='
    if (Test-Path $Descriptor) { $null = Run $Com @('mux', 'kill-server', '--force') }
    foreach ($p in SandboxProcesses) { Say "stopping sandbox pid $($p.ProcessId) $($p.ExecutablePath)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Say "processes from the sandbox still running: $((SandboxProcesses).Count)"
    foreach ($p in $userBefore) {
        $now = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.ProcessId)"
        Say "the user's own pid $($p.ProcessId): $(if ($now -and $now.CreationDate -eq $p.CreationDate) { 'still running, same start time' } else { 'NOT the same any more' })"
    }
    Say ''
    Say '=== Summary ==='
    foreach ($l in $script:results) { Say "  $l" }
    $fails = @($script:results | Where-Object { $_ -like 'FAIL*' }).Count
    Say "$($script:results.Count - $fails) of $($script:results.Count) checks passed."
}
$global:LASTEXITCODE = 0
if ($fails -gt 0 -or $script:results.Count -eq 0) { exit 1 }
exit 0
