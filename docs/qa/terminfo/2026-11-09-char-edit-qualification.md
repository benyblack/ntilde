# Character-editing qualification: ICH, DCH and REP (2026-11-09)

Qualifies exactly three pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`editing.insert-chars`, `editing.delete-chars` and `editing.repeat-char`. Line editing (IL/DL), DECIC/DECDC, the rectangle
operations, erasure, scroll regions and Unicode, wide-cell or combining-character editing are different cases and are not
substituted. This qualifies the **ASCII fixture only**.

## Pinned definitions

Each termless probe feeds, reads and gates in this order; the three `term` (pixel/query) paths write different bytes and end in a
cursor-position query (`unmeasuredCellResult`), so they never carry a verdict and are not qualified.

| Id | Feeds (in order) | Reads | Supported iff |
|---|---|---|---|
| `editing.insert-chars` | `ESC[1;1H ESC[2K ABCDEZQH`, `ESC[1;3H`, `ESC[1@` (**explicit 1**) | cells 0..7 (`ABCDEZQH`), cursor (2,0), cells 0..7 | `after[0..1]=AB`, `after[2]` blank, `after[3..7]=CDEZQ` |
| `editing.delete-chars` | same seed and cursor, `ESC[1P` (**explicit 1**) | cells 0..7, cursor (2,0), cells 0..6 | `after[0..1]=AB`, `after[2..6]=DEZQH` |
| `editing.repeat-char` | `ESC[1;1H ESC[2K AX   Z`, `ESC[1;2H`, `X`, `ESC[3b` | cells 0..5 (`AX   Z`), cursor (1,0), cells 0..5 and cursor (2,0), cells 0..5 | `after[0]=A`, `after[5]=Z`, `after[1..4]=XXXX` |

A missing fixture, changed control (`AB` prefix or `A`/`Z` flank) or too-small geometry is inconclusive. The pinned probes never
read the cursor after the edit, any column the fixture does not name, other rows or any attribute.

## REP adjacency (a deliberate, documented deviation in transport only)

REP repeats the preceding graphic character; ECMA-48 8.3.103 leaves it undefined after any intervening control function, and
the App parser clears the repeatable character on every control sequence. The owned fence ends each write with a DA query,
which is such a control function. The pinned harness has no bytes between `X` and `ESC[3b`, so this suite sends them in **one
fenced write** (`X ESC[3b`: the same byte stream, no DA between them) and takes the pinned pre-REP reads (cells and cursor after
`X`) from a separate group whose bytes end at `X`. The pinned code runs over the recorded screens in its own feed order, and the
parent re-executes it. Whether REP survives a barrier is recorded as the labeled `split` control: **it does not** (the row stayed
`AX___Z`, cursor 2): a REP in a separate write after the barrier repeats nothing, as the parser documents.

## Method

Thirteenth digest-checked collector extension `ntilde-owned-char-edit-grading-v1` (derived over the erase-in-line one; it refuses
derivation unless the three registrations, with their termless **and** cursor-query paths, and the `isBlank`, `parserStateResult` and
`unmeasuredCellResult` bodies are present verbatim once), contracts v1, suite `ntilde-owned-char-edit-graded-v1`, prefix `ched`,
nonce `NTILDE_CE_`. Every group is a separate parent-journaled read on a measured reset screen; the pinned probe runs over the
journaled screens and the independent contract grader must agree wherever semantics coincide (recorded divergences: none).

The contract is the finer grader and only ever stricter than the pinned probe: the whole row (not eight cells) must match, every
other cell of the 40x140 screen must be blank with default storage, the cursor must stay at its seeded cell (ICH, DCH) or sit
after the last repeat (REP), and moved or retained cells keep their stored attributes. Controls:

