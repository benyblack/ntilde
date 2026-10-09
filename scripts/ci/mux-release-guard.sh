#!/usr/bin/env bash
# Keeps a release run from replacing a published ntilde-mux (Codex review of PR #511, P1). Every installed App
# pins the SHA-256 of its own version's ntilde-mux-<rid> (Phase 5 Task 10, R16). A rerun for a tag that already
# has them signs the macOS binary again, with a new secure timestamp, so it gets new bytes and a new hash, and
# replacing the asset breaks remote installs on macOS hosts for every App of that version. So a run refuses:
#
#   mux-release-guard.sh preflight <tag>
#     In release.yml's release_metadata, which every other job needs, so before anything is built, signed or
#     uploaded: fails when the tag's GitHub release already carries any ntilde-mux asset. A release that does
#     not exist yet passes. Any other gh failure, or an answer that is not the expected JSON, fails closed.
#
#   mux-release-guard.sh uploaded <rid>
#     In publish_mux_daemon, right after its upload, whose `overwrite_files: false` the pinned
#     softprops/action-gh-release treats as "skip the existing asset and succeed". Reads that step's `assets`
#     output (the assets it really uploaded) from $UPLOADED_ASSETS and fails unless both ntilde-mux-<rid> and
#     ntilde-mux-<rid>.sha256 are in it, so a leg rerun on its own (which skips release_metadata) never hands
#     the App builds a checksum for bytes the release does not carry.
#
# The tag and rid are only ever data (a workflow_dispatch input can hold shell metacharacters).
# Needs jq, and gh for preflight. Tested by scripts/tests/mux_release_guard_tests.py. Runs under macOS's bash 3.2.
set -eu

fail() {
  echo "::error::mux-release-guard: $*" >&2
  exit 1
}

usage() {
  fail "usage: mux-release-guard.sh preflight <tag> | uploaded <rid>"
}

preflight() {
  tag="$1"
  err="$(mktemp)"
  trap 'rm -f "$err"' EXIT
  status=0
  out="$(gh release view "$tag" --json assets 2>"$err")" || status=$?
  if [ "$status" != "0" ]; then
    # gh's own error for a tag with no release (its ErrReleaseNotFound). Anything else is not an answer.
    if grep -qF "release not found" "$err"; then
      echo "mux-release-guard: no release for $tag yet, so no ntilde-mux asset to replace"
      return 0
    fi
    fail "could not read the release for $tag (gh exit $status): $(tr -d '\r' <"$err")"
  fi
  out="$(printf '%s' "$out" | tr -d '\r')"
  [ -n "$out" ] || fail "gh printed nothing for the release $tag"
  names="$(printf '%s' "$out" | jq -r 'if (.assets | type) == "array" then .assets[] | .name | strings | select(startswith("ntilde-mux")) else error("no assets list") end')" \
    || fail "could not read gh's answer for the release $tag: $(printf '%s' "$out" | head -c 200)"
  names="$(printf '%s' "$names" | tr -d '\r' | tr '\n' ' ' | sed 's/ *$//')"
  if [ -n "$names" ]; then
    fail "ntilde-mux assets already exist for $tag ($names); installed apps pin their hashes. Publish a new version instead of rerunning. (To finish a run that stopped part-way, use \"Re-run failed jobs\" on that run.)"
  fi
  echo "mux-release-guard: the release $tag has no ntilde-mux asset yet"
}

uploaded() {
  rid="$1"
  json="$(printf '%s' "${UPLOADED_ASSETS-}" | tr -d '\r')"
  [ -n "$json" ] || fail "no upload output to check (UPLOADED_ASSETS is empty)"
  names="$(printf '%s' "$json" | jq -r 'if type == "array" then .[] | .name | strings else error("not a list") end')" \
    || fail "could not read the upload output: $(printf '%s' "$json" | head -c 200)"
  names="$(printf '%s\n' "$names" | tr -d '\r')"
  missing=""
  for want in "ntilde-mux-$rid" "ntilde-mux-$rid.sha256"; do
    printf '%s\n' "$names" | grep -qxF -- "$want" || missing="$missing $want"
  done
  if [ -n "$missing" ]; then
    fail "this run did not upload$missing: the release already had it from an earlier run, and it was left as it is. Installed apps pin their hashes, and this run's checksum would not describe the published binary. Publish a new version instead of rerunning."
  fi
  echo "mux-release-guard: uploaded ntilde-mux-$rid and ntilde-mux-$rid.sha256"
}

[ "$#" -eq 2 ] || usage
[ -n "$2" ] || usage
command -v jq >/dev/null 2>&1 || fail "jq not found"
case "$1" in
  preflight) preflight "$2" ;;
  uploaded) uploaded "$2" ;;
  *) usage ;;
esac
