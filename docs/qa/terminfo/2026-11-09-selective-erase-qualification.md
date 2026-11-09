# Selective-erasure qualification: DECSED and DECSERA (2026-11-09)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`erase.selective` (DECSED, `CSI ? 2 J`) and `editing.decsera` (DECSERA, `CSI 1;1;1;5 $ {`). DECSEL, ISO SPA/EPA protection, the other rectangle
commands, ED 0/1/2, Unicode editing and platform qualification are different cases and are not substituted. This qualifies the **ASCII fixtures only**.

## Existing and new protection semantics

Before this batch Ntilde had no character protection: `CSI Ps " q` (DECSCA) was ignored and DECSED and DECSERA were aliases of ED and DECERA, so
both pinned cases would have erased the protected `P`. The foundation PR (merged as `2a3ee886`, a separate, reviewed change) added, in `Ntilde.VT` only:

- the DECSCA pen (`1` protected; `0`, `2` or omitted erasable), kept apart from SGR (`SGR 0` does not clear it, DECSCA does not touch colors) and from
  read-only text; it survives DECSC/DECRC and the alternate screen, and DECSTR/RIS reset it;
- protection stored as a sparse per-row side table (the 12-byte cell and `TerminalCellFlags` are unchanged), derived for wide glyphs from the anchor cell
  so a half-overwritten glyph reads unprotected; it moves with scrolling, insert/delete, reflow, resize and state transfer, and is lost in scrollback;
- DECSED modes 0/1/2 and DECSERA erase only unprotected cells (mode 2 also retires images like ED 2); with nothing protected they are exactly ED and
  DECERA; DECFRA, DECCRA and the ordinary erases clear or copy protection as the contract catalogue states.

This batch adds only **measurement**: an opt-in `readScreen(includeProtection)` on the agent observer (additive, off by default) that adds a boolean
`protected` to run-length cell details. No VT behavior changes here.

## Pinned definitions

Both cases feed exactly `ESC[1;1H ESC[1"q P ESC[0"q ABCDZ`, then the erase, then the finalizer `ESC[0"q`, and read the first six cells of row 0.

| Id | Erase | Supported iff |
|---|---|---|
| `erase.selective` | `ESC[?2J` | before `P A B C D Z`; after `P` kept and `A B C D Z` erased |
| `editing.decsera` | `ESC[1;1;1;5${` | before `P A B C D Z`; after `P` kept, `A B C D` erased and the outside-rectangle `Z` kept |

A lost `P` (or, for DECSERA, a lost `Z`) is **inconclusive** in the pinned helper, not unsupported. The pinned terminal pixel/query path of each case ends
in a cursor-position query (`unmeasuredCellResult`) and never carries a verdict; it is not qualified, and the stock collector therefore grades both ids
inconclusive.

## Method

Twenty-third digest-checked collector extension `ntilde-owned-selective-erase-grading-v1` (derived over the erase-character / saved-lines one, which sits
over cursor, attribute-rectangle, erase-attributes/margins and saved-state/reset; it refuses derivation unless `parserStateResult`,
`selectiveEraseResult`, `unmeasuredCellResult` and both registrations, with their termless and query paths, are present verbatim once), contracts v1,
suite `ntilde-owned-selective-erase-graded-v1`, prefix `sels`, nonce `NTILDE_SE_`, 40x140. Every read is a parent-journaled `readScreen` (cells, stored
protection, attributes, buffer state) plus `readScrollback`; the pinned code runs over the journaled cells and the parent re-executes it. The pinned bytes
are replayed exactly (`pinnedFed` is recorded per case and compared with the expected feeds).

The contract adds, beyond the pinned check: protection stored on `P` only after the seed; exactly the protected cell (and, for DECSERA, `Z`) left after the
erase; unchanged cursor, modes and saved lines; stored protection preserved. Controls:

- ordinary erase of the same seed (ED 2 / DECERA `1;1;1;5`) removes the protected `P` and its protection: protection is not read-only text;
- no-op and whole-screen clearing: a 440-cell painted screen with 27 protected cells keeps exactly those 27 after a whole-screen DECSED or a whole-page
  DECSERA (`1;1;40;140`), cursor unmoved (also at the bottom-right cell); a DECSERA over an interior rectangle erases only unprotected cells inside it;
- bad or odd rectangle bounds (graded for DECSERA): interior `P___DZ`, oversized rectangle `P_____`, reversed rectangle erases nothing (`PABCDZ`); DECSED
  modes 0 and 1 are observed against ECMA-48, never graded;
