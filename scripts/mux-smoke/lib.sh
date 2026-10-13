# shellcheck shell=bash
# Shared by the Linux cell scripts in scripts/mux-smoke/. Sourced, never run.
#
# The PASS/FAIL lines and the closing "N of M checks passed." are the format of the logs the
# Phase 5 manual checklist quotes (docs/superpowers/plans/2026-10-08-ntilde-mux-phase5-manual-checklist.md).
# A script ends with `finish` (through its EXIT trap): it cleans up, prints the summary, and exits 1 when
# any check failed, when none ran, or when the script stopped early.

say() { printf '[%s] %s\n' "$(date +%T)" "$*"; }

RESULTS=()
# check <name> <0 | anything else> [detail]: the second argument is a condition's exit status, so a cell
# reads `check 'what' $([[ ... ]] && has ...; echo $?)`.
check() {
  local v=PASS
  [[ "$2" == 0 ]] || v=FAIL
  RESULTS+=("$v  $1")
  say "$v  $1${3:+ - $3}"
}

# die <why>: a setup step failed. Counted as a failure, then the EXIT trap cleans up.
die() {
  RESULTS+=("FAIL  setup: $*")
  say "FAIL  setup: $*"
  exit 1
}

# run <args...>: the App's CLI (`Ntilde <args>`). Sets OUT (stdout and stderr), CODE and ELAPSED (seconds), and
# prints them.
run() {
  local t0=$SECONDS
  OUT="$("$APP" "$@" 2>&1)"
  CODE=$?
  ELAPSED=$((SECONDS - t0))
  say "> Ntilde $*"
  while IFS= read -r l; do say "  $l"; done <<<"$OUT"
  say "  (exit $CODE, $ELAPSED s)"
}

# run_bin <label> <exe> <args...>: the same for another executable (ntilde-mux).
run_bin() {
  local label=$1 exe=$2
  shift 2
  OUT="$("$exe" "$@" 2>&1)"
  CODE=$?
  say "> $label $*"
  while IFS= read -r l; do say "  $l"; done <<<"$OUT"
  say "  (exit $CODE)"
}

has() { grep -Eq -- "$1" <<<"$OUT"; }

# resolve_exe <dir or file> <file name>: the executable itself, given it or the folder it is in.
resolve_exe() {
  local p=$1
  [[ -d "$p" ]] && p="$p/$2"
  [[ -x "$p" ]] || { echo "not an executable: $p" >&2; return 1; }
  (cd "$(dirname "$p")" && printf '%s/%s\n' "$PWD" "$(basename "$p")")
}

# prepare_sandbox <dir>: makes it, refuses / and $HOME, and prints its absolute path.
prepare_sandbox() {
  mkdir -p "$1" || return 1
  local abs
  abs="$(cd "$1" && pwd -P)"
  if [[ "$abs" == / || "$abs" == "$(cd ~ && pwd -P)" ]]; then
    echo "refusing $abs as a sandbox: give a folder of its own" >&2
    return 1
  fi
  printf '%s\n' "$abs"
}

# isolate <data root>: what every process a script starts inherits. NTILDE_APPDATA_ROOT moves the data folder
# (settings, the multiplexer's socket and descriptor, profiles, known hosts). The system credential store does
# not move with it (the checklist's setup, item 1): on Linux it is libsecret over the D-Bus session bus, so the
# bus address is pointed at a socket that does not exist, and a vault read (an automatic OpenSSH attempt's)
# fails instead of reaching a real keyring.
isolate() {
  export NTILDE_APPDATA_ROOT=$1
  export DBUS_SESSION_BUS_ADDRESS="unix:path=$1/no-session-bus"
  unset SSH_AUTH_SOCK
}

# sandbox_settings <data root> <agent observe: true|false>: the checklist's sandbox settings.json.
sandbox_settings() {
  mkdir -p "$1"
  printf '{\n  "SessionPersistence": "KeepOnClose",\n  "AgentAccessObserveEnabled": %s,\n  "QuakeModeEnabled": false,\n  "AutomaticUpdateChecks": false\n}\n' "$2" >"$1/settings.json"
}

