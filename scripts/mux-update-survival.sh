#!/usr/bin/env zsh
# Update-survival evidence for the local multiplexer on macOS and Linux (Phase 5 Task 24, spec R9/R10): a real Velopack
# update, applied while the daemon runs, in a sandbox, with the daemon's shells intact afterwards. The Windows run is
# scripts/mux-update-survival.ps1; this follows it step for step. Written for the maintainer's macOS run (zsh); the
# same script runs on Linux (install zsh first), with the notes below.
#
# Usage: scripts/mux-update-survival.sh [--sandbox DIR] [--allow-any-location] [--version-prefix P] [--skip-build]
#                                       [--leave-running | --cleanup-only]
#
#   1. AOT-publishes src/Ntilde.App as <prefix>.1 and .2 for this machine's RID (osx-arm64, osx-x64, linux-x64 or
#      linux-arm64), as release.yml does. --skip-build reuses <sandbox>/publish/<version>/.
#   2. Packs both with vpk 1.2.0 (installed into <sandbox>/tools) as packId NtildeSurvival, never NtildeApp, into
#      <sandbox>/feed, with the multiplexer protocol marker in the release notes. NtildeSurvival is the only install for
#      which the app honours NTILDE_UPDATE_SOURCE_DIR.
#   3. "Installs" .1 without touching the system: macOS takes Velopack's Portable .app (unzipped into
#      <sandbox>/install/); Linux takes the AppImage (copied into <sandbox>/install/). Both are complete Velopack
#      installs that update in place, so there is nothing to uninstall.
#   4. Starts the GUI with NTILDE_APPDATA_ROOT=<sandbox>/data, NTILDE_UPDATE_SOURCE_DIR=<sandbox>/feed and a settings
#      file with SessionPersistence=KeepOnClose. The GUI starts the daemon: on macOS from the .app's own executable; on
#      Linux as `$APPIMAGE mux serve`, in a mount of the AppImage of its own (MuxDaemonSpawner).
#   5-6. Starts two heartbeat sessions with `ntilde mux spawn-for-test` and lists them with `ntilde mux ls`.
#   7. Lets the GUI's own update check stage .2 from the local feed and stops the GUI (the daemon keeps its sessions).
#      - Linux: starts .1 again, and Velopack's startup auto-apply replaces the AppImage file and restarts the app as .2.
#      - macOS: never lets Velopack restart the app (see the macOS notes). It applies the staged .2 with
#        `UpdateMac apply --norestart` and starts .2 itself, so the startup auto-apply is NOT exercised on macOS.
#      Neither updater kills processes (spec R9).
#   8. Checks: the daemon's pid is unchanged; both heartbeats kept advancing; `ntilde mux ls` lists the same session
#      ids; the new GUI raised the "multiplexer is from the previous build" notice (Task 23), naming .2 as itself; and on
#      Linux, that the relaunched .1 is the process that decided the startup auto-apply.
#   9. Prints the no-overlap path's manual steps (Windows and Linux only: it needs a click in the in-app apply, which
#      restarts the app itself).
#  10. Cleans up in an EXIT trap, even when a step fails: `ntilde mux kill-server --force`, then the sandbox's own
#      processes, by pid only, then the install and Velopack's state named for NtildeSurvival.
#
# SAFETY. Never touches the real install or data (~/Library/Application Support/ntilde, ~/.local/share/ntilde), the
# user's own ntilde processes, daemon or sockets, or the keychain: no SSH is involved.
#   - The sandbox must be the script's own: a new or empty folder, which it marks with .ntilde-survival-sandbox on first
#     use, or a folder carrying that marker. It must lie under $TMPDIR, /tmp or /var/tmp unless --allow-any-location is
#     given, and it may never be, or contain, $HOME, the repository, a temp root or a system folder, nor overlap the
#     real data. Refusals happen before the cleanup is armed; the cleanup and every recursive delete act only on a
#     marked sandbox.
#   - Only the processes this script starts get NTILDE_APPDATA_ROOT=<sandbox>/data (the script itself does not), so
#     the daemon's socket and descriptor live there. On Linux the processes Velopack starts (the restart after an
#     apply) inherit it, as on Windows. On macOS they do NOT: Velopack 1.2.0 restarts the app with `/usr/bin/open -n`
#     (src/bins/src/shared/util_osx.rs, start_package), and LaunchServices starts it without the caller's environment,
#     so a restarted GUI would run against the real data and daemon. Hence step 7's macOS path.
#   - It stops only processes whose executable is under the sandbox or whose environment names the sandbox's data root
#     (on Linux an AppImage runs from a /tmp/.mount_* of its own), and only by pid.
#
# macOS notes:
#   - Needs the .NET SDK (global.json), the Xcode command-line tools (AOT link; iconutil and sips for the icon) and git.
#   - The app is built locally and unsigned: it carries no quarantine attribute, so Gatekeeper does not stop it.
#   - The GUI is started by running Contents/MacOS/Ntilde directly (not `open`), so the environment reaches it.
#   - `apply --norestart` is Velopack 1.2.0's own option (src/bins/src/update.rs, the apply subcommand; apply.rs starts
#     the app only `if restart`).
# Linux notes:
#   - Needs the .NET SDK, clang, the Rust toolchain (rusty_pty, rusty_ssh), squashfs-tools (mksquashfs, for the
#     AppImage), FUSE for running AppImages (fusermount, libfuse2; in Docker --device /dev/fuse --cap-add SYS_ADMIN),
#     a display for the GUI (xvfb-run, or Xvfb with DISPLAY set, in Docker or over ssh) and zsh.
#   - APPIMAGE_EXTRACT_AND_RUN=1 avoids FUSE, but then the code runs from an extracted copy in /tmp while $APPIMAGE
#     still names the file, which a real install never does; prefer FUSE so the run matches one.
#   - The Velopack channel is linux-x64 / linux-arm64, as release.yml packs and the app asks for.
#   - Velopack keeps a staged update in /var/tmp/velopack/<packId>/packages and logs to /tmp/velopack_<packId>.log; the
#     script removes both (named for NtildeSurvival) before and after the run.

