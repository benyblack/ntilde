# SGR attribute qualification — 2026-11-08

Five pinned `terminfo.dev@4.0.0` cases — `sgr.underline.single`, `sgr.inverse`,
`sgr.faint`, `sgr.hidden` and `sgr.strikethrough` — measured against a fresh
disposable macOS desktop instance, each **supported at the stored-state level**
by executing the pinned collector's own probe code over independently observed
cells, with the pinned result, an independent contract grader and a parent-side
re-derivation agreeing, beside a completed schema-16 cleanup receipt. The
nine earlier unique qualifications (ASCII text/bold/italic, SGR resets and
stored indices, resolved standard-color RGB) are reconfirmed in the same run
and their frozen publications still rederive.

This is a **local collector-extension measurement**, not stock CLI output. The
same instance's owned-adapter collector run keeps its recorded identity
(86 supported / 1 unsupported / 168 inconclusive, `suiteComplete=false`) and
grades all five cases inconclusive with `consumed` evidence ("Cursor advance
does not verify SGR styling"). No combined stock score is manufactured.

## Pinned definitions and naming

All five are built by the pinned `sgrProbe(id, sequence, check)`: the termless
probe feeds `<sequence>X` and reads cell (0,0). Exact IDs and fixtures match
the pin; no related variant is substituted.

| Case | Pinned fixture | Pinned check | Contract refinement |
|---|---|---|---|
| `sgr.underline.single` | `\x1b[4mX` | `!!cell.underline` (any style) | stored style must be exactly **single** (SGR 4, not `4:n`/SGR 21) |
| `sgr.inverse` | `\x1b[7mX` | `cell.inverse === true` | DECSCNM proven off; per-cell flag only |
| `sgr.faint` | `\x1b[2mX` | `cell.dim === true` | faint set and **bold clear**; SGR 22 pair as a control |
| `sgr.hidden` | `\x1b[8mX` | `cell.hidden === true` | stored `X` text must still be present |
| `sgr.strikethrough` | `\x1b[9mX` | `cell.strikethrough === true` | none |

The pinned probes never grade unsupported (a failed check is "a negative needs
rendering review"). The contract grader may grade unsupported only after valid
controls show an observed mismatch, never a missing or invalid control; it may
be stricter than the pinned probe (recorded as a `divergence`) but never more
permissive. No divergence occurred in this run.

## What each conclusion establishes

Measured with 40 × 140 geometry, theme `Default`, DECSCNM off. Flags are the
App's stored `TerminalCellFlags` (default-color bits `0x0300` included).

| Case | Observed target cell `X` | Controls | Conclusion |
|---|---|---|---|
| `sgr.underline.single` | flags 776, stored underline style **single (1)** | baseline `C` neutral (flags 768, no underline); no other attribute or color drift; `SGR 24` clears `Y` while `X` keeps its state | **supported** (stored flag + single style) |
| `sgr.inverse` | flags 770, inverse set | neutral baseline; no drift; DECSCNM off in every frame; `SGR 27` clears `Y` | **supported** (stored per-cell flag) |
| `sgr.faint` | flags 832, faint set, **bold false** | neutral baseline; no drift; `Y` (`SGR 1` over faint) holds bold+faint, `Z` after `SGR 22` holds neither | **supported** (stored faint, distinct from bold) |
| `sgr.hidden` | flags 896, conceal set, `X` text stored | neutral baseline; no drift; `SGR 28` clears `Y` | **supported** (stored conceal flag with text present) |
| `sgr.strikethrough` | flags 784, strikethrough set | neutral baseline; no drift; `SGR 29` clears `Y` | **supported** (stored flag) |

Each claim is the **stored state** only. Rendered appearance — underline
stroke, inverse swap, dimming, glyph suppression for conceal, strike line — is
**not claimed**: no pinned criterion requires it, no pixel or resolved-color
evidence was used as proof, and resolved colors were read only to bind
DECSCNM off and to keep the inverse flag distinct from global reverse video.
Missing text, missing controls, drift, or reverse video each leave a case
inconclusive; none can yield a conclusive grade.

## Remaining inconclusive criteria

None among the five. Open, outside this batch: rendered appearance of all five
attributes (would need a controlled-pixel suite per attribute, with the
measured font/theme limits) and the stock collector's own `consumed` grades.

## Collector extension identity

| Measure | Value |
|---|---|
| Layered adapter | `ntilde-owned-sgr-attributes-grading-v1` (contracts v1), suite `ntilde-owned-sgr-attributes-graded-v1` |
| Derived from | `ntilde-owned-sgr-rgb-grading-v1` → `ntilde-owned-sgr-colors-grading-v1` → `ntilde-owned-ascii-sgr-grading-v1` → `ntilde-owned-macos-v1` over pinned `terminfo.dev@4.0.0` |
| Pinned bundle SHA-256 | `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6` |
| Layered bundle SHA-256 | `acd7680f33071aba942cfaac3a4ced4f265d8f56c8e0210fdd9d970483bd106d` |
| Receipt schema / source policy | 16 / `readonly-owned-source-graph-v10` (inventory v17, 128 files); policies v1–v9 keep their frozen maps |

## Measurement

| Measure | Value |
|---|---|
| Run / receipt completed | run `af3916def9dfe3f9bbcaa6dee5c8230f`, receipt 2026-11-08T01:02:10.082Z |
| App | `0.13.0`; **App source identical to main `499a0246` (#888)** — `git diff 499a0246 HEAD -- src` is empty; sidecar built from this branch |
| Adapter/scripts source | commit `a7a0ef93` (pinned byte-for-byte by the run's policy-v10 inventory), after the Codex-review fix; the sidecar App was built earlier from an App-identical tree |
| Same-run predecessors | grading 4/4, SGR-colors 5/5 (contracts v2), SGR-RGB 2/2 supported |
| Local evidence | `artifacts/terminfo/owned-rwCXsg` (digests in the [JSON](2026-11-08-sgr-attributes-qualification.json)) |

## Preserved attempts

1. The first sidecar build failed (Ntilde.Cli not restored); a solution restore fixed it.
2. `owned-QZc8ZG`: the app aborted before collection (Avalonia render timer, display asleep).
3. `owned-FBpYqE`: collection stopped with "Unknown owner request" because the runner allowlist did not admit the new `sgattr` journal kinds. Fixed in `125a8c59` with a regression test.
4. `owned-SNe7Jd` (run `e41cfd493c53dcfff35fa4ef978448ea`): a **complete** measurement (5/5 supported) from `125a8c59`, superseded. Codex review found that contradictory attribute-run and cell-detail projections were not refused (a target with underline off in one and single underline in the other graded supported). The fix changes measured source, so the measurement was rerun; the earlier run is retained unmodified.
5. `owned-vhxDkz`: an EPERM before launch, produced by the review sandbox running the script tests; no app launched.

## Reproduce

```sh
node --test scripts/terminfo/sgr-attrs-*.test.mjs
```

The publication test re-derives the whole report from the committed journal and
run log; the receipt test re-derives the five-layer adapter chain and rejects
tampered bytes, identities, counts, predecessors and downgrades.
