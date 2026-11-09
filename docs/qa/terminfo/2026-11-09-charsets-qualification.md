# Charset qualification: UTF-8, DEC special graphics, DEC line drawing and G0/G1 switching (2026-11-09)

Qualifies exactly four pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`charsets.utf8`, `charsets.dec-special`, `charsets.dec-line-drawing` and `charsets.g0-g1-switching`. No other registered case is
substituted (`unicode.*`, `text.*`, cursor and erase cases are different cases).

## Pinned definitions (exact bytes)

Each termless probe feeds the bytes below, reads `getCell(0, col).char` and `getCursor()`, and (except utf8) runs
`SI ESC(B ESC)B` in a `finally`. A verdict needs the control cells to read as seeded, otherwise it is inconclusive.

| Id | Feeds | Pinned expectation |
|---|---|---|
| `charsets.dec-special` | `SI ESC(B ESC)B ESC[1;1H ESC[2K qqq` · `ESC[1;2H` · `ESC(0 q ESC(B q` | cells `q U+2500 q` |
| `charsets.dec-line-drawing` | `SI ESC(B ESC)B ESC[1;1H ESC[2K jklmqxj` · `ESC[1;1H` · `ESC(0 jklmqx ESC(B` | `U+2518 U+2510 U+250C U+2514 U+2500 U+2502 j` |
| `charsets.g0-g1-switching` | `SI ESC(B ESC)B ESC[1;1H ESC[2K AlllZ` · `ESC[1;2H` · `ESC(B ESC)0 l SO l SI l` | `A l U+250C l Z` |
| `charsets.utf8` | `ESC[1;1H ESC[2K Axy?Z` · `ESC[1;2H` `U+00E9` · `ESC[1;3H` `U+4E16` | `[1]=U+00E9 [2]=U+4E16`, `A` and `Z` intact |

The pinned terminal paths for the same ids (the capture branch of `dec-special` and the cursor-position query of every id) are different
code paths that return `inconclusive` whatever they see ("consumed" or "pixels" evidence): in the stock run all four stay
`inconclusive` ("Cursor movement does not verify charset glyph rendering or mapping"). This batch grades only the termless
criteria; the derivation pins the three registrations verbatim so an upstream change giving those paths a verdict would be refused.

**Overlap.** The three DEC cases share `SI ESC(B ESC)B`, the designation `ESC ( 0` and some source characters (`q` in dec-special and
dec-line-drawing, `l` in dec-line-drawing and g0-g1). They remain distinct cases with distinct fixtures, expected cells and verdicts;
each plan replays only its own bytes and a test checks that no case grades another's reads.

## Method

Sixteenth digest-checked collector extension `ntilde-owned-charsets-grading-v1` (over line editing; refuses derivation unless
`parserStateResult` and the four registrations are present verbatim once), contracts v1, suite `ntilde-owned-charsets-graded-v1`, prefix
`chrs`, nonce `NTILDE_CS_`, receipt schema 29, source policy v23 (inventory v30, 193 files). The pinned bytes (UTF-8 text, escape
designations and shift controls) are fed untouched, one write each; the pinned termless code runs over the parent-journaled cells and the
parent re-executes it; the independent contract grader must agree wherever semantics coincide (recorded divergences: none).

**Physical cells.** `readScreen` text skips wide continuations and trims the row, and attribute strings omit continuations too (the
observer refuses a row with a wide character). This suite therefore requests stored cell details only (existing opt-in projection, which
covers every physical cell with the Wide, WideContinuation and HasExtendedText flags) and derives each column from those flags and
the row text; extended text makes the rest of its row ambiguous (no cell invented), and an incoherent row is refused. No App change.
The pinned probe sees the continuation cell as `""`; it never reads it, the contract requires it.

The contract is stricter than the pinned probes: exact decoded cells and storage kind (narrow, wide, continuation), every other cell
blank with neutral stored attributes, the exact cursor, and these controls (each a separate journaled read):

- **ASCII same-source control:** the pinned seed (`qqq`, `jklmqxj`, `AlllZ`, `Axy?Z`) before any designation; an **uppercase** control
  (`QQQ`, `JKLMQX` under DEC stay unchanged: uppercase cannot prove restoration).
- **Restoration witnesses (all cases):** lowercase `jklmqxl` written after the fixture stays ASCII (would be box drawing if DEC stayed
  active), the same text under an explicit `ESC(0` maps to `U+2518 U+2510 U+250C U+2514 U+2500 U+2502 U+250C` (so the witness
  discriminates), and again ASCII after returning. dec-special and dec-line-drawing also write source characters after `ESC ( B`.
- **G0/G1:** designation without SO changes nothing; SO with G1=ASCII changes nothing; direct G0 designation and G1+SO both map; the
  G1 designation persists past SI (selecting a bank is not designating a set).
- **UTF-8:** a wide character alone (`A世Z`: Z at column 3) and precomposed `éZ`; a decomposed `e + U+0301` control is **observed, never
  graded** (stored as extended text, distinct from the precomposed cell).

Unsupported requires valid controls and an observed mismatch; missing text, ambiguous cells (extended text), a bad seed or cursor, or
incomplete restoration is inconclusive.

## Measurement

Scripts `9d26946b` (main including the character-editing, erase-in-display and line-editing layers, plus this batch); App unchanged since `4e32f6d9` (sidecar built there). Owned run
`11a73b3cbdcae9a6fe5efbb3c66edec0`, default theme, 40x140, disposable sidecar `ntilde-sidecar-charsets`.

| Case | Pinned (parent replay + live pinned code) | Contract |
|---|---|---|
| `charsets.utf8` | supported | supported |
| `charsets.dec-special` | supported | supported |
| `charsets.dec-line-drawing` | supported | supported |
| `charsets.g0-g1-switching` | supported | supported |

Observed: utf8 stored `A`, `é` (U+00E9, narrow), `世` (U+4E16, wide at column 2) with a continuation at column 3 and `Z` at column 4,
cursor (0,4), no extended text; dec-special `q ─ q`; dec-line-drawing `┘┐┌└─│j`; g0-g1 `A l ┌ l Z`. All controls and witnesses
read as contracted. Ntilde finding: none.

Same-run reconfirmation (kept separate): every merged predecessor case supported (inventory derived from merged contracts by the
publication test, including erase-in-line, character editing, erase-in-display and line editing), the 10/10 truecolor-literal finding, the 3/3 underline-color resolution; stock counts unchanged
(86 supported, 1 unsupported, 168 inconclusive).

**Preserved attempts:** the earlier measurements (receipt 26 over erase-in-line, 27 over character editing, 28 over erase-in-display) also graded all four supported; each was superseded when main merged another layer (character editing, erase-in-display, line editing) and this batch was restacked and re-measured. All four are listed in the summary.

## Limits

Stored decoded cell contents only: no glyph, pixel, font-fallback, keyboard-encoding, locale or platform claim; the Unicode scope is
this fixture's two samples, not general UTF-8, grapheme or width completeness; modes (IRM, origin, margins) come from the measured
reset and are checked through observed placement, not buffer state. macOS owned sidecar.

## Evidence

[`2026-11-09-charsets/`](2026-11-09-charsets/): summary, report, run log, common frames and one file per read frame.
`scripts/terminfo/charsets-publication.test.mjs` re-derives the report from the retained journal.
