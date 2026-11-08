# SGR underline-style qualification (2026-11-08)

Qualifies exactly four terminfo.dev@4.0.0 cases through independently observed stored state:
`sgr.underline.double`, `sgr.underline.curly`, `sgr.underline.dotted` and `sgr.underline.dashed`.
All four are **supported** on the stored layer; rendered stroke appearance is **not claimed**.

## Verified pins (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`)

Each case is built by the shared `sgrProbe` helper, which feeds `<sequence>X` and reads cell (0,0):

| Case | Spelling | Pinned check |
|---|---|---|
| `sgr.underline.double` | SGR 21 (`ESC[21m`) | `cell.underline === "double"` |
| `sgr.underline.curly` | SGR 4:3 (`ESC[4:3m`) | `cell.underline === "curly"` |
| `sgr.underline.dotted` | SGR 4:4 (`ESC[4:4m`) | `cell.underline === "dotted"` |
| `sgr.underline.dashed` | SGR 4:5 (`ESC[4:5m`) | `cell.underline === "dashed"` |

- **No pinned criterion accepts any underline.** Unlike `sgr.underline.single` (`!!cell.underline`, qualified
  earlier with an added exact-single refinement), each check already names one exact style. The contract's
  exact-style requirement therefore coincides with the pinned check: there is no semantic difference to record,
  and no verdict was substituted.
- **SGR 21 and SGR 4:2 are distinct spellings.** Only SGR 21 is graded for double. SGR 4:2, SGR 4 and SGR 4:1 are
  labeled controls that receive no benchmark grade.
- The pinned probes never return unsupported: a failed check is the pinned inconclusive ("a negative needs
  rendering review"). The stock run leaves all four inconclusive ("Cursor advance does not verify SGR styling").

## Contracts and extension identity

- Contracts `ntilde-owned-sgr-underline-styles-graded-v1`, version 1; extension `ntilde-owned-sgr-underline-grading-v1`
  (eighth layer, derived from the SGR-truecolor one by digest-checked append-only derivation; seams pin all four
  registrations, their spellings and exact-style checks plus the `sgrProbe` body and notes).
- Receipt schema 20, source policy v14 (inventory v21, 149 files). Policies v1–v13 and their publications stay frozen.
- Plan per case: neutral baseline `C`; the exact pinned fixture `<sequence>X`; SGR 24 then `Y` (X must keep its exact
  stored tuple, Y must return to neutral); then a labeled ladder of every spelling (SGR 4, 4:1, 21, 4:2, 4:3, 4:4, 4:5)
  each followed by SGR 0, and a neutral cell. Each read is journaled by the parent observer.
- Observed per cell: the attribute-run underline flag, the stored cell-detail flags, the exact stored style code
  (0–5), underline color index/ARGB, overline, foreground/background storage and resolved colors. The attribute-run
  flags, detail flags and style are cross-checked; a flag/style contradiction, undefined style code, or underline
  color without a style invalidates the evidence outright.
- Grading: inconclusive on a missing X, DECSCNM not proven off, a non-neutral baseline, any unrelated attribute,
  color, overline or underline-color drift on the target, failed SGR 24 restoration, or a malformed ladder (plain SGR 4
  must store single). Supported = exact stored style name and code. Unsupported needs all controls valid, an observed
  mismatch and a ladder that shows at least two distinct stored styles (otherwise a collapsed readback is
  inconclusive). The contract is never more permissive than the pinned probe; differences are recorded as divergences
  (none occurred).

## Measurement

Fresh committed build: scripts and App source at `7f622464` (the App source is unchanged from main `72d0bff6`;
the change is evaluation tooling only, so the App and adapter revisions coincide). Disposable sidecar, owned run
`owned-RtWkOy`, run id `45c6010b8d8153b3495361a87cc76295`, receipt completed 2026-11-08T16:04:48Z.

| Case | Pinned verdict | Exact-style contract verdict | Stored |
|---|---|---|---|
| `sgr.underline.double` | supported | supported | style 2, flag set |
| `sgr.underline.curly` | supported | supported | style 3, flag set |
| `sgr.underline.dotted` | supported | supported | style 4, flag set |
| `sgr.underline.dashed` | supported | supported | style 5, flag set |

The ladder showed SGR 4 and 4:1 → single, SGR 21 and 4:2 → double, 4:3 → curly, 4:4 → dotted, 4:5 → dashed.

The same run reconfirmed the prior 22 qualifications (colors 5, RGB 2, attributes 5, palette 6, truecolor 2,
ASCII/SGR 4; 22 unique ids) and the separate truecolor-literal finding (10/10 supported, its own ids, never counted
as benchmark qualifications). Every report and the completed receipt rederive from retained bytes. The stock collector
is unchanged: 86 supported, 1 unsupported, 168 inconclusive. Stock grades and qualifications are never combined.

## Evidence

[summary](2026-11-08-sgr-underline/summary.json), [report](2026-11-08-sgr-underline/report.json),
[run log](2026-11-08-sgr-underline/run-log.json) and one journal per case under `2026-11-08-sgr-underline/`.
`sgr-underline-publication.test.mjs` reassembles and rederives them. Two preparation failures (a missing
`Ntilde.Cli` restore in the fresh worktree and an unprepared collector cache) created no run directory and are
recorded in the summary.

## Limits

Stored state only: stroke geometry (wave, dot and dash pattern), thickness, underline color, anti-aliasing and
rendering are unmeasured. Only the pinned spellings are qualified; SGR 4:2 and the other ladder spellings are
controls. Underline color and reset behavior are outside this batch. Default theme only.
