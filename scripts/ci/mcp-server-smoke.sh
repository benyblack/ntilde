#!/usr/bin/env bash
# Drives a real MCP stdio session against a published ntilde-mcp binary, the way Claude Code would.
# Usage: mcp-server-smoke.sh <ntilde-mcp binary> <src/Ntilde.McpServer/Tools dir>
#
# release.yml runs this on every platform's NativeAOT ntilde-mcp before the binary joins a bundle. The
# E2E tests (McpServerStdioE2ETests) launch the JIT-compiled DLL, with full metadata, so nothing else
# runs the AOT binary users get - and AOT breaks quietly: a tool whose registration was trimmed away is
# simply absent from tools/list, and a parameter the binder cannot deserialize fails only when called.
# So this asserts:
#   - the handshake completes, which also proves nothing but JSON-RPC reached stdout;
#   - tools/list returns exactly as many ntilde.* tools as the source declares (counted from the
#     [McpServerTool(Name = "...")] attributes, so adding a tool needs no edit here);
#   - a call with a string parameter (explain_escape_sequence, which also loads the source-generated
#     VT capability manifest) and a call with an injected service (list_sessions, answered with the
#     "not running" instructions here, as no app is up) both return a non-error result;
#   - the server exits once stdin closes.
#
# Portable to macOS's bash 3.2, Git Bash on Windows and a bare ubuntu container: no `timeout`, no jq.
set -eu

bin="${1:?usage: mcp-server-smoke.sh <ntilde-mcp binary> <Tools source dir>}"
tools_dir="${2:?usage: mcp-server-smoke.sh <ntilde-mcp binary> <Tools source dir>}"
[ -x "$bin" ] || { echo "::error::$bin is missing or not executable" >&2; exit 1; }

expected="$(cat "$tools_dir"/*.cs | grep -o 'McpServerTool(Name = "ntilde\.[a-z_]*"' | sort -u | wc -l | tr -d ' ')"
[ "$expected" -gt 0 ] || { echo "::error::found no [McpServerTool] declarations under $tools_dir" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
out="$work/stdout"; err="$work/stderr"; done_flag="$work/done"
: > "$out"

# Requests go in one by one and stdin stays open until the responses are read: closing it right after the
# last write lets the server shut down with requests still in flight. done_flag releases the writer.
{
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ntilde-release-smoke","version":"1"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ntilde.explain_escape_sequence","arguments":{"sequence":"CSI ?25h"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"ntilde.list_sessions","arguments":{}}}'
  i=0
  while [ ! -f "$done_flag" ] && [ "$i" -lt 60 ]; do sleep 1; i=$((i + 1)); done
} | "$bin" > "$out" 2> "$err" &
pid=$!

response() { grep "\"id\":$1[,}]" "$out" || true; }

i=0
while [ "$i" -lt 60 ]; do
  if [ -n "$(response 2)" ] && [ -n "$(response 3)" ] && [ -n "$(response 4)" ]; then break; fi
  sleep 1; i=$((i + 1))
done
touch "$done_flag"

# Clean exit on EOF, with a kill as the backstop so a hung server cannot hang the job.
i=0
while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 15 ]; do sleep 1; i=$((i + 1)); done
exited=1
if kill -0 "$pid" 2>/dev/null; then exited=0; kill "$pid" 2>/dev/null || true; fi
wait "$pid" 2>/dev/null || true

dump() {
  echo "---- stdout ----" >&2; cat "$out" >&2
  echo "---- stderr (tail) ----" >&2; tail -n 40 "$err" >&2
}
fail() { echo "::error::$1" >&2; dump; exit 1; }

[ -n "$(response 1)" ] || fail "no initialize response: the server did not start, or wrote something other than JSON-RPC to stdout"
grep -q '"serverInfo"' <<EOF || fail "the initialize response carries no serverInfo"
$(response 1)
EOF

listed="$(response 2 | grep -o '"name":"ntilde\.[a-z_]*"' | sort -u | wc -l | tr -d ' ')"
[ "$listed" -eq "$expected" ] \
  || fail "tools/list returned $listed ntilde.* tool(s), the source declares $expected: a tool was trimmed out of the AOT binary or failed to register"

for id in 3 4; do
  r="$(response "$id")"
  [ -n "$r" ] || fail "no response to tools/call id $id"
  case "$r" in
    *'"error":'*|*'"isError":true'*) fail "tools/call id $id failed: $r" ;;
  esac
  grep -q '"type":"text"' <<EOF || fail "tools/call id $id returned no text content: $r"
$r
EOF
done

[ "$exited" -eq 1 ] || fail "the server was still running 15s after stdin closed"

echo "ok: $bin completed the MCP handshake, listed all $expected tools, answered both tool calls, and exited on EOF"
