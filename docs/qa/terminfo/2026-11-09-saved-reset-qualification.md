# Saved-state and reset qualification (2026-11-09)

Qualifies exactly five pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`modes.alt-screen.exit`, `modes.altscreen-47`, `modes.altscreen-1047`, `reset.sgr` and `reset.decaln`.
`reset.method` (needs a backend `reset()` API; RIS is not substituted), `scrollback.alt-screen` (scrolling/history batch), `sgr.reset` (already
qualified, a different case), state stacks, private-mode/SGR/colour stacks and the pinned DECALN pixel path are not part of this batch.

## Pinned definitions and verdict semantics

| Id | Pinned fixture bytes | Pinned verdict possible |
|---|---|---|
| `modes.alt-screen.exit` | `ESC[2J ESC[1;1H PRIMARY-A`, `ESC[3;1H PRIMARY-B`, `ESC[5;7H`, `ESC[?1049h`, `ESC[1;1H ALT-MARK`, `ESC[?1049l`, trailing `ESC[?1049l ESC[0m` | supported iff both spans restored, marker not leaked, cursor back at 6,4; every other outcome is the pinned inconclusive, **never unsupported** |
| `modes.altscreen-47` | `ESC[?47h`, `ESC[?47l` (reads `getMode("altScreen")`) | **always inconclusive** ("Mode metadata does not measure the buffer") |
| `modes.altscreen-1047` | `ESC[?1047h`, `ESC[?1047l` | **always inconclusive**, same note |
| `reset.sgr` | `ESC[1;3;7m X ESC[0m Y` | supported iff X is bold, italic, inverse and Y none of them; unsupported otherwise |
| `reset.decaln` | `ESC # 8` | supported iff cell (0,0) stores `E` |

The pinned terminal paths (cursor query, DECRPM, consumed bytes, capture-only DECALN pixels) never carry a verdict; the derivation pins them verbatim
so an upstream change that gave one a verdict would be refused. In the stock run all five stay inconclusive.

## Method

Sixteenth digest-checked extension (derived over the line-editing one), contracts v1, suite `ntilde-owned-saved-reset-graded-v1`, prefix `svrs`, nonce `NTILDE_SR_`. The harness runs inside the
alternate screen, where a nested `ESC[?1049h` is a no-op and would void the pinned exit fixture, so every group starts from a bank-normalizing reset (no RIS) that
leaves the **main** screen active with both banks blank. Pinned bytes are fed exactly, one write each, with a journaled read between pinned writes; the pinned
probe runs over those screens and the parent re-executes it. Only the active screen is read (cells, stored attributes, cursor, active-buffer flag, margins, mode
flags, history); the inactive bank is never inspected directly. Controls: blank initial screen, measured seed, no-op transition (entry must switch and blank),
bank separation (re-entry never shows main text), stale saved cursor (a decoy DECSC at 1,1), stored-attribute preservation, accidental main clearing, and a
no-reset control showing the SGR pen persists. Unsupported needs valid controls and a measured mismatch.

- **Exit:** main contents, cursor (4,6), active flag and unrelated state restored.
- **?47 / ?1047:** the effect contract is separately identified and never attributed to the pinned probe. ?47 must retain the alternate buffer across exit; ?1047 must clear it on exit.
- **reset.sgr:** X stores exactly bold+italic+inverse, Y is fully neutral (stricter than the pinned bold/italic/inverse check), plus a full-tuple control (underline, overline, colours, underline colour).
- **reset.decaln:** (0,0)=`E` is the pinned criterion; the full-grid `E` and homed cursor are a labeled refinement layer.

## Measurement

Scripts `5201cad906ca694366950173b693696c1d221f7a` (committed source: the merge of main with the character-editing, erase-in-display and line-editing layers, plus two Codex-review fixes); App source unchanged from main `4e32f6d9` (sidecar `saved-reset` built there; none of the merged layers changed an App file). Run
`a38dbcebb8390d33f6ef4dc97a689e57`, receipt schema 29, source policy v23 (inventory v30, 193 files), default theme, 40x140.

**Preserved attempts** (all in the summary, with run ids and script revisions; every case graded identically in each): (1) `2c4b2603…` (scripts `9ac95942`, receipt 26, policy v20), superseded when main merged the character-editing layer; (2) `91849fd9…` (scripts `2d22f91b`, receipt 27), superseded when Codex found the full-tuple SGR reset control did not validate unrelated state; (3) `fd0b4dc0…` (scripts `793b1937`, receipt 27), superseded when a second Codex review required every attribute of the full-style control on X and main merged the erase-in-display and line-editing layers (restacked as the sixteenth extension).

| Case | Pinned (parent replay + live pinned code) | Effect contract | Divergence |
|---|---|---|---|
| `modes.alt-screen.exit` | supported | supported | none |
| `modes.altscreen-47` | inconclusive (by design) | supported | `pinned-inconclusive-by-design` |
| `modes.altscreen-1047` | inconclusive (by design) | supported | `pinned-inconclusive-by-design` |
| `reset.sgr` | supported | supported | none |
| `reset.decaln` | supported | supported | none |

Counts: conclusions 3 supported / 2 inconclusive; effect contracts 5 supported (kept separate).

Observed: exit restored PRIMARY-A/B and cursor (4,6) every time, including after a decoy DECSC, with the alternate bank retaining `ALT-MARK`
and the saved pen (bold blue) restored (observed, not required). ?47 kept `ALT-47` across exit and re-entry; ?1047 re-entry was blank; ?1047 entry retained earlier
alternate contents (observed). SGR 0 left X at flags 775 and Y neutral, also under the full-tuple control; the omitted-parameter spelling behaved the same. DECALN stored
`E` in all 40x140 cells, homed the cursor, stored default tuples, and **an active scroll region [2,5] survived DECALN** (observed, not required; not claimed).

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (inventory derived from the merged contracts), 10/10 truecolor-literal finding,
3/3 App-observed underline-colour resolution; stock counts unchanged (86 supported, 1 unsupported, 168 inconclusive).

## Limits

Stored state only: no resolved-colour, glyph or pixel claim. macOS owned sidecar, default theme. Entry-cursor position after `?1049h` is observed, not required.

## Evidence

[`2026-11-09-saved-reset/`](2026-11-09-saved-reset/): summary, report header, one file per case, run log, common frames and one file per read frame.
`scripts/terminfo/saved-reset-publication.test.mjs` re-derives the report from the retained journal.
