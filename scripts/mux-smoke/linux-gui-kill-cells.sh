#!/usr/bin/env bash
# Phase 5 manual checklist, step 4 on Linux, headless: kill the GUI (SIGKILL, as Task Manager's End task does), its
# shell keeps running in the multiplexer, and a relaunched GUI reattaches it; then the same with SIGTERM. The GUI runs
# under Xvfb and is never sent input. A documented manual script: CI does not run it.
#
# Usage: scripts/mux-smoke/linux-gui-kill-cells.sh <app> <sandbox>
#   <app>      the App build folder (the one holding `Ntilde`), or the `Ntilde` executable
#   <sandbox>  a folder of the script's own; it writes only <sandbox>/gui-kill
#
# Needs xvfb-run. Judges only by `ntilde mux ls`. In a container whose PID 1 reaps nothing (`sleep infinity`), the
# daemon that kill-server stops at the end stays a zombie and kill-server reports "did not stop within 5 s"; run the
# container with --init.
# check's second argument is a condition's exit status, `$(...; echo $?)`: one word, by design.
# shellcheck disable=SC2046,SC2319
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/mux-smoke/lib.sh
source "$here/lib.sh"

usage="usage: $0 <app folder or Ntilde> <sandbox>"
APP="$(resolve_exe "${1:?$usage}" Ntilde)" || exit 2
SBX="$(prepare_sandbox "${2:?$usage}/gui-kill")" || exit 2
command -v xvfb-run >/dev/null || { echo "xvfb-run is not on PATH" >&2; exit 2; }

DATA=$SBX/data
rm -rf "$DATA"
isolate "$DATA"
sandbox_settings "$DATA" false
printf 'keep' >"$DATA/mux-close-choice"
GUI=""

finish() {
  trap - EXIT
  say ''
  say '=== Cleanup ==='
  for p in $(sandbox_pids "$DATA" "^$APP \$"); do kill -9 "$p" 2>/dev/null; done
  stop_sandbox "$DATA"
  for p in $(sandbox_pids "$DATA" 'Xvfb|xvfb-run'); do kill "$p" 2>/dev/null; done
  left=$(sandbox_pids "$DATA" | tr '\n' ' ')
  say "processes of the sandbox still running: ${left:-none}"
  summary
  exit $?
}
trap finish EXIT

ls_() {
  OUT="$("$APP" mux ls 2>&1)"
  say '> Ntilde mux ls'
  while IFS= read -r l; do say "  $l"; done <<<"$OUT"
}

# start_gui: the GUI under Xvfb; waits (30 s) until its pane's session is attached. Sets GUI.
start_gui() {
  xvfb-run -a -s '-screen 0 1280x800x24' "$APP" >>"$SBX/gui.out" 2>&1 &
  local _
  for _ in $(seq 1 150); do
    GUI=$(sandbox_pids "$DATA" "^$APP \$" | head -1)
    [[ -n "$GUI" && -s "$DATA/mux/mux-endpoint.json" ]] && grep -Eq ' running +1 ' <<<"$("$APP" mux ls 2>&1)" && break
    sleep 0.2
  done
  sleep 1
  say "GUI pid ${GUI:-NONE}"
}

say "=== Phase 5 checklist step 4: Linux, kill the GUI, the shell survives, a relaunch reattaches ($(date -u '+%Y-%m-%d %H:%M:%S UTC')) ==="
say "NTILDE_APPDATA_ROOT=$NTILDE_APPDATA_ROOT; app $APP"
[[ -n "$(sandbox_pids "$DATA")" ]] && die "processes are already running under $DATA"

say ''
say '=== G1. SIGKILL ==='
start_gui
[[ -n "$GUI" ]] || die "the GUI did not start (see $SBX/gui.out)"
ls_
S1=$(grep -oE '^[0-9a-f-]{36} +running +1 ' <<<"$OUT" | awk '{print $1}' | head -1)
check 'the GUI'"'"'s pane runs in the daemon: one session, running, 1 attached' $([[ -n "$S1" ]]; echo $?) "$S1"
kill -9 "$GUI"
sleep 2
ls_
check 'after kill -9 of the GUI the session is still running, 0 attached' $([[ -n "$S1" ]] && grep -Eq "^$S1 +running +0 " <<<"$OUT"; echo $?)
start_gui
ls_
check 'a relaunched GUI reattached the same session (1 attached), and started no second one' $([[ -n "$S1" ]] && grep -Eq "^$S1 +running +1 " <<<"$OUT" && [[ $(grep -cE '^[0-9a-f-]{36} ' <<<"$OUT") == 1 ]]; echo $?)

say ''
say '=== G2. SIGTERM ==='
kill "$GUI"
for _ in $(seq 1 40); do kill -0 "$GUI" 2>/dev/null || break; sleep 0.25; done
say "GUI after SIGTERM: $(kill -0 "$GUI" 2>/dev/null && echo 'still running' || echo exited)"
sleep 2
ls_
check 'after SIGTERM to the GUI the session is still running, 0 attached' $([[ -n "$S1" ]] && grep -Eq "^$S1 +running +0 " <<<"$OUT"; echo $?)
say 'mux.log, last lines:'
tail -12 "$DATA/logs/mux.log" 2>/dev/null | while IFS= read -r l; do say "    $l"; done