- protection-off: turning DECSCA off affects only later writes (existing protection stays, the later cells are erasable); overwrite: an overwritten
  protected cell takes the pen and loses protection; the pinned finalizer `ESC[0"q` restores the pen so a later write is erasable;
- bold-attribute retention and background-color erase are observed against a reference cell, never graded; viewport erasure never removes saved lines.

Unsupported needs valid controls and an observed mismatch; a missing or invalid seed, lost `P`/`Z` without valid controls, a too-small screen or
incomplete restoration is inconclusive or refused. The contract is only ever stricter than the pinned probe; every difference is recorded with both
verdicts.

## Measurement

Scripts `32b776ff` (the committed tree, measured from the private captured source snapshot); the App source is that of `4a2c55ce` (the Release sidecar was
built from this checkout over merged main, which carries the protection foundation). Owned run `9cbce8ece66991681e2ccf76551b8881`, taken through the
shared desktop lock, receipt schema 36, source policy v30 (inventory v37, 231 files), default theme, 40x140, macOS, Node 24, disposable sidecar
`~/.ntilde-qa-sidecars/selective-erase`.

| Case | Pinned (parent replay + live pinned code) | Contract | Divergence |
|---|---|---|---|
| `erase.selective` | supported | supported | none |
| `editing.decsera` | supported | supported | none |

| Pinned fixture | Before | After | Cursor |
|---|---|---|---|
| DECSED `ESC[?2J` | `PABCDZ`, protection on `P` only | `P_____`, protection on `P` | (0,6) before and after |
| DECSERA `ESC[1;1;1;5${` | `PABCDZ`, protection on `P` only | `P____Z`, protection on `P` | (0,6) before and after |

Controls: the ordinary erase gives `____` (DECSED's ED 2 clears everything) and `_____Z` (DECERA clears `P` and keeps the outside `Z`), both with no stored
protection afterwards; DECSERA interior / oversized / reversed rectangles give `P___DZ`, `P_____`, `PABCDZ`; DECSED modes 0 and 1 give `PAB___` and
`P___DZ`; protection-off leaves `AB__` with columns 0,1 still protected; the overwrite control gives `_B`; the finalizer restores an erasable pen; the
full-screen controls keep exactly the 27 protected of 440 painted cells (a DECSERA over the interior rectangle leaves 300 painted, still 27 protected);
the saved lines (`HIST-A..C`, blank) are untouched on the main screen.

**Preserved attempts** (none is evidence of a verdict for the final tree): (1) run `50cfa7ab57d7eacedd0709ba3f53d344`, incomplete (app version 0.0.0, no
cell receipt, no retained selective-erasure artifacts), superseded; (2) run `c4c36393a2d1dcb62fe396829e1e930e`, failed, "Oversized owner response": the
observer response exceeded the generic limit for this read kind, fixed by adding `sels` to the 512 KiB request-kind list in `ownership.mjs`; (3) run
`49ec890f8e83c3975b6c62e3b016fe84`, complete on policy v26 (receipt 33), DECSERA supported but DECSED graded unsupported / `contract-stricter-than-pinned`,
superseded because the contract's blank-run check was too narrow for DECSED's whole-screen erase region (a contract defect, not an App defect), fixed
with a region-based attribute description; (4) run `f4d656b3a23f5f060d49d2f33a1bd1e3`, incomplete on policy v30 (no cell receipt, no retained
selective-erasure artifacts); (5) the final run above.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (74 unique ids, including the saved-state/reset and
attribute-rectangle suites with their preserved pinned-inconclusive verdicts beside their supported effect contracts); the separate 10/10 truecolor-literal
finding; the separate 3/3 App-observed underline-color resolution. **The stock collector was not re-run** (it needs an interactive Ntilde tab): the pinned
bundle and probeHash are unchanged and this batch changes no stock behavior, so the carried counts (168 inconclusive, 86 supported, 1 unsupported) are
labeled as carried; both ids are inconclusive there through the cursor-query path.

## Remaining inconclusive criteria

None for the two pinned cases. Not qualified: DECSEL (still an EL alias, a documented, test-pinned gap), ISO SPA/EPA, protection on wide or combining cells
beyond the VT unit tests, protection across resize/reflow/state transfer as a terminal-level measurement, rendered appearance of erased cells, and the
pinned pixel/query paths.

## Limits

Stored ASCII state only; no resolved-color, glyph or pixel claim; macOS owned sidecar, default theme.

## Evidence

[`2026-11-09-selective-erase/`](2026-11-09-selective-erase/): summary, report header and per-case reports, run log, common frames and one file per read
frame. `scripts/terminfo/selective-erase-publication.test.mjs` re-derives the report from the retained journal.
