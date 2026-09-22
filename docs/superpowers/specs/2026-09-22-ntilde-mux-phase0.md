# Spec: ntilde multiplexer — Phase 0 (seams and parity, no daemon)

> Source: task handed to Claude Code on 2026-09-22. Reproduced verbatim so the plan
> that implements it can be checked against it.

## Why

ntilde is getting a tmux/WezTerm-style multiplexer: a headless `ntilde mux serve` process will
own PTYs plus an authoritative `AnsiParser`+`TerminalBuffer` per session, and GUI panes will
attach over IPC, receive a full-state snapshot, then parse the same byte stream with their own
local parser+buffer. Later phases build the daemon, IPC and GUI attach. **Phase 0 builds only the
seams and the correctness proof, with zero user-visible behaviour change.** Everything here must
land as additive changes that existing tests keep passing.

Read first: `CLAUDE.md`, `AGENTS.md`, `docs/ARCHITECTURE.md` (§2 assembly graph, §12 arch tests,
§14 tech debt), `docs/MODULE_OWNERSHIP.md`. Build/test only through `scripts/build.sh` or
`scripts/build.ps1`; never raw `dotnet`. Run test projects individually. For
`tests/Ntilde.App.Tests` pass `--blame-hang-timeout 5m` and run the `Lane!=PlatformBoot` and
`Lane=PlatformBoot` filters separately, never concurrently.

Work on a new branch off `main`. Do not touch `claude/ntilde-multiplexer-mbxx9b`.

## Deliverables

### 1. `ITerminalSessionFactory` seam (Ntilde.Pty + Ntilde.App)

`TerminalPane` constructs its session inline in `InitializeSessionCore`
(`src/Ntilde.App/Controls/TerminalPane.axaml.cs` ~line 3324): an SSH branch that news up
`SshSessionFactory` and calls `Create(profile.Id, cols, rows, diagnostics, null, log)`, and a local
branch `Session ??= new RustPtySession(effectiveShell, cols, rows, args, startingDir,
skipPowerShellPostLaunchInit:, environmentOverrides:)`. Nothing else in the app news up
`RustPtySession` (only `PtySshProcessLauncher` in Platform and tests).

- Add `ITerminalSessionFactory` to `src/Ntilde.Pty` with one method that takes a request record
  carrying everything both branches use today: command, args, cwd, cols, rows, env overrides,
  the PowerShell post-launch flag, and an opaque SSH descriptor (profile id, diagnostics level,
  interaction handler, native-enabled flag) so the SSH branch can stay in App. Keep it small.
- Add a default implementation in App that reproduces today's two branches byte-for-byte
  (same arguments, same error handling, same `ShellCommand = Session.ShellCommand` fix-up for
  SSH, same "fail loudly, never fall back to RustPtySession" on SSH failure).
- `TerminalPane` gets an injectable factory (constructor/property, defaulting to the default
  factory) and `InitializeSessionCore` calls it. Follow how `CommandAssistServices` is injected
  into `TerminalPane` from `AppServices` for the wiring pattern.
- Test: a fake factory injected into a `TerminalPane` receives the expected request and its
  returned session is wired (output → parser, `OnExit` → `HandleSessionExit`). Use
  `tests/Ntilde.App.Tests/Core/TestMainWindowFactory.cs` and `MainWindowPaneWiringTests.cs` as
  the pattern; dispose everything you create.

### 2. Session capability flags (Ntilde.Pty + Ntilde.App)

Add an optional interface in `Ntilde.Pty`, e.g. `ITerminalSessionCapabilities` with
`bool AnswersDeviceQueries { get; }` and `bool OrdersResizeInStream { get; }`. Existing sessions
do not implement it (both flags read as false).

- In `TerminalPane.InitializeSessionCore`, skip wiring `Parser.OnResponse += r => Session.SendInput(r)`
  (~line 3481) when the session reports `AnswersDeviceQueries`. Keep the
  `_marklessSubmission.ObserveDeviceReply` observer (it only invalidates a heuristic). Also skip
  the pane's `SendInBandResize` calls (~lines 3585-3618) for such a session.
- Do NOT change resize flow in this phase; `OrdersResizeInStream` is declared only. Add a short
  doc comment saying Phase 1 consumes it.
