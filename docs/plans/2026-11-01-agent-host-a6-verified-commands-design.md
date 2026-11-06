# Agent Host A6 (Verified Commands) Design

> **Status: proposed (2026-11-01).** Not yet scheduled. Proposed milestone **A6** of
> `docs/agent-host/DIRECTION.md`.

An agent that says "the tests pass" is reporting on its own work. A gate that approves on that
report is two optimists agreeing. A6 lets the terminal answer the question instead: *did this
command run, where, when, and with what exit code* — as a record the agent did not write and cannot
improve by being confident.

## Goal

- **`ntilde.run_check`** (act tier) — run a command as its own process in a dedicated, visible pane
  and return a verdict whose exit code comes from the operating system, not from the agent and not
  from bytes in the output stream.
- **`ntilde.command_history`** (observe tier) — a per-pane ledger of the commands shell
  integration saw finish: command text, working directory, start/finish time, exit code, who typed
  it, and integrity flags.
- **A Checks list in the Agent Activity window** — the place a human reads the terminal's record
  at the one step they approve (the merge), instead of the agent's summary of it.

Positioning stays inside the DIRECTION non-goals: these are primitives an orchestrator builds a
gate on, not an orchestration UI.

The change must:

- be additive at protocol version 1; no existing method changes shape
- add no new path into `App` from `McpServer` (contracts stay in `Ntilde.AgentHost.Contracts`)
- keep VT a leaf: integrity bookkeeping lives in the App, fed by the parser's existing callbacks
- state exactly what a record proves and what it does not (below), in the tool descriptions too

## Current State (verified)

- **Shell marks.** The zsh bootstrap emits `OSC 133;C;<base64 command>` from `preexec` and
  `OSC 133;D;<exit>;<duration_ms>` from `precmd`
  (`ZshBootstrapBuilder.cs` ~L198–L230). `AnsiParser.ShellIntegration.cs` parses both and already
  tolerates `key=value` attributes on marks.
- **Wiring.** `TerminalPane.Session.cs` forwards `OnCommandAccepted` to
  `StatusMachine.NotifyCommandAccepted` + `NotifyCommandStarted`, and `OnCommandFinished` to
  `NotifyCommandFinished`. The command text is only kept when
  `IsShellIntegrationConsumptionEnabled` (a comment there already notes that a C payload can be
  written by the far end of an SSH link or by `cat` of a crafted file).
- **The command/exit-code link is lost.** `AgentSessionStatusMachine.NotifyCommandFinished` clears
  `_currentCommand`, and `AgentEventDto` has no command field. An agent can only pair an exit code
  with a command if it happened to read `currentCommand` while the command was running.
- **Events are ephemeral and shared.** `AgentEventRing` holds 256 events for the whole endpoint
  (`AgentHostProtocol.EventRingCapacity`); one chatty pane evicts everyone's `commandFinished`.
  `wait_for_events` long-polls cap at 25 s (`MaxWaitForEventsTimeoutMs`).
- **No mark authentication.** Shell-integration marks are in-band. Any byte written to the PTY —
  a child process, a remote host, `printf` typed by an agent — can produce a `133;D;0`.
- **Process exit is out-of-band.** `ProcessExited` (`TerminalPane.Agents.cs` L351) carries the
  OS exit code to `StatusMachine.NotifyExited`. Nothing in the output stream can change it.
  `MainWindow.OnPaneProcessExited` closes a cleanly-exited pane per `ShellExitPolicy` unless it is a
  mux share of an exited session.
- **Other pieces in place.** `CurrentWorkingDirectory` from OSC 7; `AgentOutputRegionTracker`
  reads the output region synchronously at D; `SecretsFilter` (`Ntilde.CommandAssist`) redacts
  command text for Command Assist; `AgentActivityJournal` records every act; profiles carry
  `StartingDirectory`.
- **Blind spot.** Agents' built-in shell tools (Claude Code's Bash tool, Codex's exec) run in
  their own subprocesses, not in an Ntilde pane. Nothing they run reaches any of the above. Today
  "verify" therefore means "the gate runs the check itself, in a pane", which is the `run_check`
  shape below.

## What a record proves