# sandbox_pids <data root> [pattern]: this user's processes started under that root (they inherit
# NTILDE_APPDATA_ROOT), never anything else of the user's, and not this script or its subshells (the same command
# line, or a text tool of its pipelines). With a pattern, only those whose command line matches it (grep -E).
sandbox_pids() {
  local me pid cmd
  me=$(tr '\0' ' ' </proc/$$/cmdline)
  for pid in $(pgrep -u "$(id -u)" .); do
    cmd=$(tr '\0' ' ' 2>/dev/null <"/proc/$pid/cmdline") || continue
    [[ "$cmd" == "$me" ]] && continue
    # This script's own pipelines (`sandbox_pids ... | tr`) inherit the variable too.
    [[ "$(cat 2>/dev/null "/proc/$pid/comm")" =~ ^(tr|grep|sed|cut|head|tail|wc|sort|ps|pgrep|cat|date|timeout)$ ]] && continue
    [[ -n "${2:-}" ]] && ! grep -Eq -- "$2" <<<"$cmd" && continue
    tr '\0' '\n' 2>/dev/null <"/proc/$pid/environ" | grep -qxF "NTILDE_APPDATA_ROOT=$1" && echo "$pid"
  done
  return 0
}

# app_gui_pids <data root>: the GUI (the App started with no arguments) under that root. The command line is compared
# literally, so a build path with regex characters in it still matches.
app_gui_pids() {
  local pid
  for pid in $(sandbox_pids "$1"); do
    [[ "$(tr '\0' ' ' 2>/dev/null <"/proc/$pid/cmdline")" == "$APP " ]] && echo "$pid"
  done
  return 0
}

# refuse_if_running <data root>: a sandbox something still runs under is left alone. Called before the script deletes
# anything or installs the cleanup that kills what runs under the root.
refuse_if_running() {
  local pids
  pids=$(sandbox_pids "$1" | tr '\n' ' ')
  if [[ -n "$pids" ]]; then
    echo "processes are already running under $1 (pids $pids); stop them first, or give another sandbox" >&2
    exit 2
  fi
}

# hold_script <dir>: the session shell the cells start: `/bin/sh hold.sh <tag>` prints t30-session-<tag>, then waits.
hold_script() {
  mkdir -p "$1"
  # shellcheck disable=SC2016 # $1 is the hold script's own argument
  printf 'echo "t30-session-$1"\nexec sleep 100000\n' >"$1/hold.sh"
}

# start_daemon <descriptor>: the multiplexer, started as ProcessMuxDaemonSpawner does on Unix (`<exe> mux serve`,
# detached, stdio on /dev/null; the daemon calls setsid itself). Sets DPID from the descriptor it writes.
start_daemon() {
  setsid "$APP" mux serve </dev/null >/dev/null 2>&1 &
  local _
  for _ in $(seq 1 75); do [[ -s "$1" ]] && break; sleep 0.2; done
  sleep 0.3
  DPID=$(grep -o '"pid":[0-9]*' "$1" 2>/dev/null | cut -d: -f2)
  say "started: Ntilde mux serve; descriptor pid ${DPID:-MISSING}: $(tr -d '\n' <"$1" 2>/dev/null)"
}

# stop_sandbox <data root>: kill-server under that root, then SIGKILL whatever of it is left.
stop_sandbox() {
  local root=$1 pid
  if [[ -s "$root/mux/mux-endpoint.json" ]]; then
    NTILDE_APPDATA_ROOT=$root "$APP" mux kill-server --force 2>&1 | sed 's/^/  /'
  fi
  for pid in $(sandbox_pids "$root"); do
    say "stopping what is left of the sandbox: pid $pid $(tr '\0' ' ' 2>/dev/null <"/proc/$pid/cmdline")"
    kill -9 "$pid" 2>/dev/null
  done
  return 0
}

