# ntilde multiplexer — Phase 5 (sync with main, default decision, agent host, update survival, release)

Status: in progress. The brief below is the maintainer's, verbatim. §R records the rulings made while
executing it (the brief's "Decisions you may make", and conflicts the surveys found), each with its reason.
The implementation plan is `docs/superpowers/plans/2026-10-08-ntilde-mux-phase5.md`.

## Why

Phases 0–4 plus two hardening PRs are merged into `dev-mux` (8033845). The multiplexer is
feature-complete for its three goals: local shells survive window close and restart; several
windows and `ntilde mux attach` share sessions; SSH tabs survive network drops via a remote
`ntilde-mux` daemon. Everything is behind `SessionPersistence` (default Off) plus a per-profile
remote flag. Phase 5 turns it into a shipped product: bring the branch back onto `main`, decide
and implement the default, make sessions survive app updates, let agents see windowless sessions,
close the remaining review items, and merge to `main`.

Read first: `CLAUDE.md`, `AGENTS.md`, `docs/ARCHITECTURE.md` §8.1, `docs/MODULE_OWNERSHIP.md`,
`docs/USER_MANUAL.md` §3.3, the Phase 4 spec `docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md`
(§14 follow-ups, §15 as-built, post-merge entries), PR #509's "Deliberately not changed" list,
`docs/agent-host/DIRECTION.md` and `docs/mcp/tools.md` (the agent host you will extend),
`src/Ntilde.App/Update/*` (Velopack apply path), `docs/CONFIG_STORAGE_CONTRACT.md`.
Build/test only via `scripts/build.sh` / `scripts/build.ps1`; one project at a time; App.Tests lanes
separately with `--blame-hang-timeout 5m`. **Work on `dev-mux` (or a branch off it); the final
deliverable is a PR from `dev-mux` to `main`.** Do not touch `claude/ntilde-multiplexer-mbxx9b`.

## 0. Sync with `main` first (its own PR into `dev-mux`)

`origin/main` is 81 commits ahead of the merge base (`9585a98`). Merge `origin/main` into
`dev-mux` before any Phase 5 work. Known hotspots to resolve by hand, never by taking one side
wholesale:
- **VT write-lock wait helpers** (`7a1c364`, `f4f7c25`): "never pump messages while waiting for
  the buffer write lock", "keep VT free of native interop; App owns the non-pumping wait". Phase 0's
  `TerminalBuffer.StateTransfer.cs` takes the write lock in `ImportState`; the headless mux sessions
  and the text client take it off the UI thread. Make sure the new wait helpers are used (or
  correctly bypassed) everywhere Phase 0–4 code takes that lock, and that the App-owned
  non-pumping wait is not reachable from `Ntilde.Mux` (layering).
- **Reflow streaming** (`988622a`, `1c81fab`): "stream the reflow instead of materialising the
  scrollback 4x", "hand old scrollback pages back as the reflow consumes them". Phase 0's
  `ExportState` walks scrollback pages; Phase 1's `HeadlessTerminalSession` resizes on the parse
  thread. Re-run the full snapshot/tail parity suite (`tests/Ntilde.VT.Tests/StateTransfer/`) and the
  Mux equality suites after the merge; any divergence is a merge bug.
- **Inline image payload pinning** (`7455935`), **idle GC** (`3c50951`…`1516e5c`), **tab-list menu
  lifetime** (`f5892d1`): check interactions with mux panes (image decoder is null on mux panes;
  idle GC must not run on the daemon's parse threads; tab-list menus with shared/detached tabs).
- Architecture tests on both sides must pass unchanged.
Report the conflict list and how each was resolved. Run every suite on the merged tree before
starting section 1.

## 1. The default (decision + implementation)

**Recommendation:** flip `SessionPersistence` to `"KeepOnClose"` for **local** shells in the release
that merges to `main`; keep remote persistence per-profile opt-in. Implement so that the flip is a
one-line change plus the UX below, and let the user make the final call in the PR.

Required UX for a default-on world (today's behaviour was designed for opt-in users who know what
they enabled):
- **First-close notice**: the first time a window closes with live local sessions, a dialog
  "Your shells keep running in the background. Reopen ntilde to get them back." with
  [Keep running] [Close them] and "Don't ask again". Remembered as a setting-free flag in app data.
- **Quit and close all shells** command (palette + Settings link) that kills every local session
  and the daemon (`kill-server`) on exit.
- Settings row copy and the manual rewritten for a default-on reader (what runs, how to see it,
  how to turn it off), and a one-line README feature bullet.
- Startup with orphans/detached: the existing toasts stay; make sure a brand-new user who closed
  the window yesterday sees their shells return with no toast spam.
- Migration: an existing settings.json with no `SessionPersistence` key gets the new default;
  one with an explicit `"Off"` keeps Off. Test both.

## 2. Sessions survive app updates (cheap path)

Today `ApplyStagedUpdateAsync` sends `shutdown` and the daemon dies with every shell (PR #489).
Change the rule: **keep a protocol-compatible daemon running across an update.** On apply, if the
live daemon's `[MinProtocol, MaxProtocol]` overlaps the new build's range (read from the descriptor
or `hello`), do not shut it down; after restart the new GUI reattaches as after any launch. Show
"Multiplexer is from the previous build; restart it when convenient" with a "Restart multiplexer
now" action (which is today's warn + `kill-server` path). Shut down only when ranges do not overlap.
Verify on Windows that Velopack can replace `current\` while `Ntilde.exe mux serve` runs from it
(the running image is mapped; Velopack renames/moves the old dir); if it cannot, the daemon must be
started from a copy outside `current\` (e.g. `<app-data>/bin/<version>/`) — decide after measuring.
Same rule for remote daemons (already a separate binary version). The full fd-passing hand-off
(SCM_RIGHTS on Unix) is **not** in scope; record it as the follow-up with the Windows caveat.

## 3. Agent host sees windowless sessions

The agent host (`src/Ntilde.App/AgentHost/AgentHostService.cs`, MCP tools in `docs/mcp/tools.md`)
lists and reads only panes. Add, additively (protocol stays Min 1 / Max 2 with new optional methods
a v1 daemon answers with `protocol_error`, which the client maps to "unsupported"):
- Mux method `readScreen {sessionId, maxScrollbackRows}` → the headless buffer as a
  `TerminalStateSnapshot` (reuse `CaptureSnapshot`), plus `status {running, exitCode, hasChildren,
  attachedClients, interactiveClients, title, cwd}` already in `sessionInfo`.
- `ntilde.list_sessions` includes daemon sessions with no pane (local and remote endpoints the GUI
  knows), marked `windowless: true`; `ntilde.get_session_status` and `ntilde.read_screen` work on
  them under the observe opt-in; `ntilde.send_input` and `ntilde.close_session` under the act
  opt-in (same allowlist rules as panes; SSH profiles honour the per-profile act allowlist).
- `capture_screen render` mode on a windowless session renders from the snapshot (the renderer
  already works from a buffer); `live` mode is refused with `captureUnavailable`.
- Indicators: the pane-level agent indicators have no pane here; log to the agent journal and show
  the window-level light as today.
- Tests in `tests/Ntilde.McpServer.Tests` and App.Tests with an in-memory daemon.

## 4. Remaining review items (fixes, each with a pinning test)

From PR #509's "Deliberately not changed" and my Phase 4 review nits:
- Every UI-thread send that can block on a full outbound queue: local `KillMuxSessionOnClose`,
  Detach/Leave, Reconnect's `faulted.Kill()`, input and resize. Generalise B1: sends from the UI
  thread go through a per-host single-consumer queue, never `BlockingCollection.Add` on the UI thread.
- Jump-host password prompt can be offered the vault password on the **interactive** path
  (pre-existing, also plain native tabs): never offer a vault password to a prompt that names a hop.
- Native known-hosts: one store instance; atomic `TrustHost` write.
- `RemoteMuxCommand`: escape a recorded path with spaces/non-ASCII instead of silently replacing it.
- Askpass marker folder created 0700 off Windows; control characters stripped from server text
  quoted into toasts (`RemoteMuxFailureClassifier`); `Array.Clear` the native response payload.
- Install dir honours `${XDG_DATA_HOME:-$HOME/.local/share}` like the daemon root.
- Profile editor row for remote persistence says what it disables (SFTP, remote files, forwards).
- Manual: a plain `ntilde` via `ntilde.com` waits when output is captured.
- CI: the remote-persistence E2E step must not skip green on tests-only PRs; run it when any
  `src/Ntilde.Mux*`, `src/Ntilde.Platform/Ssh/Exec`, or `Shell/Mux/Remote` path changes.
- `SSH_AUTH_SOCK` staleness in long-lived remote shells: set `SSH_AUTH_SOCK` in daemon-spawned
  shells to a stable per-root symlink that the proxy repoints on every connect (tmux's pattern).
- Native exec idle poll: blocking wait instead of the 10 ms sleep (the ~15 ms attach floor).
- Sign and notarize `ntilde-mux` osx-arm64 in `release.yml` alongside the app, or document the
  Gatekeeper behaviour for Finder-copied binaries.
- Embed the `ntilde-mux` SHA-256s at build time: `publish_mux_daemon` writes a manifest the App
  build embeds, and `GitHubReleaseMuxAssetSource` verifies against it (the `.sha256` asset stays as
  a fallback for dev builds).

## 5. Remote endpoints in the picker and `mux ls`

"Attach to session…" lists sessions from every connected remote host (grouped by profile), and
`ntilde mux ls --all` prints them. Shared attach to a remote session follows the same exclusivity
rules; reconnect on a shared remote tab is the same loop.

## 6. SFTP, remote files and forwards on persisted remote tabs (native first)

Share the profile's native SSH connection between the mux exec channel and the SFTP subsystem and
forwards, so the sidebar and transfers work on persisted tabs. The hook is `ActiveSshSessionRegistry`
(skipped for `MuxClientSession` today, `TerminalPane.axaml.cs` RegisterActiveSshSession). OpenSSH
stays "not available" with the existing notice unless ControlMaster reuse through the persistent
exec `ssh` process turns out to be a one-day job; decide and say which.

## 7. Release

- Run the full manual checklist (PR #489's 8 steps, Phase 3's extensions, Phase 4's Windows
  `ntilde.com` steps, and the remote drop/reconnect scenario) on **all three OSes** and paste the logs.
- Release notes and CHANGELOG entry for the multiplexer as a whole (user-facing, not phase-by-phase);
  `docs/USER_MANUAL.md` gets one coherent "Persistent sessions and the multiplexer" chapter
  replacing the accreted §3.3 subsections; README feature bullet; `docs/ROADMAP.md` already updated.
- Version bump per the repo's release process (`docs/plans/2026-04-22-ci-rebalance-and-release-publishing.md`).
- **Final PR: `dev-mux` → `main`.** Squash or merge per repo convention; the PR body is the
  user-facing summary plus links to the five phase PRs and the two hardening PRs.

## Constraints

- Protocol additive only (Min 1 / Max 2 with optional new methods); a v1 daemon keeps working.
- No credential-path behaviour changes beyond the jump-host fix in §4; the once-per-attempt and
  never-empty-password rules stand.
- Layering unchanged: `Ntilde.Mux` no App/Avalonia/Platform; leaves stay leaves.
- With `SessionPersistence` explicitly Off, nothing changes.

## Decisions you may make (say which way and why)

- Whether the daemon must be started from outside Velopack's `current\` on Windows (§2).
- OpenSSH ControlMaster reuse for SFTP on persisted tabs (§6): do or defer.
- Whether the first-close notice is a dialog or a toast with actions.

## Report back with

1. The `main` sync PR: conflict list and resolutions; full-suite results on the merged tree; parity
   suite results after the reflow merge.
2. Branch/PR links; files by section; which §4 items landed with which test.
3. The default-flip implementation and the exact UX, with screenshots or test names for the
   first-close notice and migration cases.
4. Update-survival evidence: a Velopack update applied with a live daemon on Windows and on one
   Unix OS, shells intact afterwards, plus the no-overlap path.
5. Agent-host tests for windowless sessions, observe and act.
6. The three-OS manual logs and the release notes draft.
7. Open items left for a follow-up and anything done differently than stated here.

---

## R. Rulings

Each ruling: what was decided — why — what it costs if wrong. Added as they are made; the plan's
tasks carry them.

R0 (§0). The sync landed as PR #510 (merge 40fdb1a). Conflicts and resolutions are in its description.

R1 (§1). The first-close notice is a **dialog**, not a toast — the window is closing, so a toast would
vanish with it, and the choice must be made before teardown detaches or kills. "Don't ask again"
remembers the *answer* (keep or close) in a flag file under the app-data root (`mux-close-choice`,
content `keep` or `close`), not in `settings.json`. Saving Settings with a changed
`SessionPersistence` deletes it, so turning persistence back on asks again. — A remembered "keep"
with no remembered "close" would turn "Close them + Don't ask again" into the opposite of what the
user said. — Cost if wrong: one file and one branch in the close path.

R2 (§1). After a reboot, restored panes start fresh shells **silently** (no "previous sessions were
lost" toast, no per-pane banner) when the session file was saved before the current boot. A daemon
that died *since* boot still gets today's notices. **Amended in Task 19:** the boundary is the later of
the boot and, on Windows, the current logon session's start (Fast Startup's "Shut down" is a logoff
that keeps the tick count), read from the OS (`SessionStartBoundary`); a boundary that cannot be read
is not quiet, so the loss is announced. When only the Windows logon time cannot be read, the boundary
falls back to the tick-count boot, which is never later than the real boundary, so it can only make a
restore louder. — Default-on users reboot; a loss toast after every
boot would read as an error. — Cost if wrong: a crash that coincides with a reboot is not announced.

R3 (§1). The flip itself is its own commit (`SessionPersistence` default `"KeepOnClose"`), on top of
the UX, so the PR can drop it if the maintainer decides against it. `AppServices.BuildForDesigner`
pins `"Off"` so test windows and the designer never spawn a daemon.

R4 (§3). A windowless session is a daemon session (local, or a remote endpoint with a live connection
— `CurrentClient`, never `GetClient`, so the agent path never prompts or connects) that no pane of
this window shows or is about to show. Its agent-facing id is its mux session id. A session another
process attaches (a text client, another window) counts as windowless here.

R5 (§3). Reads of windowless sessions are journaled (the brief's "log to the agent journal"), unlike
pane reads, which stay unjournaled (`Capture_is_not_journaled_because_it_is_an_observe_tier_read`).
Act on a windowless session on a remote endpoint requires that profile's agent allowlist for
`send_input` **and** `close_session` (pane close is not allowlist-gated; a windowless kill is invisible
and destructive, so it is). — Cost if wrong: an agent needs the allowlist to close a remote windowless
shell.

R6 (§4). The non-blocking send path lives in `MuxClient` (one ordered overflow pump per client), not
only in `MuxConnectionHost`: the UI callers hold sessions, not hosts, local hosts have no B1 chain,
and ordering between input and a following kill must hold across both. B1's `HandOffKill` stays.

R7 (§4). `RemoteMuxInteractionHandler` keeps its stricter jump-profile rule (no remembered or vault
password for any prompt on a profile with jump hops), although native prompts now name the hop.
Relaxing it is a credential-path behaviour change the constraints exclude; it is listed as a follow-up.

R8 (§6). SFTP, remote listing and transfers on a native persisted tab open their own non-interactive
connection, exactly as a plain native tab's do (they never shared the shell's connection); the
persisted tab registers in `ActiveSshSessionRegistry` with a per-host password scope. Port forwards on
persisted tabs are **deferred**: they would ride the mux exec channel's single event queue (head-of-line
blocking against the liveness ping), drop and rebind on every reconnect, and collide with plain tabs
of the same profile. OpenSSH ControlMaster reuse is **deferred** (Win32-OpenSSH has no ControlMaster;
a stale master would capture reconnects); OpenSSH persisted tabs get palette transfers (`scp` runs on
its own connection), and the sidebar stays native-only as for plain OpenSSH tabs.

R9 (§2, measured 2026-10-08 with Velopack 1.2.0 on Windows 11, a probe app packed with vpk 1.2.0 into a
sandbox install). Velopack's Windows apply logs `Checking for running processes in: <install root>`, then
hard-kills every process whose image is under the install root (`current\` and any other subfolder).
A copy of the same binary running from outside the root survived four applies with no heartbeat gap.
The apply renames `current\` away, and an outside process whose working directory is inside `current\`
makes it fail ("Unable to start the update, because one or more running processes prevented it").
The macOS and Linux updaters contain no process-killing code.

Ntilde also sideloads `conpty.dll` and `<arch>\OpenConsole.exe` from the install folder (#310), so every
shell's console host runs from under the root too. **Decision: on a Windows Velopack install the local
daemon runs from a copy at `<app-data>\bin\<version>\`**: `Ntilde.exe`, the DLLs beside it and the
`<arch>\OpenConsole.exe` hosts, staged once per version. Elsewhere, and in dev builds, it runs from the
running executable as before.
— Without the copy, an update kills the daemon and every shell's console host whatever the GUI does.
— Cost if wrong: about 100 MB per installed version under app data (older copies are pruned when no
daemon runs them).

R10 (§2). The old GUI cannot know the new build's protocol range, so the release puts it in Velopack's
release notes as a marker line, `<!-- ntilde-mux-protocol: <min>-<max> -->`, and the GUI reads it from the
staged update.
- A missing marker counts as compatible. Min has been 1 since Phase 0, and the new GUI still handles a
  mismatch at launch with a "Restart multiplexer now" action.
- A daemon whose image is inside the install root (one started by a pre-Phase-5 build) is treated as not
  kept, because the apply would kill it: today's confirm-and-shutdown path.
- Velopack's startup auto-apply is vetoed only for such a daemon.
- The full hand-off (passing the PTY fds to a new daemon with SCM_RIGHTS on Unix) stays out of scope and
  is the follow-up. Windows has no SCM_RIGHTS; it would need `DuplicateHandle` into the new daemon plus
  re-creating each pseudoconsole's client side, which ConPTY does not support today.

Rulings made while executing the plan (`.superpowers/sdd/2026-10-08-ntilde-mux-phase5/progress.md` has
each with its reason), where they change user-visible behaviour or a contract:

R11 (§4, Task 2). The OpenSSH askpass helper withholds the vault password on a user's attempt through a
jump host whenever the prompt could be the hop's: ssh before 8.4, a hop named like the target, or a proxy
in the profile's extra SSH arguments (not parsed, so fail closed). A ProxyJump only `~/.ssh/config` names
is not seen.

R12 (§4, Task 3). One known-hosts store per path (`ForPath(AppPaths.NativeKnownHostsFilePath)`; Platform
has no `Default`). `TrustHost` writes atomically, fails only with `IOException` (a contended Windows
rename is retried 10 x 25 ms), and two processes trusting at once may lose an entry, never tear the file.

R13 (§4, Tasks 4, 6). The install dir is `$XDG_DATA_HOME/ntilde/bin` when set and absolute, else
`~/.local/share/ntilde/bin`; a recorded path sh double quotes can hold is escaped, not replaced. The
offline one-liner resolves the dir in the user's own shell and prints it, so an `XDG_DATA_HOME` set only
interactively can disagree with the exec channel's (accepted).

R14 (§4, Task 5; amended after the Codex review of PR #511). New askpass record folders are `0700`. One
that already exists with another mode (an older build made it under the umask) is tightened to `0700` by the
askpass writer before any record is written; when that fails (the folder is not ours, or is a link), no record
is written and askpass takes its no-evidence path. `PrivateDirectory` itself still never changes an existing
folder: the daemon's "never chmod an existing dir" rule for its socket folder stands.

R15 (§4, Task 8). The agent link is repointed only at a socket that answers and whose listener runs as
this user (`SO_PEERCRED` / `LOCAL_PEERCRED` against `geteuid()`); when that cannot be checked, never.

R16 (§4, Task 10). The release fails on a notarization whose status is not `Accepted`. The app verifies
a downloaded `ntilde-mux` against the hashes it embeds, and against the `.sha256` only without them.

R17 (§3, Task 11). `readScreen` replies are charged to the snapshot account, never the stream budget.
"Unsupported" is signalled only by a null result (a decode failure is a `protocol_error` exception);
`snapshot_too_large` is retried with fewer scrollback rows.

R18 (§3, Tasks 12, 13). One 7 s deadline per windowless operation. Act checks (`mayAct`: act on, the same
source, the profile's allowlist) run inside that one survey, so turning act off mid-survey stops it;
`NotAllowed` outranks `NotRunning`. Repeated windowless reads fold into one journal entry with a count
(`×N`); acts never fold.

R19 (§1, Task 16). "Close them" (asked or remembered) never ends a shell another interactive client
shows, nor a share whose sharing is unknown: those detach. macOS Cmd+Q (`ApplicationShutdown`) applies a
remembered answer and never asks; `OSShutdown` never kills. Enter answers Keep. Settings saves, imports
and restores that change the mode forget the answer.

R20 (§1, Task 17). "Quit and close all shells" asks even when the count is unknown and proceeds once
confirmed; it ends shared and detached local shells too, and never remote ones.

R21 (§1, Task 19). The quiet mark is kept per node in the session file (`PaneNode.MuxQuietPreviousLost`),
so an unvisited tab stays quiet in the next launch of the same boot. A restored share whose shell is gone
is announced even after a reboot (`ShareEnded`). The `MuxHostFactory` seam lives in `AppServiceBundle`,
and `BuildForDesigner`'s `MuxHostFactory` refuses, so no designer or test window can spawn a daemon (R3
made structural).

R22 (§2, Tasks 20, 23). An unknown version (null or `0.0.0`) is never another build's. "The previous
build" only when SemVer says older, else "a newer build" / "a different build"; no shell-count clause
at 0. A restored pane's mismatch banner stays text-only (the action is on the notice); the action labels
are the brief's, without an ellipsis. Amended (final review I1, residual N1): a remote daemon is offered a
restart only when it is older than the profile's recorded `RemoteDaemonVersion` ("a previous version"),
otherwise "Update ntilde-mux on {host}…" when older than the app; a remote offer released "after a
restart" stays claimed for the launch once `shutdown` went out, and is released by a recorded install.

R23 (§2, Task 21). A copy is reused only when its `.complete` sizes and the executable's SHA-256 match.
Velopack's uninstall hook stops the daemon and removes the copies, deleting only copy-shaped folders.
Measured in Task 24: Velopack 1.2.0's uninstall runs a kill pass, then the hook, then a second kill pass.

R24 (§2, Task 22). With `SessionPersistence` explicitly Off, both update paths keep their pre-Phase-5
behaviour: any live daemon is asked about and shut down, and vetoes the startup apply. A corrupt
`settings.json` whose `.bak` says Off reads as not-Off at startup (accepted).

R25 (§2, Task 24). (R1) `NTILDE_UPDATE_SOURCE_DIR` is honoured only when the install's Velopack app id is
the compiled-in `NtildeSurvival` (an allow-list, pinned against the scripts' pack id). (R3) On macOS the
survival script never lets Velopack restart the app (`open -n` drops the sandbox environment). (R4) The
scripts refuse a sandbox they did not create (a marker file) or outside an allowed location, before any
cleanup is armed. The hidden `spawn-for-test` verb ships: it adds nothing the same-user pipe does not.

R26 (§5, Task 25). The remote detach notice names the host: "Shell kept running on {host} — Attach to
session… reopens it", or the `ntilde-mux attach <id>` text when the profile no longer keeps sessions.
Picker error rows are worded from `MuxPickerHostError`, never server or exception text, and an empty
picker's notice is those rows. A connect row shows "Connecting to {host}…" while it waits; connect rows
sort by profile name.

R27 (§5, Task 26). A share whose sharing is unknown (link down or reconnecting, attach pending) detaches
on close without the running-process question - a detach never ends a shell - and the *Shell detached*
notice says so.

R28 (§5, Task 27). (a) `ls --all` may start an idle remote daemon, as the picker's connect does (it exits
after 10 idle minutes). (b) Off Windows it skips OpenSSH profiles through a jump host or proxy
(`MuxListingError.ThroughJumpHost`): BatchMode does not reach a ProxyJump hop, whose prompt would reach
the user's terminal. The picker still lists them.

R29 (all, pre-flight P2). A toast that quotes a value a daemon or remote host reported (a version, a
host, an unreachable reason) passes it through `RemoteOutputText.Quote` (control and format characters
dropped, length capped).

R30 (§6, Task 28). A native persisted tab registers in `ActiveSshSessionRegistry` with its host's
password scope. A password is written to that scope only when the user typed it in an attempt that got
in: never one filled from the vault, never an empty one, never on a profile with jump hops (R7); the
scope is cleared when the host goes. The exec transport's `NativeSshPromptResponder` still gets no
session id, so `SshInteractionService` never replays stored passwords past the handler's rules. The
registry keeps every live descriptor per session id, so two windows on one shared session each keep
theirs; a lookup sees the newest, so while a share is open in another window the owner window does not
see its own typed password (it recovers when the share closes). While the host still runs on a
destination the profile no longer names, the sidebar, listing and transfers are refused, checked at
every listing and again once each transfer dialog is answered, and an open sidebar closes with the
notice. Port forwards and OpenSSH ControlMaster reuse stay deferred (R8).
