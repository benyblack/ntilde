#!/usr/bin/env bash
# Reads changed paths (newline-separated) on stdin and prints exactly one line, run=true or
# run=false, saying whether the AOT gate (and so the linux-x64 ntilde-mux build the Docker
# E2E consumes) must run. Always exits 0.
#
# Scoped by DIRECTORY, not by file spelling: anything under src/ can change the bundle, the
# build inputs decide how it is compiled, and the test directories below feed the E2E that
# needs the daemon binary. A PR touching only those tests used to get no binary, and the
# remote-persistence step skipped with a notice while the job stayed green.
#
# Tested by scripts/tests/aot_gate_paths_tests.py.
set -u

changed="$(cat)"

# Here-string, not `echo | grep`: a long list would make echo take SIGPIPE under pipefail.
if grep -qE '^(src/|Directory\.(Build|Packages)\.props$|global\.json$|\.github/workflows/[^/]+\.ya?ml$|scripts/mux-daemon-smoke\.sh$|tests/Ntilde\.App\.Tests/Shell/Mux/|tests/Ntilde\.Mux\.Tests/|tests/Ntilde\.Platform\.Tests/Ssh/|tests/Ntilde\.ExternalSuites/NativeSsh/)' <<<"$changed"; then
  echo "run=true"
else
  echo "run=false"
fi
exit 0
