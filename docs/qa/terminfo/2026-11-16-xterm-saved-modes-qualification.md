# Xterm saved-mode qualification: XTSAVE and XTRESTORE (2026-11-16)

Qualifies two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `modes.xtsave` (`CSI ? 7 s`) and `modes.xtrestore` (`CSI ? 7 r`). Like the
[SGR-stack batch](2026-11-15-xterm-sgr-stack-qualification.md), **this batch adds production code**: the parser deliberately ignored `CSI ? Pm s` and `CSI ? Pm r` (so they could not be mistaken for SCOSC
and DECSTBM), which left nothing to measure. The VT now implements xterm's one-level cache of DEC private mode values, and the batch measures it.

## What the pinned probes can say

Both pinned probes return `pass: false` and an **inconclusive** outcome on every path; neither can ever return supported or unsupported.

| Case | Pinned termless path | Pinned terminal path |
|---|---|---|
| `modes.xtsave` | `feedCapture(CSI ? 7 s)` (any output is `invalid-reply`), `feedCapture(CSI c)`, `feed(CSI ? 7 r)`; then inconclusive `insufficient-evidence` (evidence `consumed`) with the DA1 reply | `write(CSI ? 7 s)`, cursor-position query, `write(CSI ? 7 r)`; inconclusive `insufficient-evidence` with the position, or `no-response` without one |
| `modes.xtrestore` | `feed(CSI ? 7 s)`, `feedCapture(CSI ? 7 r)` (any output is `invalid-reply`), `feedCapture(CSI c)`; same verdict | `write(CSI ? 7 s)`, `write(CSI ? 7 r)`, cursor-position query; same verdicts |

The pinned verdict therefore stays **inconclusive**: the unchanged pinned code's own result over the journaled raw bytes of its own fenced exchange and the cursor of its read. The graded conclusion belongs to a
separately identified effect contract; the difference is recorded as the divergence `pinned-inconclusive-effect-supported`, never absorbed.

## Result

| Case | Pinned (termless and terminal paths) | Effect contract (`ntilde-xterm-saved-modes-effect-v1`) | Stock grade (this run) |
|---|---|---|---|
| `modes.xtsave` | **inconclusive** (`insufficient-evidence`) | **supported** (22 groups; divergence `pinned-inconclusive-effect-supported`) | inconclusive |
| `modes.xtrestore` | **inconclusive** (`insufficient-evidence`) | **supported** (21 groups; divergence `pinned-inconclusive-effect-supported`) | inconclusive |

The stock collector in the same owned run (`f1940b35a20e1b08c772cc492fb3c47e`) grades 92 supported / 1 unsupported / 162 inconclusive (`suiteComplete=false`): the counts already on main after the extended-mouse
batch, so no stock grade moves. The frozen stock baseline keeps its original grades. No Core point is added. Inventory: two more IDs with a supported effect contract and an inconclusive pinned verdict; no
pinned-supported ID is added.

## Production

`CSI ? Pm s` (XTSAVE) keeps the live value of each listed DEC private mode and `CSI ? Pm r` (XTRESTORE) applies it again ([parser](../../../src/Ntilde.VT/AnsiParser.ModeSave.cs),
[slots](../../../src/Ntilde.VT/TerminalBuffer.SavedModes.cs), [unit tests](../../../tests/Ntilde.VT.Tests/ModeSaveTests.cs)). It follows xterm's control-sequence text (ctlseqs, patch 411: "Ps values are the same
as for DECSET", "a one-level cache", "specific settings can be saved and restored independently", "only those modes listed as parameters") and the shape of xterm's `savemodes` / `restoremodes` in `charproc.c`:

- one slot per mode, replaced by each later save and not emptied by a restore; `?1000`, `?1002` and `?1003` share the mouse tracking slot (the selector, none included), `?1005`, `?1006` and `?1015` the
  coordinate encoding slot, and `?9` has a slot of its own that also keeps the tracking selector, as in xterm. Modes with a slot: 1, 5, 6, 7, 9, 25, 45, 66, 69, 80, 1000/1002/1003, 1004, 1005/1006/1015, 1007, 2004;
- a restore goes through the same path as DECSET and DECRST, so a restored `?6` homes the cursor (xterm calls `CursorSet(0, 0)`) and a restored `?69` resets the margins;
- `?1048` is the cursor save and restore (the DECSC bank), as in xterm's `savemodes`;
- both sequences are silent; only the exact spelling is admitted (the `?` leader, no intermediate, plain parameters; a colon subparameter is ignored whole). They are neither SCOSC/SCORC nor DECSTBM.