emulate -L zsh
setopt err_exit pipe_fail no_unset

SANDBOX="${TMPDIR:-/tmp}/ntilde-update-survival"
PREFIX="0.12.0-survival"
SKIP_BUILD=0
LEAVE_RUNNING=0
CLEANUP_ONLY=0
ALLOW_ANY_LOCATION=0
while (( $# )); do
  case "$1" in
    --sandbox) SANDBOX="$2"; shift 2 ;;
    --version-prefix) PREFIX="$2"; shift 2 ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --leave-running) LEAVE_RUNNING=1; shift ;;
    --cleanup-only) CLEANUP_ONLY=1; shift ;;
    --allow-any-location) ALLOW_ANY_LOCATION=1; shift ;;
    *) print -u2 "unknown option: $1"; exit 2 ;;
  esac
done

REPO="${0:A:h:h}"
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS/$ARCH" in
  Darwin/arm64) RID=osx-arm64; CHANNEL=osx ;;
  Darwin/x86_64) RID=osx-x64; CHANNEL=osx ;;
  Linux/x86_64) RID=linux-x64; CHANNEL=linux-x64 ;;
  Linux/aarch64) RID=linux-arm64; CHANNEL=linux-arm64 ;;
  *) print -u2 "unsupported platform $OS/$ARCH"; exit 2 ;;
esac
if [[ "$OS" == Darwin ]] && (( LEAVE_RUNNING )); then
  print -u2 "--leave-running sets up the no-overlap path's manual steps, which need the in-app apply. On macOS that apply"
  print -u2 "restarts the app with 'open -n', which drops the sandbox's environment, so it is for Windows and Linux only."
  exit 2
fi

PACK_ID=NtildeSurvival   # never NtildeApp: that is the real install's identity; the app honours the feed only for this id
V1="$PREFIX.1"; V2="$PREFIX.2"; V3="$PREFIX.3"
S="${SANDBOX:A}"
MARKER="$S/.ntilde-survival-sandbox"
PUBLISH="$S/publish"; FEED="$S/feed"; INSTALL="$S/install"; DATA="$S/data"; TOOLS="$S/tools"; SHELLS="$S/shells"
EVIDENCE="$S/evidence"
if [[ "$OS" == Darwin ]]; then
  REAL_DATA="$HOME/Library/Application Support/ntilde"
  APP="$INSTALL/$PACK_ID.app"
  EXE="$APP/Contents/MacOS/Ntilde"
else
  REAL_DATA="${XDG_DATA_HOME:-$HOME/.local/share}/ntilde"
  APP="$INSTALL/$PACK_ID.AppImage"
  EXE="$APP"
fi

# ---- the sandbox must be this script's own (ruling R4) ------------------------------------------------------------------
# Everything here runs before anything is written and before the cleanup is armed: a refusal changes nothing.

