# Erase-in-line qualification: EL 0, EL 1 and EL 2 (2026-11-08)

Qualifies exactly three pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`erase.line.right`, `erase.line.left` and `erase.line.all`. `erase.screen.*`, `erase.display.*`, selective erasure (DECSEL/DECSED),
character and line editing and the ED3 saved-lines case are different cases and are not substituted.

## Pinned definitions

`eraseRowResult(ctx, setup, sequence, expected, adjacentRow, expectedCursorX)` feeds the setup, reads the first five cells of row 0
(and row 1 for the all case) and, for right/left, the cursor; feeds the erase sequence and reads the cells again. It is supported iff
the five cells match (blank is `""` or `" "`), row 1 still reads `KEEP!` (all case), and the setup cursor was (2,0) (right/left only).
A missing fixture is inconclusive. It never reads the cursor after the erase, retained attributes, other rows or other columns.

| Id | Setup | Erase | Expected row 0 | Other pinned checks |
|---|---|---|---|---|
| `erase.line.right` | `ABCDE\x1b[3G` | `\x1b[K` (**omitted** parameter) | `A,B,blank,blank,blank` | setup cursor 2,0 |
| `erase.line.left` | `ABCDE\x1b[3G` | `\x1b[1K` | `blank,blank,blank,D,E` | setup cursor 2,0 |
| `erase.line.all` | `ABCDE\r\nKEEP!\x1b[1;3H` | `\x1b[2K` | all blank | row 1 `KEEP!` |

**Two paths, graded separately.** The pinned terminal pixel path (`captureEraseFixture`) is a different code path: it uses the
**explicit 0** spelling (`\x1b[0K`) for the right case, and whatever the pixels show it returns `pass: false`, outcome
`inconclusive`, reason `insufficient-evidence` ("capture-only assertion; no erase support judgment"). It never carries a verdict, so
there is no pixel criterion to qualify. This batch grades only the termless criterion; the pixel path is not qualified, and the
derivation pins the pixel helper verbatim so a future upstream change that gave it a verdict would be refused. In the stock run all
three cases stay `inconclusive` ("Erase pixel fixture needs capture…").

**Naming:** Ntilde's `ntilde-owned-ascii-cells-v1` suite (`scripts/terminfo/cells.mjs`) has identically named experiments with
different bytes (explicit 0/1/2, no neighbor row). Those are Ntilde observations, not these fixtures and not benchmark grades.

## Method

Twelfth digest-checked collector extension `ntilde-owned-erase-line-grading-v1` (derived over the SGR closure one; it refuses
derivation unless the three registrations and the `isBlank`, `rowCells`, `eraseRowResult` and `captureEraseFixture` bodies are
present verbatim once), contracts v1, suite `ntilde-owned-erase-line-graded-v1`, prefix `erln`, nonce `NTILDE_EL_`. The pinned
setup and erase bytes are fed byte-exactly, one write each, on a measured reset screen (the setup is read before the erase is
fed); the pinned termless probe runs over the parent-journaled screens and the parent re-executes it; the independent contract
grader must agree wherever semantics coincide (recorded divergences: none).

The contract is the finer grader and only ever stricter than the pinned probe: the cursor must stay at its seeded cell, every
other cell of the 40x140 screen must stay blank with default storage, retained cells keep their stored text and attributes, and
erased cells read blank. Controls, each a separate journaled read:

- initial blank screen; the setup read (the unmodified seed, which is also the no-op control);
- **other spellings** of the same setup: omitted, explicit 0, explicit 1, explicit 2 (wrong direction and accidental whole-line
  clearing). They are observations, never grades: a sibling spelling that misbehaves is reported and cannot change another case's grade;
- **wide row:** `ABOVE-ROW!` / `ABCDEFGHIJ` / `BELOW-ROW!` with the cursor at column 2 of the middle row: unaffected rows, columns
  past column 5, and the cursor-cell boundary (the cursor cell is erased by EL 0 and EL 1);
