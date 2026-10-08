#!/usr/bin/env bash
# Prints the multiplexer protocol range this source tree builds, as `<min>-<max>` (e.g. `1-2`): the literals of
# MuxProtocol.MinSupportedVersion and MaxSupportedVersion in src/Ntilde.Mux.Contracts/MuxProtocol.cs, or in the file
# given as $1. release.yml puts it into every `vpk pack`'s release notes as `<!-- ntilde-mux-protocol: <min>-<max> -->`,
# which an installed app reads from a staged update to decide whether the update keeps its running multiplexer
# (Phase 5 R10; MuxUpdateCompatibility.ParseProtocolRange).
#
# A release must never ship a guessed range, so anything not read for certain fails, with nothing on stdout: a
# constant missing, assigned more than once (a comment that looks like an assignment counts), or not a 1-9 digit
# integer literal (`= SessionEventsVersion;`), or a range that is not 1 <= min <= max.
#
# Tested by scripts/tests/mux_protocol_range_tests.py. Runs under macOS's bash 3.2 and BSD grep/sed too.
set -eu

file="${1:-$(cd "$(dirname "$0")/../.." && pwd)/src/Ntilde.Mux.Contracts/MuxProtocol.cs}"

fail() {
  echo "mux-protocol-range: $*" >&2
  exit 1
}

[ -f "$file" ] || fail "$file not found"

# The one line assigning constant $1 (`==` is a comparison, not an assignment), and its integer literal.
read_constant() {
  name="$1"
  lines="$(grep -E "(^|[^A-Za-z0-9_])${name}[[:space:]]*=([^=]|$)" "$file" || true)"
  count="$(printf '%s' "$lines" | grep -c . || true)"
  [ "$count" = "1" ] || fail "expected exactly one assignment of $name in $file, found ${count:-0}"
  value="$(printf '%s\n' "$lines" | sed -nE "s/.*${name}[[:space:]]*=[[:space:]]*([0-9]{1,9})[[:space:]]*;.*/\1/p")"
  [ -n "$value" ] || fail "$name in $file is not an integer literal: $(printf '%s' "$lines" | tr -d '\r')"
  echo "$((10#$value))"
}

min="$(read_constant MinSupportedVersion)" || exit 1
max="$(read_constant MaxSupportedVersion)" || exit 1
[ "$min" -ge 1 ] || fail "MinSupportedVersion is $min; protocol versions start at 1"
[ "$min" -le "$max" ] || fail "MinSupportedVersion $min is above MaxSupportedVersion $max"
printf '%s-%s\n' "$min" "$max"