- Test: a fake session with `AnswersDeviceQueries = true` receives no DA1 reply when the parser
  emits one; the default fake still does.

### 3. Shared byte-exact UTF-8 chunk decoder (Ntilde.Pty)

`RustPtySession.ReadLoop` decodes with a stateful `Encoding.UTF8.GetDecoder()`
(`src/Ntilde.Pty/RustPtySession.cs` ~lines 252, 1015; `Reset()` on read error ~1045) and
`NativeSshSession.EmitOutput` does the same (`src/Ntilde.Platform/Ssh/Native/NativeSshSession.cs`
~516). Neither exposes how many bytes are pending, which the mux snapshot needs.

- Add `Utf8ChunkDecoder` to `Ntilde.Pty` built on `System.Text.Unicode.Utf8.ToUtf16(...,
  isFinalBlock: false)` with an explicit unconsumed tail (max 3 bytes), U+FFFD replacement for
  invalid sequences matching `Encoding.UTF8` default fallback behaviour, `ConsumedBytes` (long),
  `PendingTail` (ReadOnlySpan<byte>), and `Reset()`.
- Switch `RustPtySession` and `NativeSshSession` to it. Output must be identical.
- Tests (`tests/Ntilde.Platform.Tests` or a Pty test project if one exists; check first): a
  differential test feeding the same random byte streams, split at every offset, to both
  `Encoding.UTF8.GetDecoder()` and `Utf8ChunkDecoder`, asserting identical strings; explicit cases
  for a 4-byte code point split 1/3, 2/2, 3/1; invalid bytes; lone continuation bytes; `Reset()`.

### 4. Full-state terminal snapshot v2 + parser state export/import (Ntilde.VT, Ntilde.Replay)

Today `PtyRecorder.RecordSnapshot` (`src/Ntilde.Replay/Replay/PtyRecorder.cs` ~105) fills
`ReplaySnapshot` (`src/Ntilde.VT/ReplayModels.cs` ~63) and `TerminalBuffer.ApplySnapshot`
(`src/Ntilde.VT/TerminalBuffer.AccessAndSnapshot.cs` ~991) restores it. It covers the active
viewport cells (blittable `TerminalCell[]` via `MemoryMarshal`, `CellsLayoutId` validated),
cursor, alt flag, scroll region, a handful of modes, SGR, extended text, row wraps. It does not
cover: the inactive screen (main when alt is active and vice versa), scrollback, tab stops,
saved cursor (DECSC), synchronized-output state (`TerminalBuffer.ThreadingAndInvalidation.cs`
~156-179), DECSET 2048 flag, the kitty keyboard stack (`KittyKeyboardState` has `Clone()`),
hyperlinks (`ApplySnapshot` calls `row.ClearHyperlinks()`), and it carries no parser state.

Build:
- `TerminalStateSnapshot` (in `Ntilde.VT`, source-gen JSON via `ReplayJsonContext` or a sibling
  context; cells and scrollback as base64 blittable blobs like today) = buffer state for **both
  screens** + scrollback rows (capped by a caller-supplied max, with `ScrollbackTruncated`) +
  tab stops + saved cursor + sync-output + mode 2048 + kitty keyboard state + hyperlink table
  and per-cell link ids + `ParserState` + `DecoderTail` (byte[]) + `StreamSeq` (long) +
  a format `Version`. Every field must be something the parser/buffer consults when applying
  future bytes, or something the renderer shows. Inline images are deliberately excluded.
- `TerminalBuffer.ExportState(int maxScrollbackRows)` and `TerminalBuffer.ImportState(...)`,
  taking the appropriate lock like `ApplySnapshot` does. Keep the existing `ReplaySnapshot` /
  `ApplySnapshot` untouched so replay files keep working.
- `AnsiParser.ExportState()` / `ImportState(AnsiParserState)` covering every cross-call field:
  `_state`, `_paramBuffer[0.._paramLen]`, `_csiTruncated`, the OSC/APC/DCS accumulators,
  `_charsets[]`, `_gl`, `_pendingCharsetSlot`, `_swallowNextNewline`, `_verticalOffset`,
  `_lastGraphicChar`, `_inBandResizeReportsEnabled`, `_kittyPendingParams`,
  `_kittyPayloadBuffer`/`_kittyPayloadOverflow`. Audit `src/Ntilde.VT/AnsiParser.cs` fields
  (~lines 13-140, 295) and include anything else that persists between `Process()` calls.
  Read-only configuration (`ImageDecoder`, `CellWidth`, defaults) is not state.
