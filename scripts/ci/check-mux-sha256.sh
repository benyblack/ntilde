#!/usr/bin/env bash
# Checks the ntilde-mux checksums publish_mux_daemon wrote before release.yml embeds them in the App as pins
# (Phase 5 Task 10). Usage: check-mux-sha256.sh <dir>. Each of ntilde-mux-<rid>.sha256 for linux-x64,
# linux-arm64 and osx-arm64 must be exactly one line, `<64 lowercase hex>  ntilde-mux-<rid>` (sha256sum's
# and `shasum -a 256`'s text mode, run beside the bare asset), with or without its trailing newline.
#
# A release build refuses a remote install for any RID it has no usable pin for (release hardening item 4):
# MuxAssetPins keeps a malformed resource as unusable rather than dropping it, so a file that is not exactly
# this shape would ship an App that cannot install ntilde-mux on that platform. Fail the release instead.
#
# Tested by scripts/tests/mux_sha256_check_tests.py. Runs under macOS's bash 3.2, BSD grep and Git Bash too.
set -eu

dir="${1:?usage: check-mux-sha256.sh <dir>}"
status=0
for rid in linux-x64 linux-arm64 osx-arm64; do
  file="$dir/ntilde-mux-$rid.sha256"
  if [ ! -s "$file" ]; then
    echo "::error::ntilde-mux-$rid.sha256 is missing or empty; nothing to embed" >&2
    status=1
    continue
  fi
  # wc -l counts newlines: 0 for a lone unterminated line, 1 for a terminated one. More is never right.
  lines="$(wc -l < "$file" | tr -d ' ')"
  if [ "$lines" -gt 1 ] || ! grep -Eq "^[0-9a-f]{64}  ntilde-mux-$rid\$" "$file"; then
    echo "::error::ntilde-mux-$rid.sha256 is not exactly '<64 lowercase hex>  ntilde-mux-$rid'" >&2
    status=1
  fi
done
exit "$status"
