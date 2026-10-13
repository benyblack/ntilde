#!/usr/bin/env bash
# Phase 5 manual checklist, steps 9 and 10 on Linux: the `ntilde mux` verbs (ls, ls --json, kill, kill-server
# [--force], attach's error paths) against a daemon started as the product starts it, and `ntilde-mux --version`.
# No GUI, no SSH, no credential store.
#
# Usage: scripts/mux-smoke/linux-cli-cells.sh <app> <sandbox> [ntilde-mux]
#   <app>         the App build folder (the one holding `Ntilde`), or the `Ntilde` executable
#   <sandbox>     a folder of the script's own; it writes only <sandbox>/cli
#   [ntilde-mux]  the standalone binary (CI's ntilde-mux-linux-x64 artifact, or scripts/docker-publish-mux-daemon.sh's
#                 output); without it the step 10 checks of ntilde-mux are skipped
#
# Every process it starts gets NTILDE_APPDATA_ROOT=<sandbox>/cli/data. Sessions are started with the hidden
# `ntilde mux spawn-for-test` (setup only). Exits 1 when any check fails.
# check's second argument is a condition's exit status, `$(...; echo $?)`: one word, by design.
# shellcheck disable=SC2046,SC2319
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/mux-smoke/lib.sh
source "$here/lib.sh"

usage="usage: $0 <app folder or Ntilde> <sandbox> [ntilde-mux]"
APP="$(resolve_exe "${1:?$usage}" Ntilde)" || exit 2
SBX="$(prepare_sandbox "${2:?$usage}/cli")" || exit 2
MUX=""
if [[ -n "${3:-}" ]]; then MUX="$(resolve_exe "$3" ntilde-mux)" || exit 2; fi

DATA=$SBX/data
DESC=$DATA/mux/mux-endpoint.json
rm -rf "$DATA" "$SBX/shells"
isolate "$DATA"
sandbox_settings "$DATA" false
hold_script "$SBX/shells"
HOLD=$SBX/shells/hold.sh

finish() {
  trap - EXIT
  say ''
  say '=== Cleanup ==='
  stop_sandbox "$DATA"
  left=$(sandbox_pids "$DATA" | tr '\n' ' ')
  say "processes of the sandbox still running: ${left:-none}"
  summary
  exit $?
}
trap finish EXIT

session_shells() { ps -o args= --ppid "$1" 2>/dev/null | grep -cE 'sleep 100000|hold\.sh'; }

say "=== Phase 5 checklist steps 9 and 10: Linux CLI cells ($(. /etc/os-release && echo "$PRETTY_NAME"), $(date -u '+%Y-%m-%d %H:%M:%S UTC')) ==="
say "NTILDE_APPDATA_ROOT=$NTILDE_APPDATA_ROOT; app $APP${MUX:+; ntilde-mux $MUX}"
[[ -n "$(sandbox_pids "$DATA")" ]] && die "processes are already running under $DATA"

say ''
say '=== C1. With no multiplexer running ==='
run mux ls
check 'ls with no daemon: "No multiplexer is running.", exit 1' $([[ $CODE == 1 ]] && has 'No multiplexer is running\.'; echo $?)
run mux ls --json
check 'ls --json with no daemon: exit 1' $([[ $CODE == 1 ]] && has 'No multiplexer is running\.'; echo $?)
run mux kill-server
check 'kill-server with no daemon: exit 1' $([[ $CODE == 1 ]] && has 'No multiplexer is running\.'; echo $?)
run mux attach abcd1234
check 'attach with no daemon: exit 2' $([[ $CODE == 2 ]] && has 'No multiplexer is running\.'; echo $?)
check 'no daemon was started by those verbs' $([[ ! -e "$DESC" && -z "$(sandbox_pids "$DATA" 'mux serve')" ]]; echo $?)
run mux version
check '`ntilde mux version` is not an App verb: usage, exit 2' $([[ $CODE == 2 ]] && has '^Usage:' && ! has 'version'; echo $?)

say ''
say '=== C2. Start the daemon the way the product does, then two sessions (spawn-for-test: setup only) ==='
start_daemon "$DESC"
check 'the daemon wrote its descriptor under the sandbox root' $([[ -n "${DPID:-}" ]] && kill -0 "$DPID"; echo $?)
# PR #512 H8: the kill path (kill-server --force against another protocol version) terminates only a process whose
# start time matches the one the descriptor recorded, so the descriptor must record one.
check 'the descriptor records the daemon'"'"'s start time (H8: the kill path needs it)' $(grep -Eq '"startTime":[1-9][0-9]*' "$DESC"; echo $?)
EP=$(grep -o '"endpoint":"[^"]*"' "$DESC" | cut -d'"' -f4)
say "endpoint $EP: $(stat -c '%a %U %F' "$EP" 2>&1); its folder $(dirname "$EP"): $(stat -c '%a %U' "$(dirname "$EP")" 2>&1)"
check 'the endpoint is a 0600 socket in a 0700 folder' $([[ "$(stat -c '%a %F' "$EP")" == '600 socket' && "$(stat -c '%a' "$(dirname "$EP")")" == 700 ]]; echo $?)
run mux ls
check 'ls with an empty daemon: "No sessions.", exit 0' $([[ $CODE == 0 ]] && has 'No sessions\.'; echo $?)
run mux spawn-for-test /bin/sh "$HOLD A"
A=$OUT
run mux spawn-for-test /bin/sh "$HOLD B"
B=$OUT
check 'spawn-for-test (setup) printed two session ids' $([[ "$A" =~ ^[0-9a-f-]{36}$ && "$B" =~ ^[0-9a-f-]{36}$ ]]; echo $?)
sleep 1
say "the daemon's children: $(ps -o pid=,ppid=,args= --ppid "${DPID:-0}" | tr '\n' ';')"
check "both sessions' shells are children of the daemon (pid ${DPID:-?})" $([[ $(session_shells "${DPID:-0}") == 2 ]]; echo $?)

