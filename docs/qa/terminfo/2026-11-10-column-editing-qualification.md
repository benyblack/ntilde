# Column-editing qualification: SL, SR, DECIC, DECDC (2026-11-10)

Qualifies exactly four pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `editing.sl`, `editing.sr`, `editing.decic` and
`editing.decdc`. `modes.left-right-margin` (DECLRMM/DECSLRM are not implemented), `editing.decrqcra` (rectangle checksum), the rectangle cases, Unicode and wide-cell
editing, the inline-image plane, rendered appearance and any platform other than the macOS owned sidecar are different cases and are not substituted.
The implementation under test is the stored-text DECIC/DECDC/SL/SR of PR 917 ([contract](column-editing.md)); none of the four existed before it.

## Pinned definitions

Each termless probe reads the geometry, feeds **one seed string**, reads the cells (arrays of single characters), feeds a cursor move, reads the cursor, feeds the
edit and reads the cells again. A seed that did not read back, or a wrong setup cursor, is inconclusive in every case.

| Id | Needs | Seed (one feed) | Setup | Edit | Supported iff |
|---|---|---|---|---|---|
| `editing.sl` | 2 rows, 9 columns | `ABCDEFGHIJK` / `JKLMNOPQRST` (clipped to 11 columns) | `ESC[1;1H` (x 0, y 0) | `ESC[2 @` | each of the first nine cells of both rows holds the seed cell two to its right (blank past the page) |
| `editing.sr` | 2 rows, 9 columns | `ABCDEFGHI` / `JKLMNOPQR` | `ESC[1;1H` (x 0, y 0) | `ESC[2 A` | columns 1-2 blank, columns 3-9 the seed shifted right by two |
| `editing.decic` | 3 rows, 8 columns | `ABCDEFGH` / `IJKLMNOP` / `QRSTUVWX` | `ESC[2;3H` (x 2, y 1) | `ESC[2'}` | columns 1-2 unchanged (else inconclusive), columns 3-4 blank, columns 5-8 the seed shifted right by two |
| `editing.decdc` | 3 rows, 8 columns | `ABCDEFGHIJ` / `IJKLMNOPQR` / `QRSTUVWXab` (clipped to 10) | `ESC[2;3H` (x 2, y 1) | `ESC[2'~` | columns 1-2 unchanged (else inconclusive), columns 3-8 the seed cell two to the right |

The pinned probes never read the cursor after the edit, any row past the seeded ones, attributes, margins, origin or insert mode or the pen. The terminal pixel path of
every registration (a cursor move and the edit, then a cursor position report and `unmeasuredCellResult`) never carries a verdict; the derivation pins all four
registrations verbatim with their terminal paths and the helper bodies, so an upstream change that gave one a verdict would be refused. In the stock run all four cases
stay `inconclusive` (`insufficient-evidence`, evidence `query`).

## Effect contract

From DEC [DECIC](https://vt100.net/docs/vt510-rm/DECIC.html) and [DECDC](https://vt100.net/docs/vt510-rm/DECDC.html) (insert or delete at the cursor column within the
scrolling margins, no effect outside them, blank columns entering at the right margin) and xterm's SL/SR (ECMA-48 SL/SR applied to the scrolling region from the left
margin while the cursor row is inside it). Left/right margins are not implemented, so the page edges are the horizontal margins; nothing here claims margin-bounded behavior.

## Method

Twenty-fifth digest-checked collector extension `ntilde-owned-column-editing-grading-v1` (derived over the rectangle-editing one, `derivedFromAdapterId`
`ntilde-owned-rectangle-editing-grading-v1`), contracts v1, suite `ntilde-owned-column-editing-graded-v1`, prefix `cole`, nonce `NTILDE_CE_`, minimum geometry 13x32
(the measured screen is 40x140). The pinned bytes are fed byte-exactly, one write each, on a measured reset screen; the pinned probe runs over the parent-journaled screens
and the parent re-executes it. The independent contract grader compares every cell of the whole screen, with per-cell DECSCA protection, against a column model written
for this suite and never grades more permissively than the pinned probe (recorded divergences: none).

Every case journals the pinned groups (initial blank screen, the seed, the cursor setup, the edit), then a measured block seed (`blockBefore`: seven rows with ten
distinct characters at the left edge **and** ten at the right edge, so columns pushed off and blanks entering at the edge are both visible), the graded control groups,
the observed pending-wrap group, and a measured attribute and protection seed (`attrBefore`: plain, bold, red-on-blue and plain rows, `ROW` and a right-edge `XYZ`
written under DECSCA 1) followed by its control. A group's write begins by clearing margins, origin mode, insert mode, SGR and protection. Margins, origin mode and
insert mode are read from the App's stored buffer state in every read; a read whose state differs from the contracted setup is inconclusive. The shared observer allows
64 captures per suite, so related controls share one read and the model composes their steps (a wrong count, start column or region in any step still changes the
final screen); SL and SR take 13 reads, DECIC and DECDC 14, 57 captures in all.

