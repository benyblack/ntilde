#!/usr/bin/env bash
# Runs a published ntilde-mux: --version --json, then a foreground daemon that `ls` reaches and
# `kill-server` stops.
# Usage: scripts/mux-daemon-smoke.sh [--no-openssl] /abs/path/to/ntilde-mux
#
# ci.yml runs it in two places: mux_daemon_aot on every RID it builds, in the build container (which
# installs libssl3) or on the macOS runner, and mux_daemon_no_openssl with --no-openssl on the
# linux-x64 binary inside debian:12-slim, which ships no OpenSSL. Ntilde.Architecture.Tests sees only
# direct assembly references, not a BCL path that loads libssl at run time, and a smoke on a host that
# has libssl cannot see one either. --no-openssl first proves the host has neither libssl nor
# libcrypto, so the smoke cannot pass by finding them, asserts ldd lists neither, then runs the smoke.
#
# The macOS runner's bash is 3.2, so nothing below uses bash 4 syntax.
set -euo pipefail

no_openssl=0
if [[ "${1:-}" == "--no-openssl" ]]; then
  no_openssl=1
  shift
fi
bin="${1:?usage: $0 [--no-openssl] /abs/path/to/ntilde-mux}"

if [[ "$no_openssl" -eq 1 ]]; then
  # The loader cache and the files on disk, both: a library copied in without an ldconfig run is
  # in only one of them. Each output is captured before grep sees it: `producer | grep -q` under
  # pipefail fails when grep closes the pipe early, which an `if` would read as "not found".
  ld_cache="$(ldconfig -p)"
  if grep -E 'libssl|libcrypto' <<<"$ld_cache"; then
    echo "::error::the loader cache lists OpenSSL (above); this host cannot prove ntilde-mux runs without it" >&2
    exit 1
  fi
  # -H: on a merged-/usr distribution /lib is a symlink to /usr/lib.
  lib_dirs=()
  for d in /lib /usr/lib /usr/local/lib; do
    if [[ -d "$d" ]]; then lib_dirs+=("$d"); fi
  done
  on_disk="$(find -H "${lib_dirs[@]}" \( -name 'libssl.so*' -o -name 'libcrypto.so*' \) -print)"
  if [[ -n "$on_disk" ]]; then
    echo "$on_disk"
    echo "::error::OpenSSL is on disk (above); this host cannot prove ntilde-mux runs without it" >&2
    exit 1
  fi
  echo "no libssl or libcrypto in the loader cache or under ${lib_dirs[*]}"

  ldd_out="$(ldd "$bin" 2>&1)" || { echo "$ldd_out"; echo "::error::ldd failed on $bin" >&2; exit 1; }
  echo "$ldd_out"
  if grep -E 'libssl|libcrypto' <<<"$ldd_out" >/dev/null; then
    echo "::error::ntilde-mux links OpenSSL (ldd above); it must run on libc alone" >&2
    exit 1
  fi
fi

version_json="$("$bin" --version --json)"
echo "$version_json"
grep -q '"protocolMax":2' <<<"$version_json" \
  || { echo "::error::--version --json does not report protocolMax 2" >&2; exit 1; }

# From a pristine root, as on a fresh remote host. ntilde-mux serves a root of its own beneath the
# app-data root (MuxPaths.Standalone: never the GUI's), so the descriptor is
# <app-data root>/ntilde-mux/mux/mux-endpoint.json (MuxDiscovery), written once the daemon listens;
# `ls` exits 1 when it reaches no daemon, so its 0 proves the round trip.
NTILDE_APPDATA_ROOT="$(mktemp -d)"
export NTILDE_APPDATA_ROOT
descriptor="$NTILDE_APPDATA_ROOT/ntilde-mux/mux/mux-endpoint.json"
serve_log="${RUNNER_TEMP:-/tmp}/ntilde-mux-serve.log"
"$bin" serve --foreground --idle-exit-minutes 0 >"$serve_log" 2>&1 &
serve_pid=$!
# Whatever happens below, no daemon outlives the script, and its log is printed.
finish() {
  status=$?
  if kill -0 "$serve_pid" 2>/dev/null; then kill -9 "$serve_pid" 2>/dev/null || true; fi
  echo "---- serve --foreground log ----"
  cat "$serve_log" || true
  exit "$status"
}
trap finish EXIT

for _ in $(seq 1 60); do
  [[ -f "$descriptor" ]] && break
  kill -0 "$serve_pid" 2>/dev/null || break
  sleep 0.5
done
[[ -f "$descriptor" ]] || { echo "::error::serve wrote no descriptor within 30 s" >&2; exit 1; }

"$bin" ls
"$bin" kill-server

for _ in $(seq 1 40); do
  kill -0 "$serve_pid" 2>/dev/null || break
  sleep 0.5
done
if kill -0 "$serve_pid" 2>/dev/null; then
  echo "::error::serve was still running 20 s after kill-server" >&2
  exit 1
fi
serve_status=0
wait "$serve_pid" || serve_status=$?
if [[ "$serve_status" -ne 0 ]]; then
  echo "::error::serve exited $serve_status after kill-server" >&2
  exit 1
fi
