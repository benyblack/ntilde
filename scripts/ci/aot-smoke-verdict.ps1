# The verdict of the AOT gate's smoke launch (ci.yml "Smoke launch the published bundle" and
# release.yml "Smoke launch the Windows bundle (blocking)"): reads the GUI's debug.log and the
# daemon's mux.log and decides whether the bundle came up with a real shell behind it.
#
# The smoke runs on the real default (SessionPersistence KeepOnClose), so the shell is spawned
# by the multiplexer daemon (`Ntilde.exe mux serve`), not by the GUI. Rules:
#   - the UI must have come up: `[TerminalView] Theme applied`;
#   - the GUI started the daemon (`[Mux] starting the multiplexer daemon`): the daemon path must
#     then be complete, i.e. mux.log has `[MuxDaemon] serving` AND `[RustPtySession] Spawned
#     ... pid=N`. A GUI-only spawn line does NOT count here: when the daemon fails the GUI falls
#     back to a local shell, which would hide a broken AOT daemon;
#   - the GUI never started a daemon (persistence off): the GUI's own Spawned line is required.
# Exits 0 and prints the verdict, or throws (exit 1) naming what is missing.
#
# Tested by scripts/tests/aot_smoke_verdict_tests.py.
param(
  [Parameter(Mandatory)][string]$DebugLog,
  [Parameter(Mandatory)][string]$MuxLog
)
$ErrorActionPreference = 'Stop'

$spawnPattern = '\[RustPtySession\] Spawned .*pid=\d+'

function Test-Line([string]$Path, [string]$Pattern) {
  return [bool]((Test-Path -LiteralPath $Path) -and (Select-String -LiteralPath $Path -Pattern $Pattern -Quiet))
}

if (-not (Test-Line $DebugLog '\[TerminalView\] Theme applied')) {
  throw "$DebugLog has no line matching '\[TerminalView\] Theme applied', so the bundle started but its UI never finished coming up. A trimmed-away type reached only while building the terminal view looks exactly like this."
}

$guiSpawned    = Test-Line $DebugLog $spawnPattern
$daemonStarted = Test-Line $DebugLog '\[Mux\] starting the multiplexer daemon'
$daemonServing = Test-Line $MuxLog '\[MuxDaemon\] serving'
$daemonSpawned = Test-Line $MuxLog $spawnPattern

if ($daemonStarted) {
  if (-not ($daemonServing -and $daemonSpawned)) {
    throw "The GUI started the multiplexer daemon but the daemon path is incomplete: $MuxLog serving=$daemonServing, spawned a shell=$daemonSpawned (GUI-side spawn=$guiSpawned, which does not count: the GUI falls back to a local shell when the daemon fails). The AOT daemon (ntilde mux serve) is broken."
  }
  Write-Output "The GUI started the multiplexer daemon and the daemon spawned the shell."
}
elseif ($guiSpawned) {
  Write-Output "The shell was spawned in the GUI process (persistence off, no daemon started)."
}
else {
  throw "No shell spawn found: $DebugLog has no '$spawnPattern' line and the GUI never started the multiplexer daemon. The bundle started but cannot open a shell."
}
