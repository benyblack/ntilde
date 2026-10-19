#!/usr/bin/env bash
# Phase 5 manual checklist, step 44 on Linux: the agent host's ntilde.list_sessions lists a session that runs in the
# multiplexer with no window as `local (windowless)`, and ntilde.read_screen reads it. The GUI runs under Xvfb with
# Agent access (observe) on and is never sent input; the MCP server is driven over stdio. A documented manual script:
# CI does not run it.
#
# Usage: scripts/mux-smoke/linux-agent-cells.sh <app> <mcp-server> <sandbox>
#   <app>         the App build folder (the one holding `Ntilde`), or the `Ntilde` executable
#   <mcp-server>  the Ntilde.McpServer build folder, or the `Ntilde.McpServer` executable
#   <sandbox>     a folder of the script's own; it writes only <sandbox>/agent
#
# Needs xvfb-run. On Linux the agent socket is under the data folder, so the sandbox's agent host is its own (on
# Windows the agent pipe is per user and would be shared with a real Ntilde: the checklist's setup, item 1).
# check's second argument is a condition's exit status, `$(...; echo $?)`: one word, by design.
# shellcheck disable=SC2046,SC2319
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/mux-smoke/lib.sh
source "$here/lib.sh"

usage="usage: $0 <app folder or Ntilde> <mcp-server folder or Ntilde.McpServer> <sandbox>"
APP="$(resolve_exe "${1:?$usage}" Ntilde)" || exit 2
MCP="$(resolve_exe "${2:?$usage}" Ntilde.McpServer)" || exit 2
SBX="$(prepare_sandbox "${3:?$usage}/agent")" || exit 2
command -v xvfb-run >/dev/null || { echo "xvfb-run is not on PATH" >&2; exit 2; }

DATA=$SBX/data
refuse_if_running "$DATA"
rm -rf "$DATA" "$SBX/shells"
isolate "$DATA"
sandbox_settings "$DATA" true
printf 'keep' >"$DATA/mux-close-choice"
hold_script "$SBX/shells"

finish() {
  trap - EXIT
  say ''
  say '=== Cleanup ==='
  for p in $(app_gui_pids "$DATA"); do kill "$p" 2>/dev/null; done
  sleep 2
  for p in $(app_gui_pids "$DATA"); do kill -9 "$p" 2>/dev/null; done
  stop_sandbox "$DATA"
  for p in $(sandbox_pids "$DATA" 'Xvfb|xvfb-run'); do kill "$p" 2>/dev/null; done
  left=$(sandbox_pids "$DATA" | tr '\n' ' ')
  say "processes of the sandbox still running: ${left:-none}"
  summary
  exit $?
}
trap finish EXIT

# mcp <tool> <arguments-json>: initialize, then one tools/call; prints the answer to the call (id 2), its text unescaped.
mcp() {
  {
    printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mux-smoke","version":"1"}}}'
    sleep 1
    printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    printf '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"%s","arguments":%s}}\n' "$1" "$2"
    sleep 6
  } | timeout 30 "$MCP" 2>/dev/null | grep '"id":2' | sed 's/\\n/\n/g; s/\\"/"/g'
}

say "=== Phase 5 checklist step 44: Linux, ntilde.list_sessions sees a windowless session ($(date -u '+%Y-%m-%d %H:%M:%S UTC')) ==="
say "NTILDE_APPDATA_ROOT=$NTILDE_APPDATA_ROOT; app $APP; MCP server $MCP"

say ''
say '=== K1. The GUI with Agent access (observe) on, and a second session no window shows ==='
xvfb-run -a -s '-screen 0 1280x800x24' "$APP" >"$SBX/gui.out" 2>&1 &
for _ in $(seq 1 150); do [[ -s "$DATA/mux/mux-endpoint.json" && -s "$DATA/agent-endpoint.json" ]] && break; sleep 0.2; done
sleep 2
GUI=$(app_gui_pids "$DATA" | head -1)
say "GUI pid ${GUI:-NONE}; agent endpoint: $(tr -d '\n' <"$DATA/agent-endpoint.json" 2>/dev/null)"
check 'the GUI started, its daemon and its agent endpoint are up under the sandbox root' $([[ -n "$GUI" && -s "$DATA/mux/mux-endpoint.json" && -s "$DATA/agent-endpoint.json" ]]; echo $?)
run mux ls
run mux spawn-for-test /bin/sh "\"$SBX/shells/hold.sh\" W"
W=$OUT
check 'a windowless session (setup: spawn-for-test)' $([[ "$W" =~ ^[0-9a-f-]{36}$ ]]; echo $?)
sleep 1

say ''
say '=== K2. ntilde.list_sessions and ntilde.read_screen over the MCP server ==='
TEXT=$(mcp ntilde.list_sessions '{}')
say 'ntilde.list_sessions answered:'
while IFS= read -r l; do say "    $l"; done <<<"$TEXT"
check "list_sessions lists the CLI-started session as local (windowless), on this computer" $(grep -F "| $W |" <<<"$TEXT" | grep -q '| this computer | local (windowless) |'; echo $?)
check "list_sessions lists the GUI's own pane, not as windowless" $(grep -vF "$W" <<<"$TEXT" | grep -E '^\| [0-9a-f]{8}-' | grep -vq 'windowless'; echo $?)
check 'the footnote reads "windowless: running in the multiplexer with no window here."' $(grep -q 'windowless: running in the multiplexer with no window here\.' <<<"$TEXT"; echo $?)
STEXT=$(mcp ntilde.read_screen "{\"paneId\":\"$W\"}")
say 'ntilde.read_screen on the windowless session answered:'
while IFS= read -r l; do say "    $l"; done <<<"$(head -6 <<<"$STEXT")"
check 'read_screen on the windowless id returns its screen (the t30-session-W line)' $(grep -q 't30-session-W' <<<"$STEXT"; echo $?)