Choices the text does not make, taken here and recorded as non-deciding policy: the alternate-screen modes (47, 1047, 1049) and `?2026`, `?2031`, `?2048`, `?5522` are not saved; a restore of a mode nothing was saved
for keeps the live value (xterm's slots start zeroed, so its restore resets the mode: a recorded deviation); a saved `?1007` is the effective value; RIS clears the slots, DECSTR leaves them, both screens share them
and state transfer carries them (`saved_modes`, absent when empty, validated before any state change). A local Codex review of the production code and of the layer found nothing.

## What the effect contract decides

The oracle is written from the same text and reads the **stored mode state** the App reports for the pane: DECAWM (`autoWrap`, and physically whether a character written in the last column leaves a pending wrap),
DECOM (`originMode`), DECLRMM (the horizontal-margin mode) and DECTCEM (the stored cursor visibility). `protocol` groups decide a mismatch (unsupported), `control` groups prove the setup took effect (a failed control
is inconclusive, never a verdict) and `policy` groups record a choice above (never deciding). Every exchange must be silent and leave the screen outside the probe row, the cursor style, both margins, the insert mode
and the active screen unchanged. Each group ends with an uncaptured baseline restore (baseline modes, probe row erased, cursor home), so groups are independent, and the whole suite stays inside the owner observer's
capture budget (48 frames of 64).

- **Both cases.** the exact pinned sequences are silent and change nothing; for each of `?7`, `?6`, `?69` and `?25` a mode saved at its default, changed and restored is back at its default, and a mode saved changed,
  set back to its default and restored is changed again (so a restore applies the saved value, not the default); only the listed mode is restored; a list restores together; a restored auto-wrap decides whether a character
  in the last column leaves a pending wrap, in both polarities; the sequences are neither SCOSC/SCORC nor DECSTBM (a cursor saved by `CSI s` survives a `CSI ? 7 s`, a `CSI ? 7 r` neither restores it nor sets a
  scroll region). `modes.xtsave` also checks that a second save replaces the first. Policy: values that name no saved mode (9999, a bare `?`), a repeated restore, xterm's `?1048` cursor save/restore and the cursor move
  of a restored `?6`.

All protocol and control groups matched, all eight policy groups matched Ntilde's choices, and every exchange was silent and left the state unchanged. The contract's expected state was also written by hand as a second
oracle in the portable test fixture (the model and the hand-written table agree for all 43 groups), and the exact bytes of every plan were replayed through the C# VT before any desktop run (0 mismatches in modes,
cursor, pending wrap, silence, probe row and margins; the throwaway harness was deleted afterwards).

## Not claimed

Stored state only: no rendering, painted-pixel or host-presentation claim. Only the four read-back modes are measured: the input-side modes (`?1`, `?66`, `?1004`, `?1007`, `?2004`), the mouse selectors, `?45` and `?80`
are covered by unit tests, not by this measurement. A restore with nothing saved is not measured (the run cannot prove an empty slot) and the alternate-screen modes are deliberately not saved. The run leaves the saved
values of its last group in the disposable App's slots (they cannot be emptied without RIS). xterm's behavior beyond its text (RIS, DECSTR, an empty restore) is not asserted. Remote/mux delivery, Windows/Linux and
common-harness runs are not covered.

## Identities

App 0.14.0 (operator supplied) built from `27babfa614373b6fb47f5528824e5be8742a140b` (arm64 Release; the last commit that changed the App sources; executable SHA-256
`f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256 `c75803aba80aa522e2fda27654ee524a894d36408424d31111c2ffb354cb59ee`); measured scripts
`c243beeb43161aa2ec860917d1a6ca252ecda739`; source policy `readonly-owned-source-graph-v59` (394 files; manifest SHA-256 `078fd3e6e58bcfc2df601e332e28946c23d465fb694f1c9bb298f3305bec633d`); query policy
`owned-exact-query-paired-raw-state-v4`; initial settings SHA-256 `d5bc29e519972f9c8fb98ace7ce0e575bf1425cb9f5c81472af975a49804cd77`; receipt schema 65; Node v24.22.2; owned run
`0d11d710ddef058e0b262a70a5ad4ff3`; collected 2026-11-15T23:05Z on macOS. The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the
retained bytes.

## Retained attempts ([superseded/](2026-11-16-xterm-saved-modes/superseded/))

1. Over the runner's fixed 5-minute budget (`Owned collection exceeded 5 minutes.`), stopped after the OSC 9 suite before the device-query, color-stack, SGR-stack and this suite ran (their journals are empty). The
   machine was under heavy load from other sessions' test runs (load average about 15 to 19).
2. The same committed scripts and sidecar, rerun unchanged under the same load: the same budget, stopped at the same suite.
3. Waited until the load dropped below 7 (6.5 at the start), then ran the same committed scripts and sidecar unchanged: completed. This is the published run.

The desktop lease was held by this session for each attempt and released after verified cleanup; no other session's lock was touched.

The final run is [owned-run.zip](2026-11-16-xterm-saved-modes/owned-run.zip); `node --test scripts/terminfo/xsv-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the retained
journal and compares the published projection.
