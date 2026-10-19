#Requires -Version 7.2
<#
.SYNOPSIS
    Phase 5 manual checklist, steps 41, 27 and 43 on Windows: `ntilde mux ls --all` against the checklist's Docker sshd.

.DESCRIPTION
    The sshd is novaterm-native-ssh-e2e:v5 as t30-ssh on the t30-net network (10.230.30.0/24), published on
    127.0.0.1:2230, with a throwaway key authorized for nova and the given ntilde-mux installed. One SSH profile in the
    sandbox keeps its remote sessions: the native backend, key authentication only. The script makes t30-net and
    t30-ssh, refuses to start when either exists, and removes both at the end.

    PR #512's rulings are what it expects: a listing never starts a remote multiplexer (H7), so a host with none running
    reads "unreachable: no multiplexer is running".

    It never reads, saves or deletes a Credential Manager entry: the native backend asks the vault only at a password
    prompt, and the server accepts the throwaway key first. No OpenSSH profile is used: on Windows an automatic OpenSSH
    attempt asks the Credential Manager for a saved password (the checklist's step 42).

    Remote sessions are started (setup only) by the App's hidden spawn-for-test, run inside the container as nova on a
    .NET runtime copied from mcr.microsoft.com/dotnet/runtime:10.0 (the App's IL is the same on every OS).

    A documented manual script: it needs Docker Desktop with Linux containers, which CI's Windows runners do not have.
    Run it from a sandbox Ntilde's build, never against your real Ntilde.

.PARAMETER App
    The App build folder, the one holding Ntilde.exe.

.PARAMETER Launcher
    ntilde.com, or the Ntilde.Launcher.exe published from src/Ntilde.Launcher. Optional when the App folder holds
    ntilde.com already.

.PARAMETER Mux
    The linux ntilde-mux for the container (CI's ntilde-mux-linux-x64 artifact, or
    scripts/docker-publish-mux-daemon.sh's output).

.PARAMETER Sandbox
    A folder of the script's own; it writes only <Sandbox>\ls-all.
#>
param(
    [Parameter(Mandatory)] [string] $App,
    [string] $Launcher,
    [Parameter(Mandatory)] [string] $Mux,
    [Parameter(Mandatory)] [string] $Sandbox
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Say([string] $line) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $line) }
$script:results = [System.Collections.Generic.List[string]]::new()
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    $v = if ($ok) { 'PASS' } else { 'FAIL' }; $script:results.Add("$v  $name"); Say "$v  $name$(if ($detail) { " - $detail" })"
}
function Run([string] $exe, [string[]] $argv) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $exe @argv 2>&1 | Out-String
    $code = $LASTEXITCODE; $sw.Stop()
    Say "> $(Split-Path $exe -Leaf) $($argv -join ' ')"
    foreach ($l in ($out.TrimEnd() -split "`r?`n")) { Say "  $l" }
    Say ("  (exit {0}, {1:N1} s)" -f $code, $sw.Elapsed.TotalSeconds)
    return [pscustomobject]@{ Out = $out; Code = $code; Seconds = $sw.Elapsed.TotalSeconds }
}
# docker, failing the script on a non-zero exit. Not named `Docker`: PowerShell resolves names without regard to case,
# so a function of that name would shadow docker.exe for every `& docker` in this script, its own call included.
function Invoke-Docker([string[]] $argv) {
    $o = & docker.exe @argv 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "docker $($argv -join ' ') exited $($LASTEXITCODE): $($o.Trim())" }
    return $o.Trim()
}
function DockerLogged([string[]] $argv) {
    $o = & docker.exe @argv 2>&1 | Out-String
    Say "> docker $($argv -join ' ') (exit $LASTEXITCODE)"
    if ($o.Trim()) { foreach ($l in ($o.TrimEnd() -split "`r?`n")) { Say "  $l" } }
}
# Whether 127.0.0.1:2230 sends an SSH banner now. A refused or dropped connection, or a read that times out, is a no.
function TestSshBanner {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        if (-not $client.ConnectAsync('127.0.0.1', 2230).Wait(2000)) { return $false }
        $stream = $client.GetStream()
        $stream.ReadTimeout = 3000
        $buf = [byte[]]::new(4)
        $n = 0
        while ($n -lt 4) {
            $read = $stream.Read($buf, $n, 4 - $n)
            if ($read -le 0) { return $false }
            $n += $read
        }
        return [Text.Encoding]::ASCII.GetString($buf) -eq 'SSH-'
    }
    catch [System.IO.IOException], [System.Net.Sockets.SocketException], [System.AggregateException] {
        Write-Verbose "no SSH banner yet: $($_.Exception.Message)"
        return $false
    }
    finally { $client.Dispose() }
}
function WaitForSsh([int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (TestSshBanner) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$SshdImage = 'novaterm-native-ssh-e2e:v5'
$Ssh = 't30-ssh'
$Net = 't30-net'
$RemoteMux = '/home/nova/.local/share/ntilde/bin/ntilde-mux'
# ntilde-mux's own root beneath the app-data root (MuxPaths.Standalone), where its descriptor lives.
$RemoteMuxRoot = '/home/nova/.local/share/ntilde/ntilde-mux'
function RemoteDaemonPid { (& docker.exe exec $Ssh sh -c "pgrep -u nova -f 'ntilde-mux serve' | head -1" 2>$null | Out-String).Trim() }
function RemoteSpawn([string] $tag) {
    $id = (& docker.exe exec -u nova -e DOTNET_ROOT=/opt/mux-smoke/dotnet -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 -e "NTILDE_APPDATA_ROOT=$RemoteMuxRoot" $Ssh `
            /opt/mux-smoke/dotnet/dotnet /opt/mux-smoke/app/Ntilde.dll mux spawn-for-test /bin/sh "/home/nova/t30-hold.sh $tag" 2>&1 | Out-String).Trim()
    Say "remote setup: spawn-for-test /bin/sh t30-hold.sh $tag -> $id"
    return $id
}
# No quotes inside the shell command: PowerShell 7.2 drops a native argument's embedded double quotes.
function HostKey { (Invoke-Docker @('exec', $Ssh, 'sh', '-c', 'set -- $(cat /etc/ssh/ssh_host_ed25519_key.pub); echo $1; set -- $(ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256); echo $2')) -split "`r?`n" }

# ---- the sandbox ---------------------------------------------------------------------------------------------------
$AppSrc = (Resolve-Path -LiteralPath $App).Path
if (-not (Test-Path (Join-Path $AppSrc 'Ntilde.exe'))) { throw "no Ntilde.exe in $AppSrc" }
if (-not $Launcher) { $Launcher = Join-Path $AppSrc 'ntilde.com' }
if (-not (Test-Path -LiteralPath $Launcher)) { throw "no launcher at ${Launcher}: pass -Launcher (Ntilde.Launcher.exe from src/Ntilde.Launcher)" }
$Launcher = (Resolve-Path -LiteralPath $Launcher).Path
$MuxBin = (Resolve-Path -LiteralPath $Mux).Path
New-Item -ItemType Directory -Force $Sandbox | Out-Null
$SandboxRoot = (Resolve-Path -LiteralPath $Sandbox).Path.TrimEnd('\')
if ($SandboxRoot -eq [IO.Path]::GetPathRoot($SandboxRoot).TrimEnd('\') -or $SandboxRoot -eq $env:USERPROFILE.TrimEnd('\')) {
    throw "refusing $SandboxRoot as a sandbox: give a folder of its own"
}
$Sp = Join-Path $SandboxRoot 'ls-all'
$Data = Join-Path $Sp 'data'
$AppDir = Join-Path $Sp 'app'
$RemoteApp = Join-Path $Sp 'remote-app'
$Shells = Join-Path $Sp 'shells'
$Keys = Join-Path $Sp 'sshkey'
$Com = Join-Path $AppDir 'ntilde.com'
$Exe = Join-Path $AppDir 'Ntilde.exe'
$Shell = Join-Path $Shells 't30shell.exe'
$Descriptor = Join-Path $Data 'mux\mux-endpoint.json'
function SandboxProcesses { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Sp + '\', [StringComparison]::OrdinalIgnoreCase) }) }

$env:NTILDE_APPDATA_ROOT = $Data
Say "=== Phase 5 checklist steps 41, 27, 43: Windows ls --all cells, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') ==="
Say "NTILDE_APPDATA_ROOT=$env:NTILDE_APPDATA_ROOT"
if ((SandboxProcesses).Count) { throw "processes are already running from $Sp; stop them first" }
$userBefore = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(ntilde|ntilde-mux)(\.exe|\.com)$' -and -not ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($Sp + '\', [StringComparison]::OrdinalIgnoreCase)) })
foreach ($p in $userBefore) { Say "a process of the user's own (left alone): pid $($p.ProcessId) $($p.ExecutablePath)" }
& docker.exe image inspect $SshdImage *> $null
if ($LASTEXITCODE -ne 0) { throw "no $SshdImage image: docker build -t $SshdImage tests/Ntilde.ExternalSuites/NativeSsh" }
& docker.exe container inspect $Ssh *> $null; $sshExists = $LASTEXITCODE -eq 0
& docker.exe network inspect $Net *> $null; $netExists = $LASTEXITCODE -eq 0
if ($sshExists -or $netExists) { throw "$Ssh or $Net exists already; remove them first (docker rm -f $Ssh; docker network rm $Net)" }

foreach ($d in $AppDir, $RemoteApp, $Data, $Shells, $Keys) { if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
robocopy $AppSrc $AppDir /E /XD osx* linux* /XF *.pdb /NJH /NJS /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy $AppSrc -> $AppDir failed ($LASTEXITCODE)" }
# Only the CLI runs in the container: no GUI natives, no symbols.
robocopy $AppSrc $RemoteApp /E /XD runtimes /XF *.pdb /NJH /NJS /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy $AppSrc -> $RemoteApp failed ($LASTEXITCODE)" }
Copy-Item -LiteralPath $Launcher -Destination $Com -Force
New-Item -ItemType Directory -Force $Data, $Shells, $Keys | Out-Null
Copy-Item -LiteralPath "$env:SystemRoot\System32\cmd.exe" -Destination $Shell -Force
$settings = [ordered]@{ SessionPersistence = 'KeepOnClose'; AgentAccessObserveEnabled = $false; QuakeModeEnabled = $false; AutomaticUpdateChecks = $false }
[IO.File]::WriteAllText((Join-Path $Data 'settings.json'), ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

# This run's label on the container and the network it creates: cleanup removes only what carries it, so a run that
# lost a race to another one never removes the other's.
$runLabel = "mux-smoke.run=$PID.$(Get-Random)"
function OwnedByThisRun([string] $kind, [string] $name) {
    # {{json ...}}, not {{index ... "key"}}: PowerShell 7.2 drops the embedded quotes of a native argument.
    $format = if ($kind -eq 'container') { '{{json .Config.Labels}}' } else { '{{json .Labels}}' }
    $json = (& docker.exe $kind inspect -f $format $name 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $json -or $json -eq 'null') { return $false }
    return "mux-smoke.run=$(($json | ConvertFrom-Json).'mux-smoke.run')" -eq $runLabel
}
try {
    Say ''
    Say '=== Setup: the Docker sshd, a throwaway key, ntilde-mux on the host, one native profile ==='
    $null = Invoke-Docker @('network', 'create', '--label', $runLabel, '--driver', 'bridge', '--subnet', '10.230.30.0/24', $Net)
    $null = Invoke-Docker @('run', '-d', '--label', $runLabel, '--name', $Ssh, '--hostname', $Ssh, '--network', $Net, '-p', '127.0.0.1:2230:22', $SshdImage)
    if (-not (WaitForSsh 30)) { throw 'sshd did not answer on 127.0.0.1:2230 within 30 s' }
    # The key is made in the container (no quoting of an empty passphrase through PowerShell), authorized there, and
    # its private half copied out to the sandbox.
    $null = Invoke-Docker @('exec', $Ssh, 'sh', '-c', "ssh-keygen -q -t ed25519 -N '' -C ntilde-smoke -f /tmp/smoke_key && install -o nova -g nova -m 600 /tmp/smoke_key.pub /home/nova/.ssh/authorized_keys")
    $key = Join-Path $Keys 'id_ed25519'
    $null = Invoke-Docker @('cp', "${Ssh}:/tmp/smoke_key", $key)
    $null = Invoke-Docker @('exec', $Ssh, 'rm', '-f', '/tmp/smoke_key', '/tmp/smoke_key.pub')
    # As nova, so every folder on the way is nova's (`install -d -o nova` would leave ~/.local/share/ntilde to root).
    $null = Invoke-Docker @('exec', '-u', 'nova', $Ssh, 'mkdir', '-p', '/home/nova/.local/share/ntilde/bin')
    $null = Invoke-Docker @('cp', $MuxBin, "${Ssh}:$RemoteMux")
    $null = Invoke-Docker @('exec', $Ssh, 'sh', '-c', "chown nova:nova $RemoteMux && chmod 755 $RemoteMux")
    $null = Invoke-Docker @('exec', $Ssh, 'sh', '-c', "printf 'echo t30-session-`$1\nexec sleep 100000\n' > /home/nova/t30-hold.sh && chown nova:nova /home/nova/t30-hold.sh")
    Say "ntilde-mux on the host: $(Invoke-Docker @('exec', '-u', 'nova', $Ssh, $RemoteMux, '--version'))"
    # A .NET runtime for the remote spawn-for-test, from Microsoft's runtime image, through the sandbox.
    $rt = Join-Path $Sp 'dotnet-runtime'
    if (Test-Path $rt) { Remove-Item -LiteralPath $rt -Recurse -Force }
    $null = Invoke-Docker @('create', '--name', 'mux-smoke-dotnet', 'mcr.microsoft.com/dotnet/runtime:10.0')
    try { $null = Invoke-Docker @('cp', 'mux-smoke-dotnet:/usr/share/dotnet', $rt) } finally { & docker.exe rm -f mux-smoke-dotnet *> $null }
    $null = Invoke-Docker @('exec', $Ssh, 'mkdir', '-p', '/opt/mux-smoke')
    $null = Invoke-Docker @('cp', $rt, "${Ssh}:/opt/mux-smoke/dotnet")
    $null = Invoke-Docker @('cp', $RemoteApp, "${Ssh}:/opt/mux-smoke/app")
    # A copy through a Windows folder loses the execute bit.
    $null = Invoke-Docker @('exec', $Ssh, 'sh', '-c', 'chmod -R a+rX /opt/mux-smoke && chmod 755 /opt/mux-smoke/dotnet/dotnet')

    $sshDir = Join-Path $Data 'ssh'
    New-Item -ItemType Directory -Force $sshDir | Out-Null
    $profileId = '7a3c0f30-0000-4000-8000-000000000030'
    $store = [ordered]@{
        SchemaVersion = 1
        Profiles = @([ordered]@{
            Id = $profileId; BackendKind = 1; Name = 't30 sshd (native, key)'; GroupPath = 'General'
            Host = '127.0.0.1'; User = 'nova'; Port = 2230
            AuthMode = 2; IdentityFilePath = $key; RememberPasswordInVault = $false
            MuxOptions = [ordered]@{ Enabled = $false; PersistRemoteSessions = $true }
            ExtraSshArgs = ''
        })
    }
    $storePath = Join-Path $sshDir 'profiles.json'
    [IO.File]::WriteAllText($storePath, ($store | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $hostKey = HostKey
    $knownPath = Join-Path $sshDir 'native_known_hosts.json'
    $known = @([ordered]@{ Host = '127.0.0.1'; Port = 2230; Algorithm = $hostKey[0]; Fingerprint = $hostKey[1]; TrustedAtUtc = (Get-Date).ToUniversalTime().ToString('o') })
    [IO.File]::WriteAllText($knownPath, (ConvertTo-Json -InputObject $known -Depth 3), [Text.UTF8Encoding]::new($false))
    Say "profile ${profileId}: native backend, nova@127.0.0.1:2230, key $key, PersistRemoteSessions true, RememberPasswordInVault false"
    Say "trusted host key (sandbox native known hosts): $($hostKey -join ' ')"
    $endpointId = 'ssh:' + $profileId.Replace('-', '')

    Say ''
    Say '=== J1 (step 41.1). ls --all with a local daemon, while the host runs no ntilde-mux ==='
    $daemon = Start-Process -FilePath $Exe -ArgumentList 'mux', 'serve' -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(15); while ((Get-Date) -lt $deadline -and -not (Test-Path $Descriptor)) { Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 300
    Say "local daemon: Ntilde.exe mux serve (pid $($daemon.Id))"
    $local = (Run $Com @('mux', 'spawn-for-test', $Shell, '/q /k echo t30-local')).Out.Trim()
    Check 'the host runs no ntilde-mux before the listing' (-not (RemoteDaemonPid))
    $r = Run $Com @('mux', 'ls', '--all')
    Check 'ls --all: a HOST column, this computer''s session, then "nova@127.0.0.1  unreachable: no multiplexer is running" (H7), exit 0' ($r.Code -eq 0 -and $r.Out -match '(?m)^HOST\s+ID\s+STATE\s+ATTACHED\s+SIZE\s+TITLE' -and $r.Out -match "this computer\s+$local\s+running" -and $r.Out -match '(?m)^nova@127\.0\.0\.1\s+unreachable: no multiplexer is running\s*$')
    $r = Run $Com @('mux', 'ls', '--all', '--json')
    $j = $r.Out | ConvertFrom-Json
    $re = $j.endpoints | Where-Object { $_.endpoint -eq $endpointId }
    Check 'ls --all --json: "error":"no multiplexer is running" for the host' ($r.Code -eq 0 -and $re.error -eq 'no multiplexer is running')
    $after = RemoteDaemonPid
    Say "docker exec $Ssh pgrep -u nova -f 'ntilde-mux serve': $(if ($after) { $after } else { '(nothing)' })"
    Check 'the listing started no ntilde-mux on the host (H7)' (-not $after)

    Say ''
    Say '=== J2 (step 41.2). Two sessions on the host (setup: ntilde-mux serve, then the App''s spawn-for-test in the container) ==='
    $null = Invoke-Docker @('exec', '-d', '-u', 'nova', $Ssh, $RemoteMux, 'serve')
    $deadline = (Get-Date).AddSeconds(10)
    do { Start-Sleep -Milliseconds 200; & docker.exe exec $Ssh test -s "$RemoteMuxRoot/mux/mux-endpoint.json" } while ($LASTEXITCODE -ne 0 -and (Get-Date) -lt $deadline)
    $remoteDaemon = RemoteDaemonPid
    $ra = RemoteSpawn 'A'; $rb = RemoteSpawn 'B'
    $r = Run $Com @('mux', 'ls', '--all')
    $rows = @($r.Out -split "`r?`n" | Where-Object { $_ -match '^nova@127\.0\.0\.1\s+[0-9a-f]{8}-' })
    Check 'ls --all lists both remote sessions under nova@127.0.0.1, running, 0 attached' ($r.Code -eq 0 -and $rows.Count -eq 2 -and @($rows | Where-Object { $_ -match "($ra|$rb)\s+running\s+0\s" }).Count -eq 2)
    $r = Run $Com @('mux', 'ls', '--all', '--json')
    $j = $r.Out | ConvertFrom-Json
    $le = $j.endpoints | Where-Object { $_.endpoint -eq 'local' }; $re = $j.endpoints | Where-Object { $_.endpoint -eq $endpointId }
    Check 'ls --all --json: {"endpoints":[{"endpoint":"local","host":"this computer","sessions":[...]},{"endpoint":"ssh:<profile id>","host":"nova@127.0.0.1","sessions":[...]}]}' ($r.Code -eq 0 -and $j.endpoints[0].endpoint -eq 'local' -and $le.host -eq 'this computer' -and @($le.sessions).Count -eq 1 -and $re.host -eq 'nova@127.0.0.1' -and @($re.sessions | ForEach-Object sessionId) -contains $ra -and @($re.sessions | ForEach-Object sessionId) -contains $rb -and -not $re.error)

    Say ''
    Say '=== J3 (step 27). The host drops off the network (docker network disconnect), then comes back ==='
    DockerLogged @('network', 'disconnect', $Net, $Ssh)
    $r = Run $Com @('mux', 'ls', '--all')
    Check 'while the host is off the network: an "unreachable:" line for it, this computer still listed, exit 0' ($r.Code -eq 0 -and $r.Out -match 'nova@127\.0\.0\.1\s+unreachable: ' -and $r.Out -match "this computer\s+$local") ("{0:N1} s" -f $r.Seconds)
    Check 'the unreachable host is cut within ls --all''s 15 s per-host wait (plus a little)' ($r.Seconds -lt 25) ("{0:N1} s" -f $r.Seconds)
    DockerLogged @('network', 'connect', $Net, $Ssh)
    $back = WaitForSsh 45
    Say "sshd answers on 127.0.0.1:2230 again: $back"
    $r = Run $Com @('mux', 'ls', '--all')
    $rows = @($r.Out -split "`r?`n" | Where-Object { $_ -match '^nova@127\.0\.0\.1\s+[0-9a-f]{8}-' })
    Check 'after the host is back: the same two remote session ids, still running' ($back -and $rows.Count -eq 2 -and @($rows | Where-Object { $_ -match "($ra|$rb)\s+running" }).Count -eq 2)
    $remoteAfter = RemoteDaemonPid
    Check 'the remote ntilde-mux kept its pid across the drop' ($remoteDaemon -and $remoteAfter -eq $remoteDaemon) "before $remoteDaemon, after $remoteAfter"

    Say ''
    Say '=== J4 (step 41.3). No multiplexer on this computer ==='
    $null = Run $Com @('mux', 'kill-server')
    $r = Run $Com @('mux', 'ls', '--all')
    Check 'this computer reads "unreachable: no multiplexer is running", the remote host is still listed, exit 1' ($r.Code -eq 1 -and $r.Out -match 'this computer\s+unreachable: no multiplexer is running' -and $r.Out -match "nova@127\.0\.0\.1\s+$ra")
    $r = Run $Com @('mux', 'ls')
    Check 'plain ls never connects to the host: "No multiplexer is running.", exit 1' ($r.Code -eq 1 -and $r.Out -match 'No multiplexer is running\.' -and $r.Out -notmatch 'nova@')

    Say ''
    Say '=== J5 (step 41.4). No profile keeps its remote sessions ==='
    $store.Profiles[0].MuxOptions.PersistRemoteSessions = $false
    [IO.File]::WriteAllText($storePath, ($store | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $r = Run $Com @('mux', 'ls', '--all')
    Check 'with no profile keeping sessions the table ends "No remote hosts keep sessions."' ($r.Out -match 'No remote hosts keep sessions\.' -and $r.Out -notmatch 'nova@')
    $store.Profiles[0].MuxOptions.PersistRemoteSessions = $true
    [IO.File]::WriteAllText($storePath, ($store | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

    Say ''
    Say '=== J6 (step 43). The host''s keys change while ntilde-mux keeps running ==='
    $knownBefore = Get-Content -LiteralPath $knownPath -Raw
    $null = Invoke-Docker @('exec', $Ssh, 'sh', '-c', 'rm -f /etc/ssh/ssh_host_* && ssh-keygen -A >/dev/null && kill -HUP 1')
    if (-not (WaitForSsh 30)) { throw 'sshd did not come back after its keys changed' }
    $newKey = HostKey
    Say "host key before: $($hostKey -join ' '); now: $($newKey -join ' ')"
    Check 'the host serves a new key, and its ntilde-mux kept its pid' ($newKey[1] -ne $hostKey[1] -and (RemoteDaemonPid) -eq $remoteDaemon)
    $r = Run $Com @('mux', 'ls', '--all')
    Check 'the host reads "unreachable: signing in needs an answer, which ls --all never asks for"' ($r.Out -match 'nova@127\.0\.0\.1\s+unreachable: signing in needs an answer, which ls --all never asks for')
    Check 'no new key was trusted: the sandbox''s native known hosts are unchanged' ((Get-Content -LiteralPath $knownPath -Raw) -eq $knownBefore)

    Say ''
    Say '=== J7. What ls --all logged (logs\mux-ls-all.log, the last run only) ==='
    $lsLog = Join-Path $Data 'logs\mux-ls-all.log'
    if (Test-Path $lsLog) { foreach ($l in Get-Content $lsLog) { Say "  $l" } } else { Say '  (no log file)' }
    $all = if (Test-Path $lsLog) { Get-Content $lsLog -Raw } else { '' }
    Check 'the log names no saved-password read' ((Test-Path $lsLog) -and $all -notmatch '(?i)saved password')
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
    if (OwnedByThisRun 'container' $Ssh) { & docker.exe rm -f $Ssh *> $null; if ($LASTEXITCODE -eq 0) { Say "removed container $Ssh" } }
    if (OwnedByThisRun 'network' $Net) { & docker.exe network rm $Net *> $null; if ($LASTEXITCODE -eq 0) { Say "removed network $Net" } }
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
