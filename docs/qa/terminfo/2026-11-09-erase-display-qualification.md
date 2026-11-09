# Erase-in-display qualification: ED 0, ED 1 and ED 2 (2026-11-09)

Qualifies exactly three pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`erase.screen.below`, `erase.screen.above` and `erase.screen.all`. `erase.screen.scrollback` (ED 3, saved lines), `erase.line.*`,
selective erasure (DECSED/DECSEL), `erase.characters.*` and character/line editing are different cases and are not substituted.

## Pinned definitions

`eraseScreenResult(ctx, sequence, expected)` reads the measured row count `lines` (`getScrollback().screenLines`), takes
`middle = floor(lines / 2)`, feeds **one geometry-dependent setup string**
`ESC[1;1H AAAAA ESC[middle+1;1H BBBBB ESC[lines;1H CCCCC ESC[middle+1;3H`, reads the first five cells of rows `[0, middle, lines-1]`, the
cursor and the scrollback counts, feeds the erase sequence and reads them again. It is supported iff every expected cell matches (blank is
`""` or `" "`) **and the cursor is exactly where it was before the erase**. It is inconclusive when the seed, the cursor (column 2 of the middle
row) or the scrollback total (`totalLines >= lines`) is not as expected. It never reads cells beyond column 4, other rows, attributes, modes, or
whether the saved lines changed.

| Id | Erase (explicit spelling) | Row 0 | Row middle | Row last | Other pinned checks |
|---|---|---|---|---|---|
| `erase.screen.below` | `\x1b[0J` | `AAAAA` | `BB   ` | blank | cursor preserved |
| `erase.screen.above` | `\x1b[1J` | blank | `   BB` | `CCCCC` | cursor preserved |
| `erase.screen.all` | `\x1b[2J` | blank | blank | blank | cursor preserved |

On the owned 40x140 screen `middle = 20`, so the pinned setup is `\x1b[1;1HAAAAA\x1b[21;1HBBBBB\x1b[40;1HCCCCC\x1b[21;3H`, the cursor is
(row 20, col 2), and the read rows are 0, 20 and 39. **The fixture bytes depend on the measured row count**: this contract pins the owned 40-row
screen and grades any other geometry inconclusive (it never substitutes an "equivalent" fixture).

**Spellings.** All three pinned fixtures use the **explicit** parameter (`0J`, `1J`, `2J`); none uses the omitted spelling (`\x1b[J`). The omitted
spelling and the two other explicit spellings run as separate, labeled controls: observations, never grades.

**Two paths, graded separately.** The pinned terminal pixel path (`captureEraseFixture`, kind `screen`) erases at `ESC[middle;3H` with
`middle = floor((rows + 1) / 2)` (a different row from the termless path, 20 versus 21 on 40 rows) and, whatever the pixels show, returns `pass: false`,
outcome `inconclusive`, reason `insufficient-evidence` ("capture-only assertion; no erase support judgment"). It never carries a verdict, so there is
no pixel criterion to qualify. This batch grades only the termless criterion; stored-state qualification **does not establish pixel rendering**.
The derivation pins the pixel helper verbatim so a future upstream change that gave it a verdict would be refused. In the stock run all three cases
stay `inconclusive` ("Erase pixel fixture needs capture…").

## Method

Thirteenth digest-checked collector extension `ntilde-owned-erase-display-grading-v1` (derived over the erase-in-line one; it refuses derivation
unless the three registrations and the `isBlank`, `rowCells`, `eraseScreenResult` and `captureEraseFixture` bodies are present verbatim once; the
`erase.screen.scrollback` registration is deliberately not a seam), contracts v1, suite `ntilde-owned-erase-display-graded-v1`, prefix `erdi`,
nonce `NTILDE_ED_`. The pinned setup and erase bytes are fed byte-exactly, one write each, on a measured reset screen; the pinned termless probe
runs over the parent-journaled screens (cells, cursor **and the observed saved-line count** served from the same observed screen) and the parent
re-executes it; the independent contract grader must agree wherever semantics coincide (recorded divergences: none).

The contract is the finer grader and only ever stricter than the pinned probe: every cell of the whole 40x140 screen outside the erased region
keeps its stored text and neutral attributes, every cell inside it reads blank, the cursor and every mode flag (origin, wrap, insert, pending wrap,
margins, alternate screen, cursor visibility) stay as seeded, and no saved line is removed. Controls, each a separate journaled read:

- initial blank screen; the setup read (the unmodified seed, which is also the no-op control);
- **other spellings** of the same setup (omitted, explicit 0, 1, 2 minus the pinned one): wrong direction and accidental whole-screen clearing;
- **full-screen fill:** a distinct ten-character marker `ROWnn-ABCD` on every row plus a marker in the last column, cursor at (20, 4): unaffected
  rows and columns, the cursor-cell boundary and an accidental whole-screen clear;
- **boundary:** the same fill with the cursor on the first column (ED 0), the last column (ED 1) or the bottom-right cell (ED 2), so a cursor-cell
  inclusion error at the screen edges and accidental cursor homing are visible;
