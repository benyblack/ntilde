# Line-editing qualification: IL and DL (2026-11-09)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`editing.insert-lines` and `editing.delete-lines`. Character editing (`editing.insert-chars`, `editing.delete-chars`, `editing.repeat-char`),
scrolling (SU/SD/SL/SR), erase and rectangle cases are different cases and are not substituted.

## Pinned definitions

Both termless probes need 6 columns and 4 screen lines, seed four rows with one feed each (`ESC[<row>;1H ESC[2K <text>`: `AAAAA`, `BBBBB`,
`CCCCC`, `DDDDD` on rows 1-4), read the first five cells of rows 1-4, feed `ESC[2;1H`, read the cursor (must be x=0,y=1), feed the edit and read
the five cells again.

| Id | Edit | Supported iff (after the edit) |
|---|---|---|
| `editing.insert-lines` | `\x1b[1L` (explicit count) | row 1 still `AAAAA` (control), row 2 blank, row 3 `BBBBB`, row 4 `CCCCC` |
| `editing.delete-lines` | `\x1b[1M` (explicit count) | row 1 still `AAAAA` (control), row 2 `CCCCC`, row 3 `DDDDD` |

An unread seed or a wrong setup cursor is inconclusive. The probes never read the cursor after the edit, any cell past column 5, the row IL
pushes `DDDDD` into, the blank rows DL creates, attributes, margins, origin mode or history.

**Pixel path.** The second function of each registration writes `ESC[3;5H` and the edit, asks for a cursor position report and returns
`unmeasuredCellResult`: never a verdict. This batch grades only the termless criterion; the pixel path is not qualified and its registration is pinned
verbatim so an upstream change that gave it a verdict would be refused. In the stock run both cases stay `inconclusive` (`insufficient-evidence`, evidence `query`).

**Naming:** Ntilde's `ntilde-owned-cell-editing-v1` suite has identically named experiments with different bytes; those are Ntilde observations, not these fixtures.

## Method

Fifteenth digest-checked collector extension `ntilde-owned-line-editing-grading-v1` (derived over the erase-in-display one; it refuses derivation unless
the two registrations and the `parserStateResult`, `isBlank` and `unmeasuredCellResult` bodies are present verbatim once), contracts v1, suite
`ntilde-owned-line-editing-graded-v1`, prefix `ledt`, nonce `NTILDE_LE_`. The pinned bytes are fed exactly, one write each, on a measured reset
screen; the pinned probes run over the parent-journaled screens at the points where they read, and the parent re-executes them. The independent
contract grader must agree wherever semantics coincide; it is only ever stricter than the pinned probe and every difference is recorded.

Graded by the contract (each a separate journaled read; the whole 40x140 screen is compared with an independent line-editing model, including
unaffected rows and columns):

- the pinned edit: exact lines and the cursor at row 2, column 1 (no-op, reversed movement, wrong count, wrong blank, ICH/DCH-like character edits and whole-screen scrolling are each diagnosed);
- a full-height boundary at the second-to-last row (IL discards the last row, DL leaves a blank last row, the top row is untouched);
- a DECSTBM 3;5 scroll region with the cursor inside it (boundary discard / blank row at the region bottom, rows outside unchanged) and above and below it (no change);
- origin mode (DECOM) inside the region;
- an attribute control: moved cells keep their stored tuple, new blank lines carry no attribute but a background.

Margins, origin mode and insert mode are read from the App's stored buffer state in every read, never from cursor replies; a setup the App
did not confirm is inconclusive. **Observed, not graded:** the omitted, zero, two and oversized (`999`) count spellings, whether new blank lines take the
current background, the cursor column after an edit away from column 1, and scrollback. Unsupported needs valid controls and an observed mismatch; invalid
setup, missing evidence, stale or incoherent frames or incomplete restoration are inconclusive or refused.

## Measurement

Scripts `6ec38872` (this layer merged with current main, including the character-editing and erase-in-display layers), App source unchanged since `c16997c2` (the sidecar was built there); owned run
`05b14f48fd26fd3860418c6e506ac1f7`, disposable sidecar `line-edit` built from this checkout; default theme, 40x140, receipt schema 28, source policy v22 (inventory v29, 188 files).

| Case | Pinned (parent replay + live pinned code) | Contract | Layer |
|---|---|---|---|
| `editing.insert-lines` | supported | supported | stored cells, cursor, margins, stored attributes |
| `editing.delete-lines` | supported | supported | stored cells, cursor, margins, stored attributes |

Observed: IL left `AAAAA`, a blank row, `BBBBB`, `CCCCC`, `DDDDD`; DL left `AAAAA`, `CCCCC`, `DDDDD` and blank rows below; the cursor stayed at row 2, column 1.
At the full-height boundary IL discarded the last row and DL left a blank last row. Inside the scroll region (margins read back as rows 3-5) IL discarded the
region's last row and DL blanked it, rows 6-7 and the cursor-above/below reads were unchanged, and origin mode behaved identically. All four count spellings matched
the model. Moved cells kept their stored tuples. **New blank lines keep the default background** even with a blue pen active (no background fill), and **the cursor column
does not move to column 1**: after an edit with the cursor at column 11 it stays at column 11. Both are Ntilde observations, not requirements and not benchmark grades.
Scrollback stayed 0 in every read; the reads run on the alternate screen, so nothing is claimed about history.

**Preserved attempts** (all three in the summary, with run ids and script revisions): (1) the line-editing suite completed (both supported) but a later static-image stage failed with "owner did not respond", so no receipt was finalized; (2) a complete pre-integration run (receipt 26, policy v20, 178 files), superseded because main merged the character-editing layer (#901); (3) a complete run on that restack (receipt 27, policy v21, 183 files), superseded because main merged the erase-in-display layer (#900) and CodeRabbit review fixes (early pinned-probe exits, attribute seed columns, graded-reads-only state gate) changed the collector and grader. Every complete attempt graded both cases supported.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (the inventory is derived from the merged contracts by the
publication test); the separate 10/10 truecolor-literal finding; the separate 3/3 App-observed underline-color resolution; stock collector counts unchanged
(86 supported, 1 unsupported, 168 inconclusive).

## Limits

Stored ASCII state only: no resolved-color, glyph, pixel, Unicode, wide-cell or scrollback claim; the pinned pixel path is not qualified. macOS owned sidecar, default theme.

## Evidence

[`2026-11-09-line-editing/`](2026-11-09-line-editing/): summary, report index and one report per case, run log, common frames and one file per read frame.
`scripts/terminfo/line-edit-publication.test.mjs` re-derives the report from the retained journal.
