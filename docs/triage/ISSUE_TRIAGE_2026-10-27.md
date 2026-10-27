# Issue Triage — 2026-10-27

**Scope:** all 32 issues open on `benyblack/ntilde` before the pass (`main@dad7e052`).
**Method:** every issue's factual claims re-verified against the current tree — five
verification lanes ran in parallel (CI history via `gh run`/workflow files; bug claims
via code reads with `file:line`; VT state; packaging/feature state; landed-work check
on everything triaged 07-31). Verdicts carry `file:line`, commit, or run IDs.
No product code was changed.

**Headline:** **5 issues are complete and closed** (#252, #352, #454, #114, #111).
**10 bodies were rewritten** because their premise partly or wholly evaporated — two
of them outright (#349's `[DETAILS]` leak never existed on `main`; #248's "zero test
coverage" ended 2026-09-23). The quarter's landed work (Command Assist V2, the
multiplexer, Theme v2, focus mode) resolved chunks of the 07-31 backlog almost as a
side effect, while the 07-31 *refactor* family is untouched. One bug got a partial
fix in flight (#587) and two sharp edges were found on it.

**Open count: 32 → 27.**

---

## 1. Complete — closed

| # | What closed it |
|---|---|
| **#252** OSC 52 clipboard | #268 / PR #280 (2026-08-02, `919a0d39`) — write-only, settings-gated, reads denied, 1 MiB cap, injection-hardened echo. #252 was the stale duplicate of #268. |
| **#352** Settings backup/export | `Ntilde.Backup` (`811668b1`, filing day) + Backup & Restore page (`fd9c6270`, 2026-10-14) + `ntilde backup` CLI + MCP `BackupTools`. Passwords deliberately excluded. All three original asks answered. |
| **#454** Tab-overflow tooltip flake | PR #462 (`b1823497`, 2026-09-15) — the precondition held by 3 px (989 vs 986); now by construction. Zero windows Unit Tests failures in 157 runs since. Never allowlisted (list is empty, deliberately). |
| **#114** CommandAssist assembly + state machine | #276 + #283 (both 2026-08-02). NetArchTest rule at `LayeringTests.cs:154`; enum-driven `AssistSessionStateMachine.cs`. |
| **#111** Single ThemeService | Theme v2 (`fb5818f5` phase 1 #584 → #597, 2026-10-23…26). `ApplyThemeToUI` is now a thin window-specific wrapper over `ThemeService.Apply`. Remnants (`_settings = sw.Settings` swap, PR #613) noted in the closure comment, both outside this issue's duplication. |

## 2. Premise gone or wrong — rewritten before working

### #349 — two default shells + Rust debug leak ⚠️ **half struck, half narrowed**
- Part 2 (`[DETAILS]` leaks `{:?}`) **never existed on main**: the line goes through
  `SanitizeBannerValue` (`TerminalPane.Lifecycle.cs:132-151`) and the PTY crate has used
  `Display` formatting since the error channel's introduction (`804e2ffd` / #229) — true at
  the filing commit too (`2b89b67a`). Struck with commit-level evidence in the comment.
- Part 1: **Unix fixed** (`cda98f3a`, 2026-09-01 — login shell leads both paths);
  **Windows still disagrees** (`ShellHelper.cs:19-27` pwsh-first probe vs
  `TerminalSettings.cs:300-307` cmd-first candidates). Body now tracks only the Windows
  candidate-order fix. Labels: `bug`, `ux`.

### #348 — cmd.exe double `/k` ⚠️ **mis-located by the body**
- Not shell integration: no cmd provider exists (`CommandAssistServices.cs:81-86`), SI is a
  no-op for cmd. The merge is `RustPtySession.cs:789` — **unconditional** `/k chcp 65001`
  prepend, no `/k`/`/c` check (the PowerShell branch guards `-NoLogo` ten lines below).
  `/c` profiles are hit worse (stay-open). Mux daemon inherits (`LocalShellSessionFactory.cs:39-47`).
  No tests. Retitled `[shell-integration]` → `[pty]`; body re-pointed. Label: `bug`.

### #248 — theme importer tests ⚠️ **premise ended 2026-09-23**
- `ThemeImportTests.cs` (`03fd552a`) + `ThemeImporterOptionalColorTests.cs` (`c50ba2d5`,
  2026-10-26) cover the importers directly. Remaining, exactly: full 16-ANSI-slot mapping per
  importer, Name-derivation rules, malformed-colour-amid-valid. Still `good first issue`.
  (Assignee status recorded: no PR/fork/activity since 2026-08-09.)

### #385 — additional package formats ⚠️ **AUR leg built, not listed; two premises gone**
- AUR tooling landed (`c72272b9`, 2026-09-14) but the package is not on the AUR (registration
  closed — `packaging/arch/README.md:27`). Homebrew lane exists, tap 404s. `DLOPEN_DEPENDS`
  was replaced by the gated `DLOPEN_LIBS` table the same week (`973bdb48`). Arm64 is verified
  end-to-end since v0.7.0-rc.1. Remaining: listing/distribution, Flatpak/RPM/Snap.

### #91 — installer + auto-update + signing ⚠️ **two of three landed → retitled**
- Installer + Velopack (#340), macOS pkg + feed (#363), macOS codesign/notarize (#382) all
  landed. Now tracks: **Windows Authenticode** + **pipeline-fed winget**.

### #460 — macOS unit tests failing ⚠️ **failures fixed; the coverage gap is the issue → retitled**
- 36/45 = AgentHost socket paths over the 104-byte `sun_path` limit (PR #461); 7/45 = headless
  font registration (PR #520). Green in all three dispatch runs since. What remains:
  `Unit Tests (macos-latest)` is still dispatch-only (`ci.yml:281`) and nobody dispatches
  `main` (last: 2026-09-14, the issue's own control run). Label: `ci`.

### #173 — SSH output path ⚠️ **items 1+2 landed → item 3 remains**
- Bounded queue with control-event carve-out (#331), forward writes off the poll loop (#325).
  Remaining trio verified untouched: `PollDelay` 25 ms (`NativeSshSession.cs:14`), per-chunk
  `GetMaxCharCount` (`:502`), uncapped `_pendingOutputReplay` (`:63`, `:578`).

### #95 — OSC 8 adoption ⚠️ **gap 4 done; 3/5/6/7 remain**
- Gap 4 resolved harder than asked: `LinkSchemes` module + #481 (`file:` never shell-executes).
  Gap 3 still first: `TerminalView.cs:2803` single-cell hover; `GetHyperlinkIdentityAbsolute`
  (`TerminalBuffer.AccessAndSnapshot.cs:154`) exists and is unused by TerminalView — the easy
  path #260 predicted. Gap 6: zero hyperlink data in Replay/RenderSnapshots.

### #96 — SSH key wizard ⚠️ **prerequisite resolved**
- Native ssh-agent auth landed (#334, `UseAgent` + `SSH_AUTH_SOCK`/Pageant). Wizard still
  greenfield. `AllowAgentAccess`-is-not-ssh-agent trap re-flagged.

### #587 — template rule restarts every shell ⚠️ **half-fixed; two sharp edges found**
- Fixed for agent spawns under focus mode (`7cdd8fb3` / #595: `select &&` guard at
  `Panes.cs:685`). User path unchanged and now documented as by-design. New findings:
  (1) during the rebuild the agent is handed the **user's current pane** as its spawn result
  (`AddTab` → null → `AgentActions.cs:88` fallback); (2) `DisposeAllTabs` permanently abandons
  zoomed-tab panes (`Shutdown.cs:135-143`). Label: `bug`.

## 3. Verified still open — comment only

- **#610** agent SSH dialog: present as described. Corrections: `SshSessionManager` does not
  exist (the descriptor factory chain is the seam); `RemoteFailureKind.NeedsUser` is app-side
  mux (`Remote/RemoteFailureKind.cs:33`); scope is native-backend + `ExperimentalNativeSshEnabled`.
  The remote-mux automatic path already models option 2 (`RemoteMuxInteractionHandler.cs:609`).
- **#611** focus-mode/title-bar follow-ups: every checklist item verified present — no
  `CanMaximize` check (`MainWindow.axaml.cs:203-221`), pointer-typed double-tap box (`:231`),
  `SetWindowDecorationsTheme` never called from tests (mirror host calls the static
  `InvalidateLayersBeside` directly), no `AgentWrote ≠ AgentWatched` test (only the adjacent
  `Warning/Error/Info` assertions, `ThemeTokensTests.cs:87-89`). PR #612 covers part of the
  macOS test-platform gap.
- **#391** PTY backspace flake: still reproducing (3/157 runs, twice on 2026-10-26 — runs
  36406979355, 40312659778, 40379867504), signature sharpened to *empty first read* —
  unreachable-condition family, not load. Rate down from ~33% to ~2%. Label: `ci`.
- **#355** CommandPaletteUsageStore 2 s deadline: mechanism unchanged (`Task.Run` at `:82`),
  dormant in the 157-run window. The fallback (`SetMinThreads`) predates the issue and its
  causal claim was retracted (`0600f88d`) — the awaitable-write fix is the only option left.
- **#317** local test reliability: since 08-26, the candidate cause got real fixes — #436
  (`TestAppDataRoot`) + isolation roll-out through `041b456f` (today), `2c041a78` (a genuine
  `~Ssh`-class lane hang: plain `[Fact]`s creating `Dispatcher.UIThread` off-thread),
  #416/#426 (the #81 hang family). Residues: bootstrap writers still target real dirs by
  default; one rotating failure on arm64 (37112556269, 2026-10-03).
- **#270** underline styles: parsed-then-collapsed exactly as claimed; now one of only two
  survivors of the entire AI-harness audit. Label: `vt`.
- **#272** XTVERSION: unimplemented; `CSI > q` falls through the DECSCUSR guard
  (`Csi.cs:509-518`); DA2's `>`-family reply (`:475-480`) is the pattern to copy. Label: `vt`.
- **#384 / #383**: premises hold; new constraints recorded (deb test now *fails* on
  maintainer scripts — `test-build-deb.sh:258-263`; man page is `ntilde.1`). Labels: `enhancement`.
- **#121 / #127 / #110 / #112 / #113 / #115 / #108**: re-verified untouched, one delta each:
  #121's surface *grew* (#482 `JumpHopPasswords`); #127's measurement infra got richer
  (#490/#491) making the benchmark cheaper; #110 gained #594's partial split as lift-ready
  groundwork; #112's leak is still one line (`CommandRegistry.cs:38`); #113/#115 unchanged;
  #108's three judgement calls all still open (`VttestCapture.cs:112/:120` still discard
  `pty_write`).
- **#249**: stands as written; #248-coordination note obsolete; assignee inactive 2.5 months.

## 4. Stale docs found and fixed (this pass, docs-only commit)

- `docs/vt_ghostty_gap_matrix.md:51` — OSC 52 "No implementation found | Real gap | P0" →
  implemented write-only (#268/#280).
- `packaging/linux/README.md:41-47` — "arm64 legs have never executed at all" → verified
  end-to-end since v0.7.0-rc.1, `continue-on-error` dropped (`32d44ff2`).

## 5. Recommended order (from the evidence, not preference)

1. **#587** — user-facing data loss (shells killed; zoomed panes abandoned; agent handed the
   wrong pane). Fix direction is append-without-rebuild and does not depend on anything.
2. **#391** — a gating lane that reds ~2% of the time trains re-run-on-red; the empty-first-read
   signature suggests a contained harness fix (re-query screen state, don't await new output).
3. **#270** then **#272** — the last two items of the AI-harness audit; #272 is small, #270 is
   the P1 with real TUI-tooling payoff.
4. **#348 / #349(Windows half)** — small user-visible correctness fixes with the location
   work already done in the rewritten bodies.
5. **#112 → #110** — the by-ref leak is a one-liner that unblocks the tab extraction's
   sequencing; #594's partials made the lift mechanical.
6. **#610** — prefer option 2 (fail with `NeedsUser`): the mux precedent exists; no new policy
   surface. Pairs naturally with #587's agent-spawn work.
7. **#121** items 2→4 after the #96 decision, **#127** starting from #491's recording path,
   **#108** batch 2 as reviewable standalone PRs, packaging tails (**#91**, **#385**) as
   capacity allows.

## Errors and hazards, recorded

- None this pass. The 07-31 lesson (never read a body through a lossy pipe and write it back)
  was applied: all body edits were composed from UTF-8 files and applied with
  `gh issue edit --body-file`, verified after the fact via the API.