- **attributes:** the pinned seed written bold with the blue background pen active when the erase runs. Retained cells must stay bold with default
  colors (required). Whether erased cells take the current background (BCE) is **observed, not required**. A reference cell written under the same
  `SGR 44` pen defines the contracted background; erased cells are compared with it by resolved color and, separately, by stored representation;
- **history (main screen):** the alternate screen has no saved lines, so a separate control seeds four saved lines on the **main** screen
  (`HIST-A`, `HIST-B`, `HIST-C`, blank), seeds the pinned fixture and erases. The saved lines must survive: **ED 2 is not ED 3**.

**Cleanup bytes (not a qualification of CSI 3J).** The main-screen control enters and leaves the main screen with `\x1b[3J` in the cleanup feeds only
(`\x1b[?1049l … \x1b[3J` before the seed, `\x1b[0m\x1b[2J\x1b[3J\x1b[H\x1b[?1049h` after the reads), the same disposable-screen reset the History and
Editing suites use. It is never the erase under test and never graded; it returns the disposable main screen to blank so the start and finish frames
are identical. If the main screen is not blank when the suite starts, that restoration fails and the run is refused. The between-group clears use the shared
reset (`ED 2`), so a broken ED 2 would make later controls inconclusive rather than failed.

Unsupported needs valid controls and an observed mismatch; a missing or invalid seed, DECSCNM not proven off, a geometry other than 40x140, missing
buffer state or history, stale or incoherent evidence or incomplete restoration is inconclusive or refused.

## Measurement

Scripts `04eabc83` (committed final scripts); the App source is unchanged since `c16997c2` (the sidecar `erase-display` was built from this checkout, no App
change). Owned run `7af1c73318379005311633a21267fdae`, receipt schema 26, source policy v20 (inventory v27, 178 files), default theme, 40x140,
disposable sidecar.

| Case | Pinned (parent replay + live pinned code) | Contract | Layer |
|---|---|---|---|
| `erase.screen.below` | supported | supported | stored cells, cursor, stored attributes, buffer state, saved lines |
| `erase.screen.above` | supported | supported | stored cells, cursor, stored attributes, buffer state, saved lines |
| `erase.screen.all` | supported | supported | stored cells, cursor, stored attributes, buffer state, saved lines |

Observed: seed `AAAAA`/`BBBBB`/`CCCCC` on rows 0/20/39, cursor (20, 2) in every case. ED 0 left `AAAAA`, `BB` and blank; ED 1 left blank, `BB` at
columns 3-4 and `CCCCC`; ED 2 left everything blank; the cursor stayed at (20, 2) and every mode flag was unchanged. In the fill control ED 0 kept rows
0-19 and `ROW2` of row 20, ED 1 kept row 20 from column 5 (including its last-column marker) and rows 21-39, ED 2 left nothing. At the boundary the cursor
stayed on its first/last/bottom-right cell and the cursor row was erased entirely. Retained bold cells kept flags 769.
**Background-color erase is observed:** with the blue pen active every erased cell (2798, 2803 and 5600 cells, every erased position including the erased seed letters) carries the pen's resolved color
`(0,0,238)`, but **stored as a literal ARGB (`4278190318`) rather than the palette index 4 a cell written under the same pen stores**. That
representation difference is an Ntilde-specific observation, not a requirement and not a benchmark grade.
**Viewport versus scrollback:** with four saved lines on the main screen, ED 0, ED 1 and ED 2 left all four lines unchanged and added none.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (37 unique cases: the four ASCII cases, all 32 pinned
SGR ids and the three erase-in-line cases; the inventory is derived from the merged contracts by the publication test); the separate 10/10
truecolor-literal finding; the separate 3/3 App-observed underline-color resolution; stock collector counts unchanged (86 supported, 1 unsupported,
168 inconclusive).

**Preserved attempts** (all four in the summary, with run ids and script revisions): (1) `d9fbd927…`, complete but superseded: Codex review found the
background-color observation skipped the 15 seeded letters that lie inside the erased region and that the attribute and history controls did not check
mode preservation; the contract was fixed (all three graded supported both times); (2) `fe2c62b8…`, complete but superseded because shared fixture and
projection code changed afterwards (test speed) and the final run measures the committed final scripts; (3) a failed start (EPERM creating the private
fixture directory, no cell observed); (4) a run aborted with "Unexpected input during cell collection" while another session was also running desktop work
and before this session held the shared desktop lock; no receipt. Attempts 3 and 4 are not evidence.

## Limits

Stored state only: no resolved-color, glyph or pixel claim; the pinned pixel capture path is not qualified and CSI 3J is not graded. macOS owned
sidecar, default theme, 40x140 geometry only.

## Evidence

[`2026-11-09-erase-display/`](2026-11-09-erase-display/): summary, run log, common frames with the report header, one file per case and one file per read
frame. `scripts/terminfo/erase-display-publication.test.mjs` re-derives the report from the retained journal.
