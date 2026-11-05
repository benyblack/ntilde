#!/bin/bash
# Profile command for the check: `/bin/bash run-nvim.sh <log> [plain|ntilde]`.
# Unsets COLORTERM so Neovim has to discover truecolor by asking the terminal, then runs
# Neovim with probe.lua and writes the environment it saw to <log>.env.
# With "ntilde", TERM=xterm-ntilde from a database compiled next to this script:
#   tic -x -o terminfo-db ../../../../packaging/terminfo/xterm-ntilde.terminfo
out="$1"; mode="$2"; here="$(cd "$(dirname "$0")" && pwd)"
unset COLORTERM
if [ "$mode" = ntilde ]; then export TERMINFO="$here/terminfo-db" TERM=xterm-ntilde; fi
{
  echo "TERM=$TERM TERM_PROGRAM=$TERM_PROGRAM COLORTERM=${COLORTERM-unset} NTILDE_MUX_SESSION=${NTILDE_MUX_SESSION-none}"
  infocmp -x "$TERM" >/dev/null 2>&1 && echo "terminfo $TERM: found" || echo "terminfo $TERM: MISSING"
} > "$out.env"
PROBE_OUT="$out" nvim --clean --cmd "luafile $here/probe.lua"
echo "nvim exit $?" >> "$out.env"
