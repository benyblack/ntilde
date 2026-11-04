#!/usr/bin/env bash
# Builds and runs the screenshot harness. Uses scripts/build.sh rather than raw `dotnet`
# for the reason documented in CLAUDE.md: raw dotnet build leaves MSBuild daemons holding
# the caller's stdout and hangs anything reading via pipes.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

"$here/build.sh" build -c Release "$here/../tools/Ntilde.Shots"

exec dotnet "$here/../tools/Ntilde.Shots/bin/Release/net10.0/Ntilde.Shots.dll" "$@"