# summary: the closing lines; returns 1 when anything failed or nothing was checked.
summary() {
  local fails=0 r
  say ''
  say '=== Summary ==='
  for r in "${RESULTS[@]}"; do
    say "  $r"
    [[ $r == FAIL* ]] && fails=$((fails + 1))
  done
  say "$((${#RESULTS[@]} - fails)) of ${#RESULTS[@]} checks passed."
  [[ $fails == 0 && ${#RESULTS[@]} -gt 0 ]]
}

# ---- the Docker sshd of the checklist's setup (item 3) -------------------------------------------------------------
# One container and one network, made and removed by the script that uses them. The script refuses to start when
# either exists already, so it never removes one it did not make.
SSHD_IMAGE=novaterm-native-ssh-e2e:v5
SSHD_NAME=t30-ssh
SSHD_NET=t30-net
SSHD_SUBNET=10.230.30.0/24
SSHD_HOST=127.0.0.1
SSHD_PORT=2230
REMOTE_HOME=/home/nova
REMOTE_MUX=$REMOTE_HOME/.local/share/ntilde/bin/ntilde-mux
# ntilde-mux's own root beneath the app-data root (MuxPaths.Standalone), where its descriptor lives.
REMOTE_MUX_ROOT=$REMOTE_HOME/.local/share/ntilde/ntilde-mux
# This run's label on the container and the network it creates: cleanup removes only what carries it, so a run that
# lost a race to another one never removes the other's.
SSHD_LABEL="mux-smoke.run"
SSHD_RUN="$$.$RANDOM.$(date +%s)"

# ssh_answers <seconds>: whether 127.0.0.1:2230 sends an SSH banner within that time.
ssh_answers() {
  local deadline=$((SECONDS + $1)) banner
  while ((SECONDS < deadline)); do
    banner=$(timeout 3 bash -c "exec 3<>/dev/tcp/$SSHD_HOST/$SSHD_PORT && head -c 4 <&3" 2>/dev/null)
    [[ "$banner" == SSH- ]] && return 0
    sleep 0.5
  done
  return 1
}

# sshd_up <key dir> <ntilde-mux>: the network, the container, a throwaway key authorized for nova, and ntilde-mux
# installed where the app looks for it. Ends with the checklist's key-only check, which also writes the host's key
# into <key dir>/known_hosts (the OpenSSH profile's sandbox known-hosts file).
sshd_up() {
  local keys=$1 mux=$2
  command -v docker >/dev/null || die "docker is not on PATH"
  docker image inspect "$SSHD_IMAGE" >/dev/null 2>&1 \
    || die "no $SSHD_IMAGE image: docker build -t $SSHD_IMAGE tests/Ntilde.ExternalSuites/NativeSsh"
  if docker container inspect "$SSHD_NAME" >/dev/null 2>&1 || docker network inspect "$SSHD_NET" >/dev/null 2>&1; then
    die "$SSHD_NAME or $SSHD_NET exists already; remove them first (docker rm -f $SSHD_NAME; docker network rm $SSHD_NET)"
  fi
  docker network create --label "$SSHD_LABEL=$SSHD_RUN" --driver bridge --subnet "$SSHD_SUBNET" "$SSHD_NET" >/dev/null \
    || die "docker network create $SSHD_NET"
  docker run -d --label "$SSHD_LABEL=$SSHD_RUN" --name "$SSHD_NAME" --hostname "$SSHD_NAME" --network "$SSHD_NET" \
    -p "$SSHD_HOST:$SSHD_PORT:22" "$SSHD_IMAGE" >/dev/null || die "docker run $SSHD_NAME"
  ssh_answers 30 || die "sshd did not answer on $SSHD_HOST:$SSHD_PORT within 30 s"

  rm -rf "$keys" && mkdir -p "$keys" && chmod 700 "$keys"
  ssh-keygen -q -t ed25519 -N '' -C ntilde-smoke -f "$keys/id_ed25519" || die "ssh-keygen"
  docker exec -i "$SSHD_NAME" sh -c "cat > $REMOTE_HOME/.ssh/authorized_keys && chown nova:nova $REMOTE_HOME/.ssh/authorized_keys && chmod 600 $REMOTE_HOME/.ssh/authorized_keys" \
    <"$keys/id_ed25519.pub" || die "authorize the throwaway key"
  # As nova, so every folder on the way is nova's: `install -d -o nova` would leave ~/.local/share/ntilde to root,
  # and ntilde-mux could not make its root there.
  docker exec -u nova "$SSHD_NAME" mkdir -p "$(dirname "$REMOTE_MUX")" || die "make ntilde-mux's folder"
  docker cp "$mux" "$SSHD_NAME:$REMOTE_MUX" >/dev/null || die "docker cp ntilde-mux"
  docker exec "$SSHD_NAME" sh -c "chown nova:nova $REMOTE_MUX && chmod 755 $REMOTE_MUX" || die "chmod ntilde-mux"
  docker exec "$SSHD_NAME" sh -c "printf 'echo \"t30-session-\$1\"\nexec sleep 100000\n' > $REMOTE_HOME/t30-hold.sh && chown nova:nova $REMOTE_HOME/t30-hold.sh" \
    || die "write the remote hold script"
  local ok
  # UserKnownHostsFile is a list of files to ssh: the path is quoted inside the option, for a sandbox with spaces.
  ok=$(ssh -F /dev/null -i "$keys/id_ed25519" -o IdentitiesOnly=yes -o "UserKnownHostsFile=\"$keys/known_hosts\"" \
    -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o PasswordAuthentication=no -p "$SSHD_PORT" "nova@$SSHD_HOST" \
    "echo key-auth-ok; $REMOTE_MUX --version" 2>&1)
  say "key-only check: $(tr '\n' ' ' <<<"$ok")"
  grep -q '^key-auth-ok' <<<"$ok" || die "key authentication to nova@$SSHD_HOST:$SSHD_PORT"
  grep -q '^ntilde-mux ' <<<"$ok" || die "ntilde-mux does not run on $SSHD_NAME (glibc floor? build it with scripts/docker-publish-mux-daemon.sh)"
}

# sshd_down: removes the container and the network, each only when it carries this run's label.
sshd_down() {
  if [[ "$(docker container inspect -f "{{index .Config.Labels \"$SSHD_LABEL\"}}" "$SSHD_NAME" 2>/dev/null)" == "$SSHD_RUN" ]]; then
    docker rm -f "$SSHD_NAME" >/dev/null 2>&1 && say "removed container $SSHD_NAME"
  fi
  if [[ "$(docker network inspect -f "{{index .Labels \"$SSHD_LABEL\"}}" "$SSHD_NET" 2>/dev/null)" == "$SSHD_RUN" ]]; then
    docker network rm "$SSHD_NET" >/dev/null 2>&1 && say "removed network $SSHD_NET"
  fi
  return 0
}

# The host key the sshd serves now: "<algorithm> <SHA256 fingerprint>".
sshd_host_key() {
  docker exec "$SSHD_NAME" sh -c 'cut -d" " -f1 /etc/ssh/ssh_host_ed25519_key.pub; ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256 | cut -d" " -f2' \
    | tr '\n' ' ' | sed 's/ $//'
}

remote_daemon_pid() { docker exec "$SSHD_NAME" pgrep -u nova -f 'ntilde-mux serve' 2>/dev/null | head -1; }

# remote_daemon_start: `ntilde-mux serve` as nova, detached; waits for its descriptor.
remote_daemon_start() {
  docker exec -d -u nova "$SSHD_NAME" "$REMOTE_MUX" serve || return 1
  local _
  for _ in $(seq 1 50); do
    docker exec "$SSHD_NAME" test -s "$REMOTE_MUX_ROOT/mux/mux-endpoint.json" && return 0
    sleep 0.2
  done
  return 1
}

# remote_runtime_install <app dir>: a .NET runtime and the App, copied into the container, for remote_spawn. Only the
# App's CLI runs there (spawn-for-test talks to ntilde-mux's socket), so its runtimes/ folder (the GUI's native
# libraries) and symbols stay behind. The runtime is the one running this script's App.
remote_runtime_install() {
  local app_dir=$1 dn
  dn="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
  [[ -x "$dn/dotnet" && -d "$dn/shared/Microsoft.NETCore.App" ]] || die "no .NET runtime at '$dn' (set DOTNET_ROOT)"
  docker exec "$SSHD_NAME" mkdir -p /opt/mux-smoke/dotnet /opt/mux-smoke/app || die "mkdir /opt/mux-smoke"
  tar -C "$dn" -cf - dotnet host shared/Microsoft.NETCore.App | docker exec -i "$SSHD_NAME" tar -C /opt/mux-smoke/dotnet -xf - \
    || die "copy the .NET runtime into $SSHD_NAME"
  tar -C "$app_dir" --exclude=./runtimes --exclude='*.pdb' -cf - . | docker exec -i "$SSHD_NAME" tar -C /opt/mux-smoke/app -xf - \
    || die "copy the App into $SSHD_NAME"
}

# remote_spawn <tag>: setup only. A session in the host's ntilde-mux, started by the App's hidden spawn-for-test run
# inside the container as nova and pointed at ntilde-mux's root. Prints the session id.
remote_spawn() {
  docker exec -u nova -e DOTNET_ROOT=/opt/mux-smoke/dotnet -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    -e NTILDE_APPDATA_ROOT="$REMOTE_MUX_ROOT" "$SSHD_NAME" /opt/mux-smoke/dotnet/dotnet /opt/mux-smoke/app/Ntilde.dll \
    mux spawn-for-test /bin/sh "$REMOTE_HOME/t30-hold.sh $1" 2>&1 | tail -1
}