- initial blank screen; the seed, cursor and (REP) `X` reads, which are the no-op baseline;
- **other spellings**: omitted, explicit 0, 2 and 999 for ICH/DCH; omitted, 0, 1 and 5 for REP. Observations, never grades;
- **wide row**: `ABOVE-ROW!` / `ABCDEFGHIJ` (REP: `AX   ZQRST`) / `BELOW-ROW!`, the whole-row shift and unaffected rows;
- **right edge** (ICH, DCH): `WXY` ending in the last column with the cursor on `W`: ICH truncates, DCH exposes a blank;
- **REP after a barrier**: the labeled `split` observation above;
- **attributes**: D and E bold; moved cells must keep their bold-only tuple (required). Blank cells created by ICH/DCH, and the cells
  REP writes, are **observed** against a reference cell written under the same `SGR 44` (REP: `SGR 1;44`) pen, by resolved color and
  by stored representation separately, and are not required: no pinned criterion constrains them.

Unsupported needs valid controls and an observed mismatch; a missing or invalid seed, cursor or REP setup, DECSCNM not proven off, a
too-small screen, stale or incoherent evidence or incomplete restoration is inconclusive or refused.

## Measurement

Scripts `bf4d0755` (current main `4e32f6d9` plus this batch); the App source is unchanged since `c16997c2` (the Release sidecar
was built from this checkout, no App change). Owned run `4583b045720e8ff917db8b11f4be4ffb`, receipt schema 26, source policy v20
(inventory v27, 178 files), default theme, 40x140, disposable sidecar `artifacts/sidecar-char-edit`.

| Case | Pinned (parent replay + live pinned code) | Contract | Layer |
|---|---|---|---|
| `editing.insert-chars` | supported | supported | stored cells, cursor, stored attributes |
| `editing.delete-chars` | supported | supported | stored cells, cursor, stored attributes |
| `editing.repeat-char` | supported | supported | stored cells, cursor, stored attributes |

| Case | Before | After | Cursor | Attribute control |
|---|---|---|---|---|
| ICH `ESC[1@` | `ABCDEZQH` | `AB_CDEZQH` | (0,2) before and after | D, E keep bold at their new columns; the created blank carries the pen background (palette index 4, same as the reference cell) |
| DCH `ESC[1P` | `ABCDEZQH` | `ABDEZQH_` | (0,2) before and after | D, E keep bold; the blank exposed at column 139 carries the pen background |
| REP `X ESC[3b` | `AX___Z` | `AXXXXZ` | (0,2) before X-write, (0,5) after | A and Z stay neutral; the four X cells carry the pen (bold, palette index 4) |

Other observations: omitted and explicit 0 equal explicit 1 for ICH and DCH; count 2 gives `AB__CDEZQH` / `ABEZQH`; 999 leaves `AB`.
REP omitted, 0 and 1 all give `AXX__Z`; count 5 gives `AXXXXXX` (REP overwrites the Z flank). At the right edge ICH turned `WXY`
into `_WX` (Y dropped) and DCH into `XY_`. Every spelling matched the ECMA-48 expectation.

**Preserved attempts** (both in the summary): (1) `owned-kuyTIG`, the disposable app started but the worker's PTY fence received stray
input before the first cell read, so no cell was observed; not evidence, repeated unchanged; (2) the final run above.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported, including the erase-in-line suite (the
inventory is derived from the merged contracts by the publication test); the separate 10/10 truecolor-literal finding; the separate
3/3 App-observed underline-color resolution. **The stock collector was not re-run** (it needs an interactive Ntilde tab): its pinned
bundle, probeHash and the App are unchanged, so the carried counts (168 inconclusive, 86 supported, 1 unsupported) are labeled as carried;
the three cases are inconclusive there through the cursor-query path.

## Limits

Stored ASCII state only: no Unicode, wide-cell or combining-character editing, no resolved-color, glyph or pixel claim; the pinned
pixel/query paths are not qualified. macOS owned sidecar, default theme.

## Evidence

[`2026-11-09-char-edit/`](2026-11-09-char-edit/): summary, report, run log, common frames and one file per read frame.
`scripts/terminfo/char-edit-publication.test.mjs` re-derives the report from the retained journal.
