#!/usr/bin/env bash
# Phase 5 manual checklist, steps 41, 42, 27 and 43 on Linux: `ntilde mux ls --all` against the checklist's Docker sshd
# (novaterm-native-ssh-e2e:v5 as t30-ssh on the t30-net network, published on 127.0.0.1:2230), with three SSH profiles
# that keep their remote sessions: native with the throwaway key, OpenSSH with it, and OpenSSH through a jump host.
#
# Usage: scripts/mux-smoke/linux-ls-all-cells.sh <app> <ntilde-mux> <sandbox>
#   <app>         the App build folder (the one holding `Ntilde`), or the `Ntilde` executable
#   <ntilde-mux>  the linux binary for the sshd container: CI's ntilde-mux-linux-x64 artifact (`chmod +x` it), or
#                 scripts/docker-publish-mux-daemon.sh's. The image is Debian 12, so a build that needs a glibc above
#                 2.36 does not start there.
#   <sandbox>     a folder of the script's own; it writes only <sandbox>/ls-all
#
# Needs docker (the image built: docker build -t novaterm-native-ssh-e2e:v5 tests/Ntilde.ExternalSuites/NativeSsh),
# ssh and ssh-keygen, and the .NET runtime the App runs on (DOTNET_ROOT, or the dotnet on PATH): a copy of it and of the
# App go into the container, where the App's hidden spawn-for-test starts the remote sessions (setup only).
# Makes t30-net and t30-ssh, refuses to start when either exists, and removes both at the end.
#
# PR #512's rulings are what it expects: a listing never starts a remote multiplexer (H7), so a host with none running
# reads "unreachable: no multiplexer is running"; a listing forwards no agent (H3; no agent is offered here at all).
# The credential store is out of reach (lib.sh's isolate), and every profile signs in with the key only.
# check's second argument is a condition's exit status, `$(...; echo $?)`: one word, by design.
# shellcheck disable=SC2046,SC2319
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/mux-smoke/lib.sh
source "$here/lib.sh"

usage="usage: $0 <app folder or Ntilde> <ntilde-mux> <sandbox>"
APP="$(resolve_exe "${1:?$usage}" Ntilde)" || exit 2
MUX="$(resolve_exe "${2:?$usage}" ntilde-mux)" || exit 2
SBX="$(prepare_sandbox "${3:?$usage}/ls-all")" || exit 2

DATA=$SBX/data
DESC=$DATA/mux/mux-endpoint.json
KEYS=$SBX/sshkey
rm -rf "$DATA" "$SBX/shells"
isolate "$DATA"
sandbox_settings "$DATA" false
hold_script "$SBX/shells"

finish() {
  trap - EXIT
  say ''
  say '=== Cleanup ==='
  stop_sandbox "$DATA"
  sshd_down
  left=$(sandbox_pids "$DATA" | tr '\n' ' ')
  say "processes of the sandbox still running: ${left:-none}"
  summary
  exit $?
}
trap finish EXIT

remote_rows() { grep -cE "^nova@$SSHD_HOST +[0-9a-f]{8}-" <<<"$OUT"; }

say "=== Phase 5 checklist steps 41, 42, 27, 43: Linux ls --all cells ($(. /etc/os-release && echo "$PRETTY_NAME"), $(date -u '+%Y-%m-%d %H:%M:%S UTC')) ==="
say "NTILDE_APPDATA_ROOT=$NTILDE_APPDATA_ROOT; app $APP; ntilde-mux $MUX"
[[ -n "$(sandbox_pids "$DATA")" ]] && die "processes are already running under $DATA"

say ''
say '=== Setup: the Docker sshd, a throwaway key, ntilde-mux on the host, three profiles ==='
sshd_up "$KEYS" "$MUX"
remote_runtime_install "$(dirname "$APP")"
HOSTKEY=$(sshd_host_key)
read -r KEYALG KEYFP <<<"$HOSTKEY"
mkdir -p "$DATA/ssh"
printf '[{"Host":"%s","Port":%s,"Algorithm":"%s","Fingerprint":"%s","TrustedAtUtc":"%s"}]\n' \
  "$SSHD_HOST" "$SSHD_PORT" "$KEYALG" "$KEYFP" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$DATA/ssh/native_known_hosts.json"
