# ADR: Ntilde as an Agent Host

_Status: **Accepted** (2026-07-07) · Date: 2026-07-07 · Supersedes the strategic framing of
`docs/ghostty-gaps/ghostty_gap_roadmap.md`; amends Phases 4–5 of `docs/ROADMAP.md`._

---

## Context

### What changed in the industry

Since this project started, the terminal market has bifurcated:

1. **Pure emulators** competing on speed and correctness. Ghostty won this
   race: fastest rendering, strong correctness, a full-time famous maintainer,
   and default-recommendation status in every 2026 comparison.
2. **Agent-aware terminals** built to host CLI coding agents (Claude Code,
   Codex CLI, Gemini CLI). Warp rebranded itself an "Agentic Development
   Environment"; tools like cmux and Agent Deck exist purely to orchestrate
   agent sessions. CLI agents are now a mainstream developer workflow, and the
   terminal a developer chooses materially affects their agent workflow.

What agent-aware terminals compete on today: session observability (agents
reading screen state), session status detection (running / waiting-for-input /
idle), completion & stall notifications, programmatic session spawning, and
parallel-session orchestration. Most implementations are tmux-layer inference
or proprietary. None offer deterministic replay.

### Where Ntilde stands

- Chasing Ghostty on its own axis (speed + correctness as the headline) is
  not winnable for a nights-and-weekends solo project. The gap-closure
  program produced real quality, but as a *strategy* it is a treadmill.
- Ntilde's unusual investments map directly onto what agents need:
  - **Cell-based buffer + VT correctness** → structured, trustworthy screen
    reads ("the agent sees exactly what a human sees").
  - **Thread-safe PTY ownership** → real process/session status, not
    heuristics layered on tmux.
  - **Deterministic replay** → record and replay agent sessions with
    byte-for-byte buffer parity. No other terminal can do this.
  - **Existing MCP server** (`src/Ntilde.McpServer`) → the transport
    and packaging already exist; today it is repo-facing (docs, VT
    explanations, profile validation), not session-facing.
  - **Native SSH profiles** → agents can be given correct remote sessions,
    which no agent-aware terminal offers today.

## Decision

**Ntilde positions itself as the correct, deterministic terminal that is
the best host for AI agents — "enable, don't embed."**

Concretely:

1. Extend the MCP surface from *repo-facing* to *session-facing*: agents can
   observe, query status of, and (with permission) act inside live terminal
   sessions.
2. Do **not** build embedded AI (chat sidebars, model integrations, vendor
   bets). Command Assist remains the only user-facing AI-adjacent surface and
   keeps its existing opt-in constraints.
3. Demote Ghostty gap closure from an active program to a regression gate:
   the VT conformance CI lane stays blocking; new matrix cells are closed
   only when a real TUI application or agent workflow hits them.
4. Treat distribution (brew / winget / Flatpak) as a parallel first-class
   workstream: adoption goals fail on install friction before they fail on
   features.

## Consequences

- VT correctness stops being a me-too feature and becomes the trust
  foundation of the agent story.
- Replay gains a second audience: debugging *agent* behavior, not just
  terminal bugs.
- The McpServer grows a live control path and therefore a security model
  (below). It is no longer "read-only, stdio-only" once Milestone A3 ships;
  README and docs must be updated at that point.
- Roadmap Phases 4–5 (`docs/ROADMAP.md`) are effectively reframed by the
  milestones below; 5.2 Automation Hooks is absorbed into A2.

---

## Architecture

### Control channel

The MCP server is a standalone stdio process; live sessions exist in the
running App. The design is a **local IPC control endpoint hosted by the App,
proxied by the MCP server**:

```
agent (Claude Code / Codex / …)
   │  stdio MCP
   ▼
Ntilde.McpServer  ── local IPC (named pipe / unix socket) ──▶  Ntilde.App
                                                                    (session registry,
                                                                     buffer snapshots,
                                                                     PTY status, input)
```

- The stdio MCP server remains the single public surface; the IPC contract is
  internal and versioned.
- The App endpoint is loopback-only, per-user, and off by default
  (`Settings → Agent Access`).