| A record proves | A record does not prove |
|---|---|
| This command text was run (`run_check`: by construction; history: as the shell reported it) | That exit 0 means what the caller thinks — `cmd \| tail`, `cmd \|\| true`, a script that swallows failures all exit 0 |
| The exit code (`run_check`: from the OS; history: from the shell's D mark) | Which code was tested — the record names a directory, not a commit (see Open questions) |
| Start/finish times on Ntilde's clock, and the working directory | Anything about commands run outside Ntilde panes |
| Who typed it: agent input, user keystrokes, or both (history) | That a deliberately adversarial agent with act access could not forge a history record (see Integrity) |

The left column is what a gate needs from the terminal. The right column is the caller's job, and
`run_check` helps with the first row by warning (below).

## Design

### `run_check`: the check as its own process

`run_check` does **not** type into an existing shell. It spawns a new pane whose program is the
profile's shell in non-interactive mode, running the command:

```
<profile shell> -c '<command>'        # POSIX shells
pwsh -NoProfile -Command '<command>'  # PowerShell profiles
```

with the working directory set to `cwd` (default: the profile's `StartingDirectory`). The verdict is
the pane's `ProcessExited` code. Because the exit code arrives from the OS, nothing the command
prints can change it, and no shell-integration nonce is needed for this path. The command text is
the string the caller passed, recorded by the App before the process starts.

- **Visible.** The pane opens as a tab titled `check: <command, truncated>` with the agent-access
  indicator lit, so the user sees every check as it runs.
- **Kept open.** A check pane never auto-closes on exit, whatever `ShellExitPolicy` says (same
  carve-out as `MuxShareOfExitedSession`): the user can read the output that produced the
  verdict. It is a normal pane; the user closes it, or the agent does with `close_session`.
- **Output.** At exit the App reads the pane's buffer through the existing snapshot boundary and
  stores `outputSha256` and line count; `includeOutput: true` also returns the last 200 lines,
  passed through `SecretsFilter`. The full output stays in the pane and in the flight recorder, so
  `export_replay` covers the run.
- **Environment.** The check runs with the profile's environment, not the agent's or a
  particular interactive shell's (no venv activation, no exported variables from another pane).
  This is a feature for reproducibility; the tool description says so.
- **Long checks.** Test suites run for minutes, past any single MCP call. `run_check` waits up to
  `waitMs` (capped at the existing 25 s long-poll ceiling) and returns either the finished record
  or `state: "running"` with a `checkId`; `ntilde.get_check(checkId, waitMs)` long-polls the same
  way until it finishes.
- **Hygiene warnings.** The App scans the command text and attaches warnings, never refusals, for
  shapes whose exit code is unlikely to mean anything: a top-level `|` without `pipefail`,
  `|| true`, `|| :`, `; true`, a trailing `&`. These are lexical heuristics and documented as
  such. They exist because "write the check so a program can fail it" is the caller's job, and a
  check that cannot fail is the most common way to get a confident green.

Gating: act tier (`AgentAccessActEnabled`), because it runs a command. SSH profiles need
`AllowAgentAccess` like `spawn_session`. Every call — allowed or denied — goes into
`AgentActivityJournal` as `runCheck` with the command text as its target.

### `command_history`: what the shell saw

A new `AgentCommandLedger` in the App holds one `CommandRecord` per C→D pair, for every pane with
shell integration. It is endpoint-wide, bounded (1000 records), in memory only, and separate from
the event ring, so status churn cannot evict check results. Records outlive their pane until
evicted.

```
CommandRecord
  id                long     monotonic across the endpoint
  paneId            Guid
  command           string?  from the C payload; null for a bare 133;C
  cwd               string?  CurrentWorkingDirectory at the C edge
  startedAtMs       long     Ntilde's clock at C
  finishedAtMs      long     Ntilde's clock at D
  exitCode          int?     from D
  origin            "agent" | "user" | "mixed" | "unknown"
  depth             int      0 for the pane's own shell; >0 inside a nested C
  integrity         "attested" | "unattested" | "anomalous"
  anomalies         string[] e.g. duplicateFinish, finishWithoutStart, nestedStart, nonceMismatch
```

**Origin.** Between the B mark and the C edge the pane tracks where input bytes came from: the
agent `sendInput` path or the keyboard. Only agent bytes → `agent`; only keyboard → `user`; both →
`mixed`; no B/C bracket → `unknown`.

**Exposure.** `get_session_status` already exposes the in-flight command at observe tier, so
history is observe tier too. The difference is that history is retrospective, so every `command`
goes through `SecretsFilter` before it leaves the App. `commandFinished` events gain an additive
`recordId` (and `command`, filtered), so an agent watching `wait_for_events` can pair an exit code
with its command without a race.

### Integrity of shell-mark records

`run_check` does not depend on any of this; history does.

1. **Sequence rules (phase 1).** The ledger builds records only from a C followed by a D in the
   same pane. Shapes that a real shell does not produce on its own are flagged, not dropped:
   a second D for one C (`duplicateFinish` — what `printf '\e]133;D;0\a'` as a command produces,
   since the shell's own precmd emits a D after it), a D with no open C (`finishWithoutStart`), a C
   while one is open (`nestedStart`). Nested starts are legitimate inside a nested interactive
   shell or an SSH hop with remote integration, so the inner record gets `depth: 1` and
   `unattested`, not `anomalous`, unless another rule fires.
2. **Per-session nonce (phase 3).** For local shells Ntilde launches with its own bootstrap, the
   App generates a 128-bit token per PTY session and passes it as `NTILDE_SI_NONCE`. The bootstrap
   copies it into a non-exported shell variable and unsets the environment variable, so child
   processes do not inherit it. The C and D hooks append `;nt=<token>`. A pair whose marks both
   carry the matching token is `attested`; a pair with no token (third-party snippets, remote
   hosts, old bootstraps) is `unattested`; a wrong token is `anomalous`. The token never appears
   in tool output, and `export_replay` rewrites `nt=` attributes to `nt=redacted` (marks do not
   touch grid content, so buffer parity is unaffected).

What the nonce does not stop: an agent with act access can type `echo $__ntilde_si_nonce` into the
same shell and read it. That needs a deliberate command, which `AgentActivityJournal` records, and
it is out of scope by the same reasoning as the A3 threat model's local allowlist: a local shell is
already reachable by any process running as the user. The threat this design defends against is
the optimistic agent and hostile output, not a determined local adversary. For that,
`run_check` is the answer: its verdict never came from the stream.

### Protocol (additive; version stays 1)

| Method | Tier | Params | Result |
|---|---|---|---|
| `runCheck` | act | `command`, `profile?`, `cwd?`, `waitMs?`, `includeOutput?` | `CheckResult` |
| `getCheck` | act | `checkId`, `waitMs?`, `includeOutput?` | `CheckResult` |
| `commandHistory` | observe | `paneId?`, `sinceId?`, `limit?` (default 50, cap 200) | `CommandRecord[]`, `nextId`, `oldestId` |

`getCheck` sits on the act tier because a check is the act's result; reading it from observe would
let an observe-only client learn the outcome of commands it could not have run.

```jsonc
// CheckResult
{
  "checkId": "c-41",
  "paneId": "…",
  "state": "finished",           // running | finished | aborted (pane closed before exit)
  "command": "scripts/build.sh test tests/Ntilde.VT.Tests",
  "cwd": "/Users/…/ntilde",
  "profile": "zsh",
  "startedAtMs": 1761998400000,
  "finishedAtMs": 1761998652000,
  "exitCode": 0,                 // from ProcessExited; null unless finished
  "verdictSource": "processExit",
  "outputSha256": "…",
  "outputLines": 1843,
  "outputTail": null,            // populated when includeOutput
  "warnings": [],                // e.g. ["pipeWithoutPipefail"]
  "replayAvailable": true
}
```

New error codes: `checkNotFound`, `checkUnavailable` (pane failed to spawn; carries the reason).

### MCP tools

`ntilde.run_check`, `ntilde.get_check`, `ntilde.command_history`, documented in
`docs/mcp/tools.md`. Each description carries the "proves / does not prove" table in one
paragraph, so an agent reading the tool list knows exit 0 from `cmd | tail` is not a pass.

### UI: the Checks list

The Agent Activity window gains a **Checks** tab: one row per `run_check`, with command, profile,
cwd, exit code, duration, warnings, and a button that focuses the check pane if it is still open.
History records are not listed there (they are not acts); the tab is the human's evidence at the
approval step, and stays short for that reason.

## Alternatives considered

- **Only add `command` to `commandFinished` events.** Cheap, and shipped as PR 1 below, but alone
  it inherits the ring's eviction, carries no integrity information, and only helps an agent that
  was watching at the time.
- **`run_check` by typing into an existing shell** (`send_input` + match the next C/D pair). It
  keeps the user's environment, but the verdict then rests on in-band marks, the input line has to
  be empty (or the command concatenates with pending text), and any `printf` in the command's
  output can fake the D. The dedicated process removes all three problems. Revisit as an option if
  "run it in my shell" turns out to matter.
- **A wrapper binary (`ntilde-check -- cmd`) that reports to the App over IPC.** As strong as
  the dedicated process and usable from an agent's own Bash tool, but it needs a second
  authenticated client of the local endpoint and a way to stop the agent from calling the IPC
  directly with a made-up result. Deferred; the dedicated pane gets the same guarantee with no new
  trust boundary.
- **Confidence scores on records.** Rejected: integrity is a fact about how the record was built
  (`processExit`, `attested`, `unattested`, `anomalous`), not a number to put a threshold on.

## Testing

- `AgentCommandLedger` unit tests with an injectable clock: C→D pairing, eviction, each anomaly
  rule, nesting depth, origin tracking across mixed input.
- Replay-driven tests: recorded fixtures for a clean command, `printf '\e]133;D;0\a'` as a command
  (`duplicateFinish`), a fake full C…D run inside a real command (`nestedStart`), and a nested
  `zsh` session (`depth: 1`, not anomalous).
- `run_check` PtySmoke: `exit 3` → exitCode 3 with `verdictSource: processExit`; a command that
  prints a forged `133;D;0` and then exits 1 → exitCode 1; the check pane stays open under
  `ShellExitPolicy` close-on-clean-exit; closing the pane mid-run → `aborted`.
- Hygiene warnings: a table test per pattern, including the quoted cases that must not warn
  (`grep 'a|b'`).
- Gating: `runCheck`/`getCheck` → `actDisabled` with observe only; SSH profile without
  `AllowAgentAccess` → `profileNotAllowed`; every attempt journaled.
- Nonce (phase 3): bootstrap unsets the env var (a child `env` does not show it); `export_replay`
  output contains no token; marks with a wrong token → `anomalous`.
- Drift guard: the MCP tool descriptions contain the "does not prove" paragraph.

## Out of scope

- Git state in the record (commit, dirty tree). See Open questions.
- Persisting the ledger or checks across app restarts. In memory, like the activity journal.
- Windowless sessions: they emit no events today, so they have no history; `run_check` always opens
  a windowed pane.
- Verifying commands run by an agent's own shell tool outside Ntilde.
- Any gate or orchestration logic. Ntilde reports; the caller decides.

## Open questions

1. **Which commit was tested?** A check that says "exit 0 in `~/ntilde`" does not say which tree.
   Options: the App reads `.git/HEAD` and the ref it points to under `cwd` (a file read, no git
   dependency; cannot see a dirty tree), or `run_check` takes an optional `expectHead` and runs
   `git rev-parse HEAD` in the check pane before the command (stronger, but changes what the pane
   runs). Leaning toward the file read plus a `dirtyUnknown: true` flag.
2. **Remote nonce.** The remote bootstrap is injected over the SSH channel by Ntilde, so the token
   could be embedded in the script text rather than the environment. Worth it only once remote
   history is something a gate reads.
3. **Check panes in a hidden or background window.** Opening a tab the user may not be looking at
   is the point (visibility), but a flood of check tabs is not. A cap on concurrent open check panes
   per agent connection may be needed.

## Suggested PR slicing

1. **Events carry the command.** `commandFinished` gains `command`; the status machine reads the
   command into the event before clearing it. Command text leaves the App through `SecretsFilter` on
   both surfaces that carry it, this event and `get_session_status`'s `currentCommand` (which was
   unfiltered before). `recordId` waits for PR 2, where there is a ledger for it to point into.
   Small, immediately useful.
2. **Ledger + `command_history`** with sequence-rule integrity and origin tracking.
3. **`run_check` / `get_check`**, the check pane (keep-open carve-out, title, indicator), journal
   entries, hygiene warnings, and the Checks tab.
4. **Per-session nonce** for the shipped zsh/bash/fish/PowerShell bootstraps, plus replay redaction.

PR 3 delivers the gate-grade guarantee on its own and could ship before PR 2 if the "verify the
check" use case matters more than history.