P_NATIVE=7a3c0f30-0000-4000-8000-0000000000a1
P_OPENSSH=7a3c0f30-0000-4000-8000-0000000000b2
P_JUMP=7a3c0f30-0000-4000-8000-0000000000c3
write_profiles() { # write_profiles <true|false>: PersistRemoteSessions on every profile
  # OpenSSH reads ~/.ssh/known_hosts unless told otherwise (the checklist's setup); the extra argument keeps it in
  # the sandbox file sshd_up's key-only check wrote.
  profile() { # id backend name jumphops extra-args
    printf '{"Id":"%s","BackendKind":%s,"Name":"%s","GroupPath":"General","Host":"%s","User":"nova","Port":%s,"AuthMode":2,"IdentityFilePath":"%s","RememberPasswordInVault":false,"JumpHops":%s,"MuxOptions":{"Enabled":false,"PersistRemoteSessions":%s},"ExtraSshArgs":"%s"}' \
      "$1" "$2" "$3" "$SSHD_HOST" "$SSHD_PORT" "$KEYS/id_ed25519" "$4" "$PERSIST" "$5"
  }
  PERSIST=$1
  printf '{"SchemaVersion":1,"Profiles":[%s,%s,%s]}\n' \
    "$(profile $P_NATIVE 1 'a native key' '[]' '')" \
    "$(profile $P_OPENSSH 0 'b openssh key' '[]' "-o UserKnownHostsFile=$KEYS/known_hosts")" \
    "$(profile $P_JUMP 0 'c openssh via jump host' "[{\"Host\":\"$SSHD_HOST\",\"User\":\"nova\",\"Port\":$SSHD_PORT}]" "-o UserKnownHostsFile=$KEYS/known_hosts")" \
    >"$DATA/ssh/profiles.json"
}
write_profiles true
say "host key $HOSTKEY, trusted in the sandbox's native known hosts and in $KEYS/known_hosts"
say "profiles: 'a native key' (native), 'b openssh key' (OpenSSH), 'c openssh via jump host' (OpenSSH, one jump hop); all nova@$SSHD_HOST:$SSHD_PORT, key only, keep remote sessions"

say ''
say '=== L1 (step 41.1). ls --all while the host runs no ntilde-mux ==='
start_daemon "$DESC"
run mux spawn-for-test /bin/sh "$SBX/shells/hold.sh L"
LOCAL=$OUT
check 'a local daemon with one session (setup)' $([[ "$LOCAL" =~ ^[0-9a-f-]{36}$ ]]; echo $?)
check 'the host runs no ntilde-mux before the listing' $([[ -z "$(remote_daemon_pid)" ]]; echo $?)
run mux ls --all
check 'ls --all: a HOST column, this computer'"'"'s session first, exit 0' $([[ $CODE == 0 ]] && has '^HOST +ID +STATE +ATTACHED +SIZE +TITLE' && head -2 <<<"$OUT" | tail -1 | grep -Eq "^this computer +$LOCAL +running"; echo $?)
check 'both key profiles read "nova@127.0.0.1  unreachable: no multiplexer is running" (H7)' $([[ $(grep -cE "^nova@$SSHD_HOST +unreachable: no multiplexer is running$" <<<"$OUT") == 2 ]]; echo $?)
check 'the jump-host profile is not connected to: "unreachable: it goes through a jump host, which ls --all does not sign in through" (step 42)' $(has "^nova@$SSHD_HOST +unreachable: it goes through a jump host, which ls --all does not sign in through$"; echo $?)
run mux ls --all --json
check 'ls --all --json: "error":"no multiplexer is running" for both key profiles' $([[ $CODE == 0 ]] && has "\"endpoint\":\"ssh:${P_NATIVE//-/}\",\"host\":\"nova@$SSHD_HOST\",\"error\":\"no multiplexer is running\"" && has "\"endpoint\":\"ssh:${P_OPENSSH//-/}\",\"host\":\"nova@$SSHD_HOST\",\"error\":\"no multiplexer is running\""; echo $?)
REMOTE_AFTER_LISTING=$(remote_daemon_pid)
say "docker exec $SSHD_NAME pgrep -u nova -f 'ntilde-mux serve': ${REMOTE_AFTER_LISTING:-(nothing)}"
check 'the listing started no ntilde-mux on the host (H7)' $([[ -z "$REMOTE_AFTER_LISTING" ]]; echo $?)

say ''
say '=== L2 (steps 41.2, 42). Two sessions on the host (setup: ntilde-mux serve, then the App'"'"'s spawn-for-test in the container) ==='
remote_daemon_start || die "ntilde-mux serve on $SSHD_NAME wrote no descriptor"
REMOTE_PID=$(remote_daemon_pid)
RA=$(remote_spawn A)
RB=$(remote_spawn B)
say "remote ntilde-mux pid ${REMOTE_PID:-?}; sessions $RA, $RB"
[[ "$RA" =~ ^[0-9a-f-]{36}$ && "$RB" =~ ^[0-9a-f-]{36}$ ]] || die "remote spawn-for-test printed no session ids"
run mux ls --all
check 'ls --all lists both remote sessions under nova@127.0.0.1 for each key profile (4 rows), running, 0 attached' $([[ $CODE == 0 && $(remote_rows) == 4 && $(grep -cE "^nova@$SSHD_HOST +($RA|$RB) +running +0 " <<<"$OUT") == 4 ]]; echo $?) "$(remote_rows) remote rows"
check 'the jump-host profile still reads its "unreachable:" line' $(has "^nova@$SSHD_HOST +unreachable: it goes through a jump host"; echo $?)
run mux ls --all --json
check 'ls --all --json: {"endpoints":[{"endpoint":"local","host":"this computer","sessions":[...]},{"endpoint":"ssh:<id>","host":"nova@127.0.0.1","sessions":[...]},...]}' $([[ $CODE == 0 ]] && has "^\{\"endpoints\":\[\{\"endpoint\":\"local\",\"host\":\"this computer\",\"sessions\":\[\{\"sessionId\":\"$LOCAL\"" && has "\"endpoint\":\"ssh:${P_NATIVE//-/}\",\"host\":\"nova@$SSHD_HOST\",\"sessions\":\[" && has "\"endpoint\":\"ssh:${P_OPENSSH//-/}\",\"host\":\"nova@$SSHD_HOST\",\"sessions\":\[" && has "\"sessionId\":\"$RA\"" && has "\"sessionId\":\"$RB\"" && has "\"endpoint\":\"ssh:${P_JUMP//-/}\",\"host\":\"nova@$SSHD_HOST\",\"error\":\"it goes through a jump host"; echo $?)

say ''
say '=== L3 (step 27). The host drops off the network, then comes back ==='
docker network disconnect "$SSHD_NET" "$SSHD_NAME"
say "> docker network disconnect $SSHD_NET $SSHD_NAME (exit $?)"
run mux ls --all
check 'while the host is off the network: an "unreachable:" line for each profile, this computer still listed, exit 0' $([[ $CODE == 0 && $(grep -cE "^nova@$SSHD_HOST +unreachable: " <<<"$OUT") == 3 ]] && has "^this computer +$LOCAL +running"; echo $?)
check 'the unreachable host is cut within ls --all'"'"'s 15 s per-host wait (plus a little)' $([[ $ELAPSED -lt 25 ]]; echo $?) "$ELAPSED s"
docker network connect "$SSHD_NET" "$SSHD_NAME"
say "> docker network connect $SSHD_NET $SSHD_NAME (exit $?)"
if ssh_answers 45; then BACK=0; else BACK=1; fi
say "sshd answers on $SSHD_HOST:$SSHD_PORT again: $([[ $BACK == 0 ]] && echo yes || echo no)"
run mux ls --all
check 'after the host is back: the same two remote session ids, still running' $([[ $BACK == 0 && $CODE == 0 && $(grep -cE "^nova@$SSHD_HOST +($RA|$RB) +running " <<<"$OUT") == 4 ]]; echo $?)
REMOTE_AFTER=$(remote_daemon_pid)
check 'the remote ntilde-mux kept its pid across the drop' $([[ -n "$REMOTE_PID" && "$REMOTE_AFTER" == "$REMOTE_PID" ]]; echo $?) "before ${REMOTE_PID:-none}, after ${REMOTE_AFTER:-none}"

say ''
say '=== L4 (step 41.3). No multiplexer on this computer ==='
run mux kill-server
run mux ls --all
check 'this computer reads "unreachable: no multiplexer is running", the remote sessions are still listed, exit 1' $([[ $CODE == 1 ]] && has '^this computer +unreachable: no multiplexer is running$' && [[ $(remote_rows) == 4 ]]; echo $?)
run mux ls
check 'plain ls never connects to the host: "No multiplexer is running.", exit 1' $([[ $CODE == 1 ]] && has 'No multiplexer is running\.' && ! has 'nova@'; echo $?)

say ''
say '=== L5 (step 41.4). No profile keeps its remote sessions ==='
write_profiles false
run mux ls --all
check 'with no profile keeping sessions the table ends "No remote hosts keep sessions."' $(tail -1 <<<"$OUT" | grep -q '^No remote hosts keep sessions\.$' && ! has 'nova@'; echo $?)
write_profiles true

say ''
say '=== L6 (step 43). The host'"'"'s keys change while ntilde-mux keeps running ==='
cp "$DATA/ssh/native_known_hosts.json" "$SBX/native_known_hosts.before"
cp "$KEYS/known_hosts" "$SBX/known_hosts.before"
docker exec "$SSHD_NAME" sh -c 'rm -f /etc/ssh/ssh_host_* && ssh-keygen -A >/dev/null && kill -HUP 1'
ssh_answers 30 || die "sshd did not come back after its keys changed"
NEWKEY=$(sshd_host_key)
say "host key before: $HOSTKEY; now: $NEWKEY"
check 'the host serves a new key, and its ntilde-mux kept its pid' $([[ "$NEWKEY" != "$HOSTKEY" && "$(remote_daemon_pid)" == "$REMOTE_PID" ]]; echo $?)
run mux ls --all
check 'both key profiles (native, and OpenSSH in batch mode) read "unreachable: signing in needs an answer, which ls --all never asks for"' $([[ $(grep -cE "^nova@$SSHD_HOST +unreachable: signing in needs an answer, which ls --all never asks for$" <<<"$OUT") == 2 ]]; echo $?)
check 'no remote session is listed' $([[ $(remote_rows) == 0 ]]; echo $?)
check 'no new key was trusted: the sandbox'"'"'s native and OpenSSH known hosts are unchanged' $(cmp -s "$DATA/ssh/native_known_hosts.json" "$SBX/native_known_hosts.before" && cmp -s "$KEYS/known_hosts" "$SBX/known_hosts.before"; echo $?)

say ''
say '=== L7. What ls --all logged (logs/mux-ls-all.log, the last run only) ==='
LSLOG=$DATA/logs/mux-ls-all.log
if [[ -f "$LSLOG" ]]; then while IFS= read -r l; do say "    $l"; done <"$LSLOG"; else say '    (no log file)'; fi
check 'the log names no saved-password read' $([[ -f "$LSLOG" ]] && ! grep -qi 'saved password' "$LSLOG"; echo $?)
check 'OpenSSH ran in batch mode (it will not prompt)' $([[ -f "$LSLOG" ]] && grep -q 'batch mode: ssh will not prompt' "$LSLOG"; echo $?)