- Buffer reads go through the existing snapshot boundary — the same
  deterministic serializer the replay/parity tests use. No new read path.

### Module placement (respects enforced invariants)

- Session-control contracts (DTOs, IPC protocol) live in a new leaf library
  (working name `Ntilde.AgentHost.Contracts`) referenced by both App
  and McpServer. `VT` stays a leaf; `Pty` still never sees VT types — status
  events surface via the existing App orchestration layer.
- No production assembly references test libraries; architecture tests gain
  assertions for the new edges before the first merge.

### Permission model

| Capability | Default | Gate |
|---|---|---|
| list sessions, read screen/scrollback, session status | off | user enables Agent Access (observe) |
| notifications (completion / stall) | off | same toggle |
| live indicators (pane status segment, tab marker, window light) | off | tied to observe / act; cannot be silenced separately |
| send input, spawn/close sessions & panes | off | separate explicit opt-in, per-profile allowlist for SSH |
| resize a single visible direct local pane (raw AgentHost `resizePane`) | off | separate explicit act opt-in; normal resizable window, active single-pane tab only |
| replay export | off | observe permission + explicit export action |
| screenshot (render a pane to PNG) | off | observe permission |

Every acting tool call is journaled to a visible activity log in the UI. A
screenshot is not: it acts on nothing, and the pane's own agent-access indicator
(A5 follow-up, #339) lights when an agent captures it, which is the surface a
user actually watches.
Nothing is silent — same principle as the credential-consent rules.

---

## Roadmap

Each milestone is independently shippable and announceable. Estimates assume
~5–15 hrs/week.

### A1 — Observe (first public demo)

**Deliverables**
- [x] App-side IPC endpoint + session registry exposure (PRs #183, #184)
- [x] MCP tools: `ntilde.list_sessions`, `ntilde.read_screen`
      (visible grid + cursor), `ntilde.read_scrollback` (ranged)
- [x] `Agent Access (observe)` settings toggle, off by default (PR #184)
- [ ] Docs + demo recording: Claude Code reading a live `htop`/`vim` session

**Acceptance criteria**
- Screen reads are snapshots from the deterministic serializer; a replay of
  the same byte stream yields an identical `read_screen` result (parity test)
- Zero behavior change when the toggle is off (architecture + integration test)

### A2 — Status & notifications

**Deliverables**
- [x] Session status model: `running` / `awaitingInput` / `idle` / `exited`
      with precise/heuristic confidence tiers (PTY-derived, not scrape-based;
      exit status included; foreground-process reporting deferred) (PR #186)
- [x] MCP tools: `ntilde.get_session_status`,
      `ntilde.wait_for_events` (cursor long-poll over a bounded event
      ring) for completion/stall events (PRs #187, #188)
- [x] In-app notification for long-running command completion, default-off
      toggle (absorbs ROADMAP 5.2); OS-native notification backends remain a
      follow-up

**Acceptance criteria**
- Status transitions covered by replay-driven tests (recorded fixtures per
  state)
- Stall detection has an explicit, documented definition (no output for N s
  while `running`) — no heuristics that can't be tested

### Controlled visible-pane resize

The raw AgentHost method `resizePane` accepts `{paneId, rows, cols}` with rows
1–128 and columns 1–256. It uses the separate default-off act permission and
journals attempts that reach the operation, including malformed parameters,
refusals and cancellations. Frames rejected before method dispatch, including
unsupported protocol versions, are not journaled.
It is not an MCP tool. Only the current single direct local pane in the selected
tab of a visible, normal, resizable window is eligible. Remote, mux/deferred,
windowless, split, inactive and focus-mode targets are refused without activating
a tab or directly resizing the VT buffer. Restored command-based local panes
remain eligible when their saved profile no longer resolves.

App changes its actual window client size using measured cell/chrome dimensions
and starts a two-second observation timeout for the ordinary view → buffer →
session resize path. Timeout observation and event cleanup run on the UI dispatcher;
this is a responsive-UI bound, not a hard request deadline. A stalled dispatcher
or contended buffer write lock can delay the response. Qualification callers need
their own transport deadline and disposable-process cleanup.

The result `{rows, cols, layoutSettled, exact}` contains observed buffer geometry. `layoutSettled` requires agreement with the view and a successfully
returned current-session resize callback. Changing the buffer or session clears
older completion evidence; `exact` additionally requires the requested
grid. Native window limits or a callback failure can produce partial results.
A busy buffer is sampled without waiting; unavailable observation returns
`resizeUnavailable`. Cancellation and timeout do not undo terminal reflow or
promise restoration. A qualification runner must restore and verify geometry
separately. Session callback completion does not attest the child's kernel PTY
size; independent PTY observation is required for that claim.

### Generated framework input (raw API)

`generatePaneInput` is a permissioned raw AgentHost method, with no MCP tool or
new setting. It accepts exactly one declared event per request: `keyDown`,
`keyUp`, `text`, `mousePress`, `mouseRelease`, `mouseMove` or `mouseWheel`.
The existing default-off act permission and visible-pane resize admission rules
apply. The target must be the current single direct local pane in the selected
tab of a visible, normal, resizable window. Deferred/mux, remote, windowless,
split, inactive, hidden, stale and dead targets are unavailable.

The BCL contract validates the following bounds before any UI work:

- `key`: keypad `0`–`9`, `decimal`, `separator`, `add`, `subtract`, `multiply`,
  `divide` or physical keypad `enter`, only for key events.
- `text`: exactly one character from `0123456789.+-*/,=` or carriage return,
  only for a separate standalone text event. Calls do not form a backend key/text callback pair.
- `modifiers`: bits shift=1, control=2, alt=4, meta=8; text requires zero.
- Pointer `column`/`row`: one-based viewport cells within 256×128 and the actual
  agreeing view/buffer grid. `button` is `left`, `middle`, `right`; move also
  permits `none`. Wheel accepts no held button and exactly one `wheelDelta`
  of +1 or -1. Unrelated/unknown fields and combined events are rejected.

App raises actual Avalonia routed key/text/pointer events at the TerminalView.
Mouse positions are measured cell centers translated into the window's event
coordinate space. Mouse events require active reporting immediately before the
TerminalView handler, so a disabled protocol cannot fall through into selection
or hyperlink activation. Such unavailable attempts are not no-byte evidence.
Synthetic keyboard events are unavailable while Kitty associated-text reporting
is enabled or any human key/text/held-key correlation is already pending. Admission and the terminal
handler recheck this bound, and the deferred-queue creation boundary independently
refuses synthetic work. This foundation does not retain authorization across
separate associated-text callbacks or schedule a generated fallback after return.
Each call owns only the correlation it creates on a clean UI-thread entry and
clears that state when it returns. A synthetic keyDown cannot suppress a later
human text-only commit. A separate text call is standalone, without cross-call
key/text pairing. Ordinary keyboard callbacks retain their existing behavior.

Window shortcut/broadcast commands, Command Assist interception and user-command history observers are
excluded during synthetic dispatch; terminal routing, keypad text-suppression,
mouse coordinate conversion/coalescing, wheel accumulation, encoding and normal
session transport remain in use. There is no native/global OS input injection,
selector lookup or endpoint-level text/byte send bypass.

A two-second request deadline cancels queued UI work. The permission,
registration, pane, buffer and session identities are checked before dispatch,
again at the TerminalView handler and after observation. Cancellation prevents
queued events from running after expiry, but cannot undo an event already being
processed or preempt synchronous framework/transport code. Attempts are recorded
in the existing activity journal as method/target/outcome only, without event
text or encoded bytes. Synthetic input does not feed command-history observers;
child output retains its existing recording policy.

The result reports `eventDispatched`, framework `handled`, `inputSubmitted`, boundary
`avalonia-routed`, actual buffer/view geometry, view-local pointer coordinates,
framework event modifiers, and post-dispatch parser mode samples (application keypad,
tracking 0/9/1000/1002/1003, SGR encoding and Kitty flags). Handled does not mean
bytes were emitted. `inputSubmitted` means a normal session-input call returned
or byte input was accepted; it controls the agent-write attention marker and
does not acknowledge delivery to the child. Mode samples are not an atomic encoder snapshot. Neither
result nor pane identity attests native input, fixture ownership or child byte
delivery. Qualification requires separate disposable-process/PTY proof and
independent child-byte capture; no stock or published terminfo result is regraded
by this API.

### A3 — Act (permissioned)

**Deliverables**
- [x] MCP tools: `ntilde.send_input` (PR #193), `ntilde.spawn_session`
      (local profile or SSH profile by name) + `ntilde.close_session` (PR #194)
- [x] Separate opt-in (`AgentAccessActEnabled`, default off) + per-profile SSH
      allowlist (`SshProfile.AllowAgentAccess`, fail-closed) + UI activity journal
      (`AgentActivityJournal` + "Agent Activity…" window; every attempt, allowed or
      denied, is recorded) (PRs #193–#195)
- [x] Threat-model doc for the acting surface
      (`docs/agent-host/2026-07-12-acting-threat-model.md`, PR #195)

**Acceptance criteria**
- [x] Acting tools hard-fail when only observe permission is granted (tested:
      `actDisabled`; SSH also `profileNotAllowed`)
- [x] Input injection is byte-faithful. New manual and flight recordings retain
      output only, never typed input (#671); legacy input events remain readable.
      (goes through the session's normal `SendInput`; PtySmoke verifies delivery
      and the absence of input events in the recording)

**Milestone A3 complete.** Design: `docs/plans/2026-07-12-agent-host-a3-act-design.md`.

### A4 — Replay for agents (the moat)

**Deliverables**
- [x] `ntilde.export_replay` for a session/time range (PRs #190–#191:
      flight-recorder ring bounded by bytes = the retained time range; output +
      resize only, never input; gated by the default-off `AgentReplayExportEnabled`
      sub-toggle on top of observe)
- [x] CLI: `Ntilde.Cli --replay <file> [--attributes]` headless
      render-to-text for CI and agent-run postmortems (PR #192; PNG output and
      frame-stepping deferred — see the A4 design doc's out-of-scope list)
- [x] Docs: "Debug what your agent did, frame by frame" (README, agent host
      program section)

**Acceptance criteria**
- Exported replays round-trip through the existing replay runner with
  byte-identical buffer snapshots on all three OSes

### A5 — Screenshots (see what the agent sees)

**Deliverables**
- [x] `ntilde.capture_screen`: captures a pane as a PNG and returns its
      path, plus the image inline on request (size-capped, `maxWidth`
      downscale). Rides the observe toggle like every other read; the pane
      indicator is its visibility surface.
- [x] Two modes, as the design doc specified: `render` (default, deterministic,
      works on hidden panes) and `live` (WYSIWYG through the UI-thread bridge,
      visible panes only, carries the background image and window opacity).
- [x] Caller-named `scale` (1–3) so an agent can render text large enough to
      read back out of the image. Named by the caller, never taken from the
      monitor, so a capture stays reproducible. Unblocked by #346.
- [x] Optional controlled offscreen captures: `presentationTimeMs` selects a synthetic
      text/cursor phase; `includeRenderDetails` returns actual primary-font identity,
      palette, snapped geometry and presentation facts. Both reject `live` mode and leave
      the live clock/terminal untouched. Defaults retain fixed-visible phases. See
      [controlled capture](../qa/terminfo/controlled-capture.md) for provenance limits.
- [x] The snapshot renderer (`TerminalSnapshotRenderer`) promoted out of test
      infrastructure into the App, so the golden-PNG baselines, the agent
      capture path, and any future CLI PNG output are one code path. Renders
      through the real `TerminalDrawOperation` with no window, visual tree, or
      GPU surface — a minimized or occluded window captures identically, and
      nothing outside the pane can appear in the image.

**Acceptance criteria**
- [x] Two captures of an unchanged buffer are byte-identical at a given scale
      (scale named by the caller rather than taken from the monitor, cursor
      follows the buffer's mode rather than a blink phase, no selection or
      render HUD)
- [x] `render` mode hard-fails with `captureUnavailable` for a pane that has not
      been measured, and for one whose grid exceeds the pixel budget *in device
      pixels* — the budget is squared by scale, so a grid that fits at 1x can
      miss at 3x
- [x] `live` mode hard-fails with `captureUnavailable`, pointing back at
      `render`, when there is no window or the pane is not on screen
- [x] An unrecognized mode is a malformed request rather than being coerced to
      the default

### Windowless sessions (multiplexer)

With session persistence on, the agent host also serves **windowless** sessions: sessions in the
multiplexer that no pane of this window shows, including ones another process attaches to. The
observe and act tools take their id (the multiplexer session id) like a pane id, and the pane
registry wins when both name the same id.

- **Reach (R4).** Only endpoints the window is already connected to are asked, so the agent path
  never connects or prompts. Every windowless operation has a 7 s budget inside the MCP round trip;
  an unreachable daemon gives `sessionNotFound`, and a daemon too old for `readScreen` gives
  `unsupported`.
- **Acting (R4).** `send_input` and `close_session` on a remote endpoint's session need that SSH
  profile's agent allowlist.
- **Journal (R5).** Windowless reads are journaled and light the window-level agent light, because
  there is no pane indicator to show them; repeated reads fold into one "×N" entry. Pane reads stay
  unjournaled.
- **Limits.** Scrollback is limited to the newest 2000 rows. Windowless sessions emit no events, so
  `wait_for_events` never mentions them. Status is heuristic. Render capture borrows an open pane's
  font metrics, and live capture is refused (`captureUnavailable`).

### Parallel track — Distribution

- [ ] Homebrew cask/formula (macOS, Linux)
- [ ] winget manifest (Windows)
- [ ] Flatpak (Linux)
- [ ] Each A-milestone announcement links a one-command install

---

## Non-goals

- Embedded chat UI, model API keys, or any model-vendor dependency
- Agent orchestration UI (fleets, worktree-per-task) — v1 hosts sessions;
  orchestrators like cmux can build *on* the MCP surface
- Remote (network-exposed) control endpoint
- Weakening any existing invariant: determinism, parity, replayability, and
  the zero-flicker rule gate this work exactly as they gate everything else

## Final rule (unchanged)

> **If it is not testable, it is not shippable.**

Agent-facing reads and events are only trustworthy because they are built on
the deterministic core — that is the product.

### Raw read-only pane metadata

`readPaneMetadata({paneId})` observes an attached, visible, current, direct local
pane through the existing observe endpoint; it requires no act opt-in. It does
not activate a tab, type input, open a hyperlink or create a notification.
An unknown/windowless identity returns `sessionNotFound`. An unavailable UI,
hidden/remote/mux-backed/replaced pane, busy parser or excessive snapshot returns
`paneMetadataUnavailable`, never an empty substitute for absent evidence.

The result contains raw OSC title, host cwd, display title, a bounded active-buffer
projection (geometry, cursor, links, input-window/anchor facts), and actual UI
properties (focus/settings, tab text, stored progress with owner, optional actual
vertical progress bar, window-global toast visibility/title/message). Link run
coordinates are zero-based in the active viewport, excluding scrollback. Link
`identity` values are meaningful only within that snapshot. Shell anchors are raw
stored coordinates: consumers must check screen, history generation and eviction
before resolving them. Main-history count, generation and eviction counters are
included even while alternate is active; alternate anchors use their own screen
coordinates. These counters do not describe alternate-screen history.

The buffer portion is captured under one immediate read lock. UI properties are
read on the UI dispatcher; asynchronous parser-to-UI effects may still be pending.
These components are not a globally atomic transaction. Clients qualifying an
effect must compare actual host values after their controlled output and retain
independent controls. A response receipt or consumed marker does not prove it.

Budgets are 8,192 cells, 128 complete link runs, 4,096 UTF-16 code units per
metadata field and 32,768 combined text code units, including repeated run URIs.
Nothing is truncated. The endpoint applies a two-second await deadline and
revalidates the registration/session after completion; clients should also bound
their transport. No new fields appear in existing method responses. This API
foundation adds observation, not live protocol qualification or a stock grade.
UI visibility is Avalonia control visibility, not proof of occlusion or paint;
OS notifications/taskbar state and pixel output require separate evidence.