- **counts**: one, omitted and zero (each one), then three, composed; **clip**: an oversized count clamps at the right edge.
- **region**: margins 3-5 with the cursor inside them (only those rows move), then the cursor on rows 1 and 7 (no-ops); **origin**: the same under DECOM, parked relative to the region.
- **modes**: insert mode on, then an extra parameter, a colon subparameter and a private leader (no-ops), then an admitted edit that insert mode must not change.
- **columns** (DECIC/DECDC): at the first column, then at the last column (the count clamps to one). SL/SR start at the left margin, which every SL/SR control shows with the cursor elsewhere.
- **attributes and protection**: under a different pen (italic, palette 3 on palette 2), moved cells keep their stored tuples and protection, introduced blanks carry no visual attribute and no protection, protected text pushed past the edge disappears, and a reference cell printed afterwards proves the pen and cursor were kept. The colors of introduced blanks are observed, not required.

**Observed, not graded:** whether a column edit at the last column keeps the pending-wrap state (a character printed afterwards wraps to the next row or overwrites the
last column). DEC does not define it.

Unsupported needs valid controls and an observed mismatch. An invalid or missing seed, reverse video not proven off, a geometry below the contract minimum, a stale
palette binding, contradictory stored projections, missing protection or buffer state, a margin or mode the App did not confirm, an unmeasured pen reference or
incomplete restoration is inconclusive or refused, never conclusive.

## Measurement

Scripts (adapter) revision `a6457537c7a856441e99f42f7ccffb98de811299`; App source revision `ed95d75900124a77e5cfbde0735a68c657c2da9c` (the last commit that touched
`src/` in the measured tree: PR 917's DECIC/DECDC/SL/SR on main `07322bb3`); the Release sidecar was built from that tree with the repository wrappers. Owned run
`140e8da353177b481d17be1a7474be57`, receipt schema 38, source policy v32 (inventory v39, 241 files), default theme, 40x140, App version 0.13.0 (operator supplied; not
independently verified), disposable sidecar `artifacts/sidecar-col-edit`, stock collector run `7fc55df7d17c31ab13c336ee0c47115f`, measured 2026-11-10T15:07Z on macOS
with Node 24, under the shared desktop-collection lock.

| Case | Pinned (parent replay + live pinned code) | Contract | Stock grade |
|---|---|---|---|
| `editing.sl` | supported | supported | inconclusive (`query`) |
| `editing.sr` | supported | supported | inconclusive (`query`) |
| `editing.decic` | supported | supported | inconclusive (`query`) |
| `editing.decdc` | supported | supported | inconclusive (`query`) |

**SL and SR.** The pinned rows read `CDEFGHIJK` / `LMNOPQRST` and `  ABCDEFGHI` / `  JKLMNOPQR` with the cursor still at (0,0). The composed counts shifted the block by
six from the left margin with the cursor in column 6; blank columns entered at the right edge (SL) and the right-edge text was pushed off (SR). The oversized count
cleared every row of the page. Only the three region rows moved, and the cursor above and below the margins changed nothing. DECOM and insert mode behaved as modelled,
and the three non-admitted spellings changed nothing.

**DECIC and DECDC.** The pinned rows read `AB  CDEFGH` and `ABEFGHIJ` (and the matching rows 2-3) with the cursor still at (1,2). The composed counts inserted (deleted)
six columns at column 6 on every row; the oversized count cleared from the cursor column; at the first column whole rows moved, and at the last column only that column
was blanked.

**Attributes and protection.** In all four cases moved cells kept their stored tuples and DECSCA protection (`ROW` and `XYZ` moved with their protection; SR and DECIC
pushed `Y` and `Z` past the edge). Introduced blanks carried no visual attribute and no protection, and the reference cell proved the pen and cursor were unchanged.
**Observed, not graded:** introduced blanks carry the current colors (the ICH/DCH fill), and the pending-wrap state was kept (the next character wrapped).

**Predecessors and findings.** Every merged predecessor suite (twenty-three, including rectangle editing) and both finding suites reconfirm in the same run with the same
outcomes as their own publications; the receipt re-derives all of them.

**Preserved attempts:** (1) run `6ed5c2fd13272c9e81e0664900d63d6d` (scripts `f19d0e1c`, same App and sidecar) aborted in this suite with `Oversized owner response`
after every earlier suite had completed: the attribute read (34 KB of protection and attribute runs) exceeded the 16 KiB control budget of the owner socket. The fix
(scripts `a6457537`) admits `cole` reads to the cell snapshot budget exactly as the selective-erasure suite's reads were; it is retained under
`artifacts/terminfo/owned-WH1b72` and no evidence from it was used. (2) The published run.

## Layout

`summary.json` (run identity, context, receipt, same-run predecessor outcomes, stock grades, attempts), `run-log.json`, `journal-common.json` (the report header and
the start, bind and finish frames), one `case-<id>.json` per case and one `journal-<id>-read<n>.json` per read frame. Arrays of flat records (attribute and detail runs)
are written one record per line so the largest frame stays reviewable. `col-edit-publication.test.mjs` reassembles the journal and re-derives the report byte for byte.

## Boundaries

Stored ASCII state with full-width horizontal margins only: no left/right-margin, Unicode, wide-cell, scaled-text, image-plane, rendered-appearance or platform claim.
DECRQCRA and `modes.left-right-margin` are later batches. History is observed only (column edits never scroll). The pending-wrap reading and the colors of introduced
blanks are observations, not criteria. The pinned terminal paths never carry a verdict and are not qualified; stock grades are unchanged.
