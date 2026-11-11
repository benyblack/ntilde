#!/usr/bin/env bash
# Reads a PR's changed paths (newline-separated) on stdin and prints exactly one line, run=true or
# run=false, saying whether ci.yml's .NET and Rust jobs (Build and everything after it, Analyzers,
# Format, Coverage, Rust FFI Tests) must run. Always exits 0.
#
# The reverse of aot-gate-paths.sh, and deliberately so: that one lists what CAN change the AOT
# bundle and runs on any match; this one lists what CANNOT change a .NET or Rust result and skips
# only when EVERY changed path is on the list. A path nobody thought about therefore runs the
# jobs - the safe way to be wrong - and so does an empty list (a diff that could not be read).
#
# What is on the list, and why it cannot matter to those jobs:
#   - scripts/terminfo/ and scripts/terminfo-*: the terminfo qualification scripts. No project,
#     build target or .NET test reads them; their own tests are the Script self-tests, which
#     always run.
#   - docs/qa/: the qualification evidence those scripts publish. Nothing under src/ or tests/
#     reads it.
#   - scripts/ci/node-tests-*: the shard script, launcher and durations table of those same
#     Script self-tests.
#   - .github/workflows/terminfo-*.yml: their own workflows, which do not touch ci.yml.
# Not on it, on purpose: anything else under scripts/ (the build wrappers and the ci helpers the
# .NET jobs call), .github/workflows/ci.yml itself and .github/actions/, .gitattributes, which
# decides the line endings the format job checks, and Markdown outside docs/qa - .NET tests read
# docs (the VT conformance report and capability tests, the release workflow and MCP server
# tests), so a .md beside a script change still runs them.
#
# Tested by scripts/tests/dotnet_gate_paths_tests.py.
set -u

changed="$(cat)"
changed="$(grep -v '^[[:space:]]*$' <<<"$changed" || true)"

if [[ -z "$changed" ]]; then
  echo "run=true"
  exit 0
fi

# Here-string, not `echo | grep`: a long list would make echo take SIGPIPE under pipefail.
if grep -qvE '^(scripts/terminfo/|scripts/terminfo-[^/]+$|docs/qa/|scripts/ci/node-tests-[^/]+$|\.github/workflows/terminfo-[^/]+\.ya?ml$)' <<<"$changed"; then
  echo "run=true"
else
  echo "run=false"
fi
exit 0