- Serialization helpers to and from `byte[]` (JSON envelope; cell blobs base64 inside, same as
  today). Measure and report the serialized size for an 80x24 buffer with 10,000 scrollback lines.

### 5. The parity test (tests/Ntilde.VT.Tests) — the point of Phase 0

Prove that "snapshot + tail" equals "continuous" for the same input:

- Build a corpus: existing replay fixtures under `tests/` (search for replay v2 `.rec`/NDJSON
  fixtures used by `ReplayRunner` tests) plus synthetic generators covering CSI (incl. long
  params, truncation), OSC 0/2/7/8/52, DCS, APC/kitty graphics with `m=1` chunking, alt screen
  in/out, scroll regions, tab stops, DECSC/DECRC, charset designation and shift, SGR incl.
  256/RGB colours, wide chars/emoji/grapheme clusters, hyperlinks, kitty keyboard push/pop,
  synchronized output (2026), mode 2048, in-band resizes.
- Feed each corpus as bytes through a stateful decoder into parser A + buffer A continuously.
  For each of many cut points (deterministic seed; include cut points mid-escape-sequence and
  mid-UTF-8 code point, and ones taken while the alt screen is active), run parser B + buffer B
  over the prefix, `ExportState` (buffer + parser + decoder tail), construct a fresh parser C +
  buffer C, `ImportState`, feed the remaining tail, and assert C equals A.
- Interleave resizes at fixed stream offsets and apply each resize to every buffer at that same
  offset.
- Equality: `BufferSnapshot.Capture(buffer, includeAttributes: true)`
  (`src/Ntilde.Replay/Replay/BufferSnapshot.cs`) for both screens, plus explicit compares of
  cursor, saved cursor, scroll region, tab stops, all modes, kitty keyboard state, sync-output
  state, hyperlink ids, and the parser's exported state. Also assert that A and C emit identical
  `OnResponse` strings for the tail.
- Also a round-trip test: `ExportState` → bytes → `ImportState` → `ExportState` is byte-identical.
- Construct all parsers with the same `forceConPtyFiltering` value and `ImageDecoder = null` so
  the test is platform-independent; add one variant with `forceConPtyFiltering: true`.

Any divergence found is a Phase 0 bug to fix in the export/import, not a test to weaken.

### 6. Persisted mux fields (Ntilde.Pty)

Add `string? MuxSessionId` and `string? MuxEndpoint` to `PaneNode`
(`src/Ntilde.Pty/SessionModels.cs`). Backward-compatible (nullable, omitted when null). Extend
`tests/Ntilde.App.Tests/Core/SessionManagerTests.cs` with a round trip and a "legacy JSON without
the fields still loads" case. Do not read them anywhere yet.

## Constraints

- No new `TerminalSettings` fields in this phase (they require three extra edits per AGENTS.md).
- `Ntilde.VT` stays a leaf; `Ntilde.Pty` must not reference `Ntilde.VT` (arch test
  `Pty_must_not_depend_on_Vt`). Run `tests/Ntilde.Architecture.Tests` before finishing.
- App is Native AOT with `IL2026;IL3050` as errors: source-generated JSON only, no reflection
  serialization.
- Additive changes; do not refactor `MainWindow`/`TerminalPane` beyond the seams above.
- Hot paths: the new decoder and the snapshot must not add per-chunk allocations in
  `RustPtySession.ReadLoop` beyond what exists today.

## Report back with

1. Branch name and the list of files added/changed, grouped by deliverable.
2. Test commands you ran (exact wrapper invocations) and their pass/fail counts, including the
   two App.Tests lanes and the architecture tests.
3. Every field you added to the snapshot and to the parser state, and any state you found that
   you deliberately excluded and why.
4. Any divergence the parity test exposed and how you fixed it.
5. Serialized snapshot size for 80x24 + 10k scrollback lines, and the time to export/import it.
6. Open questions or seams that turned out harder than described (especially in
   `InitializeSessionCore` and `NativeSshSession`).