say ''
say '=== C3. ntilde mux ls / ls --json ==='
run mux ls
check 'ls lists both sessions as running, 0 attached, with the header' $([[ $CODE == 0 ]] && has '^ID +STATE +ATTACHED +SIZE +TITLE' && has "^$A +running +0 " && has "^$B +running +0 "; echo $?)
run mux ls --json
check 'ls --json: {"sessions":[...]} with both ids, running, attachedClients 0' $([[ $CODE == 0 ]] && has '^\{"sessions":\[' && has "\"sessionId\":\"$A\"" && has "\"sessionId\":\"$B\"" && [[ $(grep -o '"running":true' <<<"$OUT" | wc -l) == 2 && $(grep -o '"attachedClients":0' <<<"$OUT" | wc -l) == 2 ]]; echo $?)

say ''
say '=== C4. ntilde mux kill ==='
run mux kill "$A"
check 'kill <id>: "Killed <id>.", exit 0' $([[ $CODE == 0 ]] && has "Killed $A\."; echo $?)
sleep 1
run mux ls
check 'ls no longer lists the killed session; the other stays running' $(! has "$A" && has "^$B +running"; echo $?)
check 'one session shell is left under the daemon' $([[ $(session_shells "${DPID:-0}") == 1 ]]; echo $?)
BOGUS=$(cat /proc/sys/kernel/random/uuid)
run mux kill "$BOGUS"
check 'kill of an unknown id: "No session <id>.", exit 1' $([[ $CODE == 1 ]] && has "No session $BOGUS\."; echo $?)
run mux kill not-a-guid
check 'kill with a malformed id: usage, exit 2' $([[ $CODE == 2 ]] && has '^Usage:'; echo $?)

say ''
say '=== C5. attach error paths (the interactive attach itself is a maintainer step) ==='
run mux attach zzzz
check 'attach to a prefix nothing matches: exit 2' $([[ $CODE == 2 ]] && has "No session starts with 'zzzz'"; echo $?)
run mux attach ab
check 'attach with a prefix under 4 characters: exit 2' $([[ $CODE == 2 ]] && has 'at least 4 characters'; echo $?)
run mux attach --help
check 'attach --help: usage text naming Ctrl+\ then d, exit 0, no cmd /c' $([[ $CODE == 0 ]] && has 'Ctrl\+\\ then d' && ! has 'cmd /c'; echo $?)

say ''
say '=== C6. ntilde mux kill-server ==='
run mux kill-server
check 'kill-server: "Multiplexer stopped.", exit 0' $([[ $CODE == 0 ]] && has 'Multiplexer stopped\.'; echo $?)
sleep 0.5
check "the daemon (pid ${DPID:-?}) has exited, with its shells" $(! kill -0 "${DPID:-0}" 2>/dev/null && [[ -z "$(sandbox_pids "$DATA" 'sleep 100000|mux serve')" ]]; echo $?)
run mux ls
check 'ls afterwards: "No multiplexer is running.", exit 1' $([[ $CODE == 1 ]] && has 'No multiplexer is running\.'; echo $?)
run mux kill-server
check 'kill-server again: exit 1' $([[ $CODE == 1 ]]; echo $?)

say ''
say '=== C7. kill-server --force against a daemon this build talks to ==='
start_daemon "$DESC"
run mux spawn-for-test /bin/sh "$HOLD C"
run mux kill-server --force
check 'kill-server --force on a compatible daemon: "Multiplexer stopped.", exit 0' $([[ $CODE == 0 ]] && has 'Multiplexer stopped\.'; echo $?)
sleep 0.5
check "the second daemon (pid ${DPID:-?}) and its shell have exited" $(! kill -0 "${DPID:-0}" 2>/dev/null && [[ -z "$(sandbox_pids "$DATA" 'sleep 100000|mux serve')" ]]; echo $?)
run mux kill-server --bogus
check 'kill-server with an unknown option: usage, exit 2' $([[ $CODE == 2 ]] && has '^Usage:'; echo $?)

say ''
say '=== C8. ntilde-mux --version (step 10) ==='
if [[ -z "$MUX" ]]; then
  say 'SKIP  no ntilde-mux given: its checks did not run'
else
  run_bin ntilde-mux "$MUX" --version
  check 'ntilde-mux --version: one line "ntilde-mux <version> (protocol 1-2, <rid>)", exit 0' $([[ $CODE == 0 && $(wc -l <<<"$OUT") == 1 ]] && has '^ntilde-mux [0-9][^ ]* \(protocol 1-2, linux-(x64|arm64)\)$'; echo $?)
  run_bin ntilde-mux "$MUX" --version --json
  check 'ntilde-mux --version --json: version, protocolMin 1, protocolMax 2, rid, path, exit 0' $([[ $CODE == 0 ]] && has '"version":"' && has '"protocolMin":1' && has '"protocolMax":2' && has '"rid":"linux-' && has '"path":"'; echo $?)
  run_bin ntilde-mux "$MUX" ls --all
  check 'ntilde-mux ls --all: exit 2 (--all needs the ntilde app)' $([[ $CODE == 2 ]] && has 'needs the ntilde app'; echo $?)
fi