- **attributes:** a bold-seeded row with the blue background pen active when the erase runs. Retained cells must stay bold with
  default colors (required). Whether erased cells take the current background (BCE) is **observed, not required**, because no pinned
  criterion constrains it. A reference cell written under the same `SGR 44` pen (a separate read) defines the contracted background;
  the observation compares the erased cells with it by resolved color and, separately, by stored representation.

Unsupported needs valid controls and an observed mismatch; a missing or invalid seed, DECSCNM not proven off, too small a screen,
stale or incoherent evidence or incomplete restoration is inconclusive or refused.

## Measurement

Scripts `5a3b9533` (the merge of current main with the SGR closure layer); the App source is unchanged since `783dd991` (the sidecar was built there; no App change). Owned run
`be21c8c78a42205d3be915a1a44ebd79`, receipt schema 25, source policy v19 (inventory v26, 173 files), default theme, 40x140,
disposable sidecar `erase-line` built from this checkout.

| Case | Pinned (parent replay + live pinned code) | Contract | Layer |
|---|---|---|---|
| `erase.line.right` | supported | supported | stored cells, cursor, stored attributes |
| `erase.line.left` | supported | supported | stored cells, cursor, stored attributes |
| `erase.line.all` | supported | supported | stored cells, cursor, stored attributes |

Observed: seed `ABCDE`, cursor (0,2) in every case. Right erased to `AB` + blanks (cell 2 included), left to blanks + `DE`, all to an
empty row with row 1 still `KEEP!`; the cursor stayed at (0,2) every time. Omitted and explicit 0 gave the same result; EL 1 and EL 2
differed as expected. In the wide row EL 0 left `AB`, EL 1 left `DEFGHIJ`, EL 2 left nothing, with the rows above and below intact.
Retained bold cells kept flags 769; **background-color erase is observed:** with the blue pen active the erased cells (for EL 0, from
column 2 through the last column) carry the pen's resolved color `(0,0,238)`, but **stored as a literal ARGB (`4278190318`) rather than the
palette index 4 a cell written under the same pen stores**. That representation difference is an Ntilde-specific observation, not a
requirement and not a benchmark grade; it affects nothing the pinned criteria read.

**Preserved attempts** (all seven in the summary, with run ids and script revisions): (1) the disposable app aborted at startup (Avalonia
render timer, display asleep); (2) the suite ran but the later private-clipboard stage timed out and no receipt was completed; (3)
`owned-CEXOUy`, complete but superseded — the first full measurement graded the attribute control over columns 0–4 only, so the correct
end-of-line background fill read as damage and two cases graded unsupported; the contract was fixed (re-deriving that retained run with
the fixed contract gives three supported) and the measurement repeated; (4) `owned-iVrLXY`, complete, superseded when the observed
record was compacted for reviewable file sizes; (5) `owned-cAU9u2`, complete, superseded after Codex review found the background
observation called any non-default color the current pen (a pen reference read was added); (6) `owned-w6W0UA`, complete, superseded
because that reference showed the same color stored in a different representation, which the observation now reports separately; (7) `owned-aBspA8`, the pre-integration final run (receipt 24, policy v18, 168 files), superseded because main
meanwhile merged the SGR closure layer: this layer was restacked as the twelfth extension over it (receipt 25, policy v19, 173 files) and the
whole suite re-measured on the merged scripts.
Every complete attempt graded all three cases supported except (3).

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (the inventory is derived from the
merged contracts by the publication test); the separate 10/10 truecolor-literal finding; the separate 3/3 App-observed underline-color
resolution; stock collector counts unchanged (86 supported, 1 unsupported, 168 inconclusive).

## Limits

Stored state only: no resolved-color, glyph or pixel claim; the pinned pixel capture path is not qualified. macOS owned sidecar,
default theme.

## Evidence

[`2026-11-08-erase-line/`](2026-11-08-erase-line/): summary, report, run log, common frames and one file per read frame.
`scripts/terminfo/erase-line-publication.test.mjs` re-derives the report from the retained journal.