refuse() { print -u2 "REFUSED: $*"; exit 2; }
inside() { [[ "$1/" == "$2"/* ]]; }   # inside <child> <parent>: the child is the parent or lies under it

TEMP_ROOTS=("${${TMPDIR:-/tmp}:A}" "${/tmp:A}" "${/var/tmp:A}")
for real in "$REAL_DATA"; do
  if inside "$S" "$real" || inside "$real" "$S"; then refuse "the sandbox $S overlaps $real, which this script must never touch"; fi
done
for wide in "$HOME" "$REPO" / /usr /bin /etc /System /Library /Applications /var /private $TEMP_ROOTS; do
  if inside "$wide" "$S"; then refuse "the sandbox $S is, or contains, $wide; choose a folder of its own (for example under \$TMPDIR)"; fi
done
if (( ! ALLOW_ANY_LOCATION )); then
  under_temp=0
  for root in $TEMP_ROOTS; do if inside "$S" "$root" && [[ "$S" != "$root" ]]; then under_temp=1; fi; done
  (( under_temp )) || refuse "the sandbox $S is not under ${TEMP_ROOTS[*]}; choose a folder there, or pass --allow-any-location"
fi
[[ "$S" != *[[:space:]]* ]] || refuse "the sandbox path must not contain whitespace: $S"
[[ ! -f "$S" ]] || refuse "the sandbox $S is a file"
if [[ -d "$S" && -n "$(ls -A "$S")" && ! -f "$MARKER" ]]; then
  refuse "$S is not empty and does not carry ${MARKER:t}: it is not this script's sandbox; choose a new or empty folder"
fi
if [[ ! -f "$MARKER" ]]; then
  if (( CLEANUP_ONLY )); then print "$S is not a sandbox of this script's yet: there is nothing to clean up."; exit 0; fi
  mkdir -p "$S"
  print "A sandbox of scripts/mux-update-survival.sh. Anything in this folder may be deleted by it." > "$MARKER"
fi

mkdir -p "$EVIDENCE"
LOG="$EVIDENCE/survival-run-$(date +%Y%m%d-%H%M%S).log"

say() { local line="[$(date +%H:%M:%S)] $*"; print -r -- "$line"; print -r -- "$line" >> "$LOG"; }
section() { say ""; say "=== $* ==="; }
CHECKS_PASSED=0; CHECKS_FAILED=0; SUMMARY=(); RUN_FAILURE_RECORDED=0
check() {  # check <name> <1 ok|0> [detail]
  local verdict=FAIL
  if [[ "$2" == 1 ]]; then verdict=PASS; (( CHECKS_PASSED += 1 )); else (( CHECKS_FAILED += 1 )); fi
  SUMMARY+=("$verdict  $1")
  say "$verdict  $1${3:+ - $3}"
}
die() { say "ERROR: $*"; check 'the run completed' 0 "$*"; RUN_FAILURE_RECORDED=1; exit 1; }

# Recursive deletes of the script's fixed-name folders: only inside a marked sandbox, and never the sandbox itself.
remove_in_sandbox() {  # remove_in_sandbox <path>
  local p="${1:A}"
  [[ -f "$MARKER" ]] || { say "refusing to delete $p: $S is not a marked sandbox"; return 1; }
  if [[ "$p" == "$S" ]] || ! inside "$p" "$S"; then say "refusing to delete $p: it is not inside the sandbox $S"; return 1; fi
  rm -rf -- "$p"
}

# Everything the sandbox runs gets its root and its feed; this script itself does not, so it never looks like one of
# the sandbox's processes.
sandboxed() { NTILDE_APPDATA_ROOT="$DATA" NTILDE_UPDATE_SOURCE_DIR="$FEED" "$@"; }
ntilde() { sandboxed "$EXE" "$@"; }
# The GUI, in the background, with its pid in GUI_PID. Not through sandboxed(): a function started with & runs in a
# subshell, and $! would be that subshell instead of the app.
start_gui() {  # start_gui <output file>
  NTILDE_APPDATA_ROOT="$DATA" NTILDE_UPDATE_SOURCE_DIR="$FEED" "$EXE" > "$1" 2>&1 &
  GUI_PID=$!
}

# ---- the sandbox's own processes ---------------------------------------------------------------------------------------

exe_of() {
  if [[ "$OS" == Linux ]]; then readlink "/proc/$1/exe" 2>/dev/null || true
  else ps -o comm= -p "$1" 2>/dev/null || true; fi
}
alive() { kill -0 "$1" 2>/dev/null; }
ours() {
  local exe; exe="$(exe_of "$1")"
  [[ "$exe" == "$S"/* ]] && return 0
  if [[ "$OS" == Linux && -r "/proc/$1/environ" ]]; then
    tr '\0' '\n' < "/proc/$1/environ" 2>/dev/null | grep -qxF "NTILDE_APPDATA_ROOT=$DATA" && return 0
  fi
  return 1
}
# Loops below use `if`, not `&&`: under err_exit and pipe_fail, a loop whose last test failed fails its pipeline.
sandbox_pids() {
  local p exe
  if [[ "$OS" == Linux ]]; then
    for p in /proc/[0-9]*(N); do p="${p:t}"; if ours "$p"; then print "$p"; fi; done
  else
    ps -axo pid=,comm= | while read -r p exe; do if [[ "$exe" == "$S"/* ]]; then print "$p"; fi; done
  fi
  return 0
}
stop_ours() {  # stop_ours <pid> <why>
  alive "$1" || return 0
  if ! ours "$1"; then say "REFUSED to stop pid $1 ($(exe_of "$1")): not the sandbox's"; return 0; fi
  say "stopping pid $1 ($(exe_of "$1")): $2"
  kill -TERM "$1" 2>/dev/null || true
  for _ in {1..20}; do alive "$1" || return 0; sleep 0.25; done
  kill -KILL "$1" 2>/dev/null || true
}

# Velopack's own state outside the sandbox, named for the packId: on Linux its packages folder
# (/var/tmp/velopack/<packId>, where a staged update waits) and its log (/tmp/velopack_<packId>.log); on macOS the
# same under ~/Library (~/Library/Caches/velopack/<packId>/packages). Anything named for this run's packId is the run's
# own; the sandbox itself is never part of it.
velopack_state() {
  local root
  {
    for root in /var/tmp/velopack /tmp "${TMPDIR:-/tmp}" "$HOME/Library/Caches" "$HOME/Library/Logs" "$HOME/.cache" "$HOME/.local/share/velopack"; do
      if [[ -d "$root" ]]; then find "$root" -maxdepth 3 -name "*$PACK_ID*" -not -path "$S" -not -path "$S/*" -not -path "*/.mount_*" 2>/dev/null || true; fi
    done
  } | sort -u
  return 0
}
remove_velopack_state() {  # remove_velopack_state <why>
  local p
  for p in ${(f)"$(velopack_state)"}; do
    if [[ "$p" != *"$PACK_ID"* || ! -e "$p" ]]; then continue; fi   # a file inside a folder already removed
    if inside "$S" "$p"; then say "refusing to delete $p: it is, or contains, the sandbox"; continue; fi
    if [[ -f "$p" && "$p" == *.log ]]; then cp "$p" "$EVIDENCE/${p:t}" 2>/dev/null || true; fi
    say "removing Velopack's $p ($1)"
    rm -rf -- "$p"
  done
  return 0
}

descriptor_pid() {
  [[ -f "$DATA/mux/mux-endpoint.json" ]] || return 0
  grep -oiE '"pid" *: *[0-9]+' "$DATA/mux/mux-endpoint.json" | grep -oE '[0-9]+$' || true
}
session_ids() { ntilde mux ls --json | grep -oiE '"sessionId" *: *"[0-9a-f-]+"' | grep -oE '[0-9a-f-]{36}' | sort; }

cleanup() {
  local rc=$?
  if [[ "${LEFT_RUNNING:-0}" == 1 ]]; then
    say "LEFT RUNNING. When done: scripts/mux-update-survival.sh --sandbox $S --cleanup-only"
    return
  fi
  # An abort the run did not record itself (a failed command under err_exit) still fails the run.
  if (( rc != 0 && ! RUN_FAILURE_RECORDED )); then check 'the run completed' 0 "the script stopped with exit code $rc"; fi
  section 'Cleanup'
  if [[ ! -f "$MARKER" ]]; then say "$S carries no ${MARKER:t}: not this script's sandbox, so nothing is cleaned up"; return; fi
  local d p left
  d="$(descriptor_pid)"
  if [[ -n "$d" ]] && alive "$d" && ours "$d"; then
    say "the sandbox daemon (pid $d) is running: ntilde mux kill-server --force"
    if [[ -e "$EXE" ]]; then ntilde mux kill-server --force >> "$LOG" 2>&1 || true; fi
    if alive "$d"; then stop_ours "$d" 'the daemon outlived kill-server'; fi
  fi
  for _ in 1 2 3; do
    left=($(sandbox_pids))
    (( ${#left} )) || break
    for p in $left; do stop_ours "$p" 'left running by the run'; done
    sleep 1
  done
  say "processes of the sandbox still running: $(sandbox_pids | wc -l | tr -d ' ')"
  if [[ -e "$INSTALL" ]]; then say "removing the sandbox install $INSTALL"; remove_in_sandbox "$INSTALL" || true; fi
  remove_velopack_state 'the run made it'
  local leftover; leftover="$(velopack_state | tr '\n' ' ')"
  if [[ -z "${leftover// /}" ]]; then check "no Velopack state for $PACK_ID is left outside the sandbox" 1
  else check "no Velopack state for $PACK_ID is left outside the sandbox" 0 "$leftover"; fi
  if [[ -n "${USER_PIDS:-}" ]]; then
    local intact=1
    for p in ${=USER_PIDS}; do alive "$p" || intact=0; done
    check "the user's own ntilde processes are untouched" "$intact" "$USER_PIDS"
  fi
  section 'Summary'
  for line in $SUMMARY; do say "  $line"; done
  say "$CHECKS_PASSED of $(( CHECKS_PASSED + CHECKS_FAILED )) checks passed. Evidence: $EVIDENCE"
  (( CHECKS_FAILED == 0 && rc == 0 )) || exit 1
}

wait_for() {  # wait_for <seconds> <command...>: polls every half second
  local deadline=$(( SECONDS + $1 )); shift
  while (( SECONDS < deadline )); do "$@" && return 0; sleep 0.5; done
  return 1
}

# Heartbeat lines are "<n> <epoch seconds>". Prints: the longest gap (s) between consecutive beats in [from, to], the
# beats before from, the beats after to, and the beats in all.
measure_beats() {  # measure_beats <file> <from> <to>
  awk -v from="$2" -v to="$3" '
    { t = $2 + 0; if (t < from) before++; if (t > to) after++;
      if (NR > 1 && !(t < from || prev > to) && t - prev > max) max = t - prev; prev = t }
    END { printf "%d %d %d %d\n", max + 0, before + 0, after + 0, NR }' "$1"
}

# ---- the run -------------------------------------------------------------------------------------------------------------

say "mux-update-survival: sandbox $S (marked), builds $V1 and $V2, packId $PACK_ID, $RID"

if (( CLEANUP_ONLY )); then
  trap cleanup EXIT
  exit 0
fi

# Refusals that must change nothing: before the EXIT trap below, so no cleanup is armed for them (ruling R4).
[[ ! -e "$INSTALL" ]] || refuse "$INSTALL exists: an earlier run left it; clean it up with --cleanup-only first"
(( $(sandbox_pids | wc -l) == 0 )) || refuse 'processes are already running from the sandbox; clean up with --cleanup-only first'

trap cleanup EXIT
trap 'exit 130' INT TERM

section 'Safety audit'
USER_PIDS="$( { pgrep -x Ntilde 2>/dev/null || true; } | while read -r p; do ours "$p" || print -n "$p "; done )"
say "the sandbox carries ${MARKER:t}"
say "real data $REAL_DATA exists: $([[ -e "$REAL_DATA" ]] && echo yes || echo no) (never touched)"
say "the user's own ntilde processes (left alone): ${USER_PIDS:-none}"
say "isolation: the daemon's socket and descriptor follow NTILDE_APPDATA_ROOT ($DATA/mux; \$XDG_RUNTIME_DIR/ntilde-mux-<user>-<hash of the root> when that path is too long for a socket)"
say "isolation: on Unix the agent host's socket is under the data root too; the sandbox settings keep it off anyway"
# A staged update left by an earlier run would be applied at the first launch, before there is a daemon to keep.
existing="$(velopack_state | tr '\n' ' ')"
say "Velopack state for $PACK_ID outside the sandbox before the run: ${existing:-none}"
if [[ -n "${existing// /}" ]]; then remove_velopack_state 'an earlier run left it'; fi

if (( ! SKIP_BUILD )); then
  section '1. Build'
  for v in $V1 $V2; do
    say "AOT-publishing src/Ntilde.App as $v ($RID); log $EVIDENCE/publish-$v.log"
    if [[ -e "$PUBLISH/$v" ]]; then remove_in_sandbox "$PUBLISH/$v"; fi
    "$REPO/scripts/build.sh" publish "$REPO/src/Ntilde.App/Ntilde.App.csproj" -c Release -r "$RID" --self-contained true \
      -p:PublishAot=true -p:SkipCliShim=true "-p:Version=$v" "-p:InformationalVersion=$v" -o "$PUBLISH/$v" > "$EVIDENCE/publish-$v.log" 2>&1 \
      || die "the AOT publish of $v failed; see $EVIDENCE/publish-$v.log"
    rm -rf "$PUBLISH/$v"/*.pdb(N) "$PUBLISH/$v"/*.dbg(N) "$PUBLISH/$v"/*.dSYM(N)   # as release.yml strips them
  done
fi
for v in $V1 $V2; do [[ -x "$PUBLISH/$v/Ntilde" ]] || die "$PUBLISH/$v/Ntilde is missing; run without --skip-build"; done

section '2. Pack'
VPK="$TOOLS/vpk"
if [[ ! -x "$VPK" ]]; then
  say "installing vpk 1.2.0 into $TOOLS (a tool path, not a global tool)"
  dotnet tool install vpk --version 1.2.0 --tool-path "$TOOLS" > "$EVIDENCE/vpk-install.log" 2>&1 || die 'dotnet tool install vpk failed'
fi
RANGE="$(bash "$REPO/scripts/ci/mux-protocol-range.sh")" || die 'scripts/ci/mux-protocol-range.sh failed'
say "protocol range: $RANGE"
if [[ -e "$FEED" ]]; then remove_in_sandbox "$FEED"; fi
mkdir -p "$FEED"
ICON="$REPO/src/Ntilde.App/Assets/ntilde_icon.png"
if [[ "$OS" == Darwin ]]; then
  ICON="$S/ntilde_icon.icns"
  [[ -f "$ICON" ]] || "$REPO/packaging/macos/make-icns.sh" "$REPO/src/Ntilde.App/Assets/ntilde_icon.png" "$ICON" >> "$LOG" 2>&1 || die 'make-icns.sh failed'
fi
pack() {  # pack <version> <publish dir> <range>
  local notes="$S/release-notes-$1.md"
  printf '<!-- ntilde-mux-protocol: %s -->\n' "$3" > "$notes"
  local args=(pack --packId "$PACK_ID" --packVersion "$1" --packDir "$2" --mainExe Ntilde --packTitle "$PACK_ID"
    --packAuthors benyblack --icon "$ICON" --releaseNotes "$notes" --outputDir "$FEED")
  if [[ "$OS" == Darwin ]]; then args+=(--bundleId com.benyblack.NtildeSurvival --exclude 'Ntilde\.dSYM')
  else args+=(--channel "$CHANNEL" --exclude '.*\.pdb|Ntilde\.dbg'); fi
  say "vpk ${args[*]}"
  "$VPK" "${args[@]}" > "$EVIDENCE/vpk-pack-$1.log" 2>&1 || die "vpk pack $1 failed; see $EVIDENCE/vpk-pack-$1.log"
}
pack $V1 "$PUBLISH/$V1" "$RANGE"
mkdir -p "$INSTALL"
if [[ "$OS" == Darwin ]]; then
  portable=("$FEED"/*Portable.zip(N))
  (( ${#portable} )) || die 'vpk pack produced no Portable.zip'
  ditto -x -k "$portable[1]" "$INSTALL"
  [[ -x "$EXE" ]] || die "the Portable zip did not hold $EXE"
else
  images=("$FEED"/*.AppImage(N))
  (( ${#images} )) || die 'vpk pack produced no AppImage (is squashfs-tools installed?)'
  cp "$images[1]" "$APP"; chmod +x "$APP"
fi
pack $V2 "$PUBLISH/$V2" "$RANGE"
say "feed: $(ls "$FEED" | tr '\n' ' ')"
if grep -q "ntilde-mux-protocol: $RANGE" "$FEED"/releases.*.json; then check 'the feed carries the protocol marker' 1; else check 'the feed carries the protocol marker' 0; fi

section '3-4. Start the first build'
say "NTILDE_APPDATA_ROOT=$DATA NTILDE_UPDATE_SOURCE_DIR=$FEED for every process the sandbox runs"
for dir in "$DATA" "$SHELLS"; do if [[ -e "$dir" ]]; then remove_in_sandbox "$dir"; fi; done
mkdir -p "$DATA" "$SHELLS"
cat > "$SHELLS/heartbeat.sh" <<'EOF'
#!/bin/sh
# A heartbeat: one line a second, "<n> <epoch seconds>", into the file named by $1. Started by ntilde mux spawn-for-test.
n=0
while :; do n=$((n + 1)); echo "$n $(date +%s)" >> "$1"; sleep 1; done
EOF
chmod +x "$SHELLS/heartbeat.sh"
cat > "$DATA/settings.json" <<'EOF'
{
  "SessionPersistence": "KeepOnClose",
  "AutomaticUpdateChecks": true,
  "AgentAccessObserveEnabled": false,
  "QuakeModeEnabled": false
}
EOF
printf keep > "$DATA/mux-close-choice"
DEBUG_LOG="$DATA/logs/debug.log"
start_gui "$EVIDENCE/gui-$V1.out"
GUI1=$GUI_PID
say "GUI pid $GUI1: $EXE"
daemon_up() { local d; d="$(descriptor_pid)"; [[ -n "$d" ]] && alive "$d"; }
wait_for 90 daemon_up || die "no daemon came up under $DATA within 90 s"
DAEMON="$(descriptor_pid)"
say "daemon pid $DAEMON: $(exe_of "$DAEMON")"
# The hook is honoured only for an install packed under the verification id (ruling R1): this line proves it is active.
if wait_for 30 grep -q 'Update source: the local directory' "$DEBUG_LOG"; then
  check "the GUI logged at startup that the update-source hook is active for the $PACK_ID install" 1 "$(grep -m1 'Update source:' "$DEBUG_LOG")"
else check "the GUI logged at startup that the update-source hook is active for the $PACK_ID install" 0 "$(grep -m1 'Update source:' "$DEBUG_LOG" 2>/dev/null || true)"; fi

section '5. Start two heartbeat sessions (ntilde mux spawn-for-test)'
HB_IDS=()
for n in 1 2; do
  id="$(ntilde mux spawn-for-test /bin/sh "$SHELLS/heartbeat.sh $SHELLS/hb$n.txt")" || die "spawn-for-test failed"
  say "ntilde mux spawn-for-test /bin/sh \"$SHELLS/heartbeat.sh $SHELLS/hb$n.txt\" -> $id"
  HB_IDS+=("$id")
done
beating() { [[ -f "$SHELLS/hb1.txt" && -f "$SHELLS/hb2.txt" ]] && (( $(wc -l < "$SHELLS/hb1.txt") >= 3 && $(wc -l < "$SHELLS/hb2.txt") >= 3 )); }
if wait_for 30 beating; then check 'both heartbeats are writing' 1; else check 'both heartbeats are writing' 0; fi

section '6. ntilde mux ls'
ntilde mux ls | while IFS= read -r line; do say "  $line"; done
IDS_BEFORE="$(session_ids)"
both=1; for id in $HB_IDS; do print -r -- "$IDS_BEFORE" | grep -qx "$id" || both=0; done
check 'ntilde mux ls lists both heartbeat sessions' "$both" "$(print -r -- "$IDS_BEFORE" | tr '\n' ' ')"

section '7. Stage the second build, then apply it'
wait_for 120 grep -q "Update $V2 downloaded" "$DEBUG_LOG" || die "$V2 was not staged within 120 s"
check "the GUI's own update check staged $V2" 1 "$(grep -m1 "Update $V2 downloaded" "$DEBUG_LOG")"
say "stopping the GUI (pid $GUI1); the daemon keeps its sessions"
stop_ours "$GUI1" 'the first launch is done'
wait "$GUI1" 2>/dev/null || true
mv "$DEBUG_LOG" "$EVIDENCE/debug-$V1.log"   # every line read from debug.log from now on is the second build's
if alive "$DAEMON"; then check "the daemon outlived the GUI's exit" 1 "pid $DAEMON"; else check "the daemon outlived the GUI's exit" 0 "pid $DAEMON"; fi

SEEN_DOWN=0
watch_until_next_gui() {  # samples the daemon until the next GUI opens its log, at most 180 s
  local deadline=$(( SECONDS + 180 ))
  while (( SECONDS < deadline )); do
    alive "$DAEMON" || SEEN_DOWN=1
    [[ -f "$DEBUG_LOG" ]] && return 0
    sleep 0.25
  done
  return 1
}
VELOPACK_LOG="/tmp/velopack_$PACK_ID.log"
T0="$(date +%s)"
if [[ "$OS" == Darwin ]]; then
  # Never a Velopack restart on macOS: `open -n` would start .2 without the sandbox's environment.
  say "macOS: the startup auto-apply is NOT exercised here. Velopack 1.2.0 restarts the app with 'open -n', which drops"
  say "the sandbox's environment, so the staged $V2 is applied with 'UpdateMac apply --norestart' and started by this script."
  staged=("$HOME/Library/Caches/velopack/$PACK_ID/packages/"*"$V2"*-full.nupkg(N))
  (( ${#staged} )) || die "no staged $V2 package under ~/Library/Caches/velopack/$PACK_ID/packages"
  "$APP/Contents/MacOS/UpdateMac" apply --norestart --package "$staged[1]" --log "$EVIDENCE/updatemac-apply.log" \
    >> "$LOG" 2>&1 || die "UpdateMac apply --norestart failed; see $EVIDENCE/updatemac-apply.log"
  sq="$(cat "$APP/Contents/MacOS/sq.version" "$APP/Contents/Resources/sq.version" 2>/dev/null || true)"
  if [[ "$sq" == *"<version>$V2</version>"* ]]; then check "the .app's sq.version is $V2" 1; else check "the .app's sq.version is $V2" 0; fi
  start_gui "$EVIDENCE/gui-$V2.out"
  GUI2=$GUI_PID
  say "started the second build (pid $GUI2) with the sandbox's environment"
  watch_until_next_gui || say 'no GUI started within 180 s'
else
  velopack_lines_before=0
  if [[ -f "$VELOPACK_LOG" ]]; then velopack_lines_before=$(wc -l < "$VELOPACK_LOG" | tr -d ' '); fi
  start_gui "$EVIDENCE/gui-relaunch.out"
  GUI2=$GUI_PID
  say "started the first build's GUI again (pid $GUI2): Velopack's startup auto-apply should apply $V2"
  # The relaunch exits inside Velopack's startup, before it opens a log; the next debug.log is the restarted GUI's.
  watch_until_next_gui || say 'no GUI started within 180 s'
fi
T1="$(date +%s)"
say "from the apply to the next GUI: about $(( T1 - T0 )) s; the daemon $( (( SEEN_DOWN )) && echo WAS || echo 'was never') seen down"

section '8. Verify'
if [[ "$OS" == Linux ]]; then
  # The apply came from the relaunch's startup auto-apply: that very process decided it, and UpdateNix waited for it.
  apply_log="$(tail -n +$(( velopack_lines_before + 1 )) "$VELOPACK_LOG" 2>/dev/null || true)"
  print -r -- "$apply_log" > "$EVIDENCE/velopack-apply.log"
  auto="$(print -r -- "$apply_log" | grep -m1 -E "\[lib-csharp:$GUI2\].*Auto apply is true" || true)"
  waited="$(print -r -- "$apply_log" | grep -m1 -E "WaitPid\($GUI2\)|--waitPid $GUI2( |\$)" || true)"
  if [[ -n "$auto" ]]; then check "the first build's relaunch (pid $GUI2) decided the startup auto-apply" 1 "$auto"
  else check "the first build's relaunch (pid $GUI2) decided the startup auto-apply" 0; fi
  if [[ -n "$waited" ]]; then check "the updater's apply waited for that pid" 1 "$waited"
  else check "the updater's apply waited for that pid" 0; fi
fi
sleep 6
if alive "$DAEMON" && (( ! SEEN_DOWN )); then check "the daemon's pid is unchanged, and it never went down" 1 "pid $DAEMON"
else check "the daemon's pid is unchanged, and it never went down" 0 "pid $DAEMON"; fi
for n in 1 2; do
  read -r gap before after count <<< "$(measure_beats "$SHELLS/hb$n.txt" $(( T0 - 5 )) $(( T1 + 2 )))"
  if (( before > 0 && after >= 3 && gap <= 3 )); then ok=1; else ok=0; fi
  check "heartbeat $n kept advancing across the apply" $ok "$count beats; the longest gap ${gap} s; $after beats after the new GUI"
done
IDS_AFTER="$(session_ids)"
missing="$(comm -23 <(print -r -- "$IDS_BEFORE") <(print -r -- "$IDS_AFTER"))"
# Self-contained: an empty list before, or one without both heartbeats, fails rather than passing vacuously.
same=0
if [[ -n "$IDS_BEFORE" && -z "$missing" ]] && (( both )); then same=1; fi
check 'after the restart, ntilde mux ls lists the same session ids' "$same" "before: $(print -r -- "$IDS_BEFORE" | tr '\n' ' '); after: $(print -r -- "$IDS_AFTER" | tr '\n' ' ')"
if wait_for 60 grep -q 'is from another build' "$DEBUG_LOG"; then
  notice="$(grep -m1 'is from another build' "$DEBUG_LOG")"
  check "the second build's GUI offered to restart the previous build's multiplexer" 1 "$notice"
  if [[ "$notice" == *"this is $V2)"* ]]; then check "the restarted GUI is $V2" 1; else check "the restarted GUI is $V2" 0 "$notice"; fi
else
  check "the second build's GUI offered to restart the previous build's multiplexer" 0 'not raised within 60 s'
fi
cp "$DEBUG_LOG" "$EVIDENCE/debug-$V2.log"

section '9. No-overlap path: NOT RUN by this script (manual steps, Windows and Linux only)'
say 'Only the in-app apply reads the staged marker, so only it takes the confirm-and-shutdown path; it is a click. It'
say "restarts the app itself, and on macOS Velopack 1.2.0 does that with 'open -n', which drops the sandbox's environment:"
say 'so these steps are for Windows and Linux only.'
say "  1. Re-run with --skip-build --leave-running: after step 8 it packs $V3 (the $V2 build, marker 3-3) and stops."
say "  2. In the sandbox window: palette, 'Check for updates'; then 'Restart to update', and confirm the question."
say "  3. Expected: the daemon's pid exits, the heartbeats stop, the restarted GUI starts a fresh daemon; debug.log has"
say "     '[MainWindow] the update closes the multiplexer: protocol 1-2, the new build's 3-3; ...'."
say "  4. scripts/mux-update-survival.sh --sandbox $S --cleanup-only"
if (( LEAVE_RUNNING )); then
  pack $V3 "$PUBLISH/$V2" 3-3
  LEFT_RUNNING=1
fi
