# Follow-up to PR #959: reflow inventory admission, inherited review and duplication (2026-11-14)

[PR #959](https://github.com/benyblack/ntilde/pull/959) merged as `cd8ab1ec` (final head `0f9ce55f`) under an explicit exception: its final head had no completed
Codex or CodeRabbit review and SonarCloud's gate failed on duplication. This follow-up closes that remaining work. It measures nothing new, changes no
production code and does not alter any retained evidence, grade or historical report. The original merge was **not** fully reviewed or fully green.

## 1. Why `extensions.reflow` was missing from the inventory

The inventory is derived from an explicit registry (`benchmarkSuites` in `scripts/terminfo/qualification-inventory.mjs`); a suite exists in it only if someone adds a
row. #959 shipped its publication, publication test and doc but never added the `ascii-reflow` row, and nothing failed because no check compared the registry with
the publications that exist. The totals at that merge (108 ids / 110 claims) were the unchanged post-#960 numbers.

Fixed in two parts:

- `ascii-reflow` is registered (`2026-11-12-ascii-reflow/`, contracts v1, `ascii-reflow-publication.test.mjs` as its trusted rederivation).
- A completeness guard: every `*publication*.test.mjs` under `scripts/terminfo` must be a registered suite/finding or appear in `nonInventoryPublicationTests`
  with its reason (frozen pre-inventory datasets, Core requirement results, the common-harness comparison, a projection unit test). A future graded publication
  that is not registered now fails `qualification-inventory.test.mjs`.

## 2. How an unsupported effect result is represented

`extensions.reflow` keeps two separate verdicts, exactly as retained: **pinned = inconclusive** (the pinned probe has no fixture and can never decide;
recorded divergence `pinned-inconclusive-by-design`) and **effect = unsupported** (width round trip model-exact; the history round trip's shrunk stage displaced
38 rows against the model's 44, hard-line pairs merging with inserted layout blanks, no observed text loss; restored stage model-exact). The stock grade stays
inconclusive.

Totals now derived from main (previously 108 ids / 110 claims / 108 effect-supported):

| Measure | Before | After |
|---|---:|---:|
| Unique benchmark ids with qualified retained effect evidence (any decided verdict) | 108 | **109** |
| Suite claims | 110 | **111** |
| Effect contract supported | 108 | 108 |
| Effect contract unsupported | (not tracked) | **1** (`extensions.reflow`) |
| Pinned-supported / pinned+effect supported | 85 / 85 | 85 / 85 |
| Effect supported, pinned inconclusive | 23 | 23 |
| Effect unsupported, pinned inconclusive | (not tracked) | **1** |
| Not effect-supported, `effectUnqualified` (168 − 107) | 61 | 61 |
| of which measured, effect unsupported | (conflated) | **1** |
| of which measured, another non-supported verdict | (conflated) | 0 |
| of which not measured (no qualified evidence) | (conflated) | **60** |
| Not pinned-supported | 84 | 84 |

No effect-supported, pinned-supported, stock, Core or common-harness measure moves. `effectUnqualified` keeps its original meaning (stock-inconclusive ids without
a supported effect verdict) and is now split into exactly three states that must not be conflated: *measured and unsupported* (retained, rederived evidence that
the behavior is absent or diverges), *measured with another non-supported verdict*, and *not measured*. Only the last means "not yet qualified". The inventory's
identity tests require the three to sum to `effectUnqualified` and every qualified id to have exactly one effect state.

## 3. Inherited review

Independent Codex reviews (`codex review`, a separate process from the implementer, not an implementer self-check):

| Review | Reviewed SHAs | Result |
|---|---|---|
| Inherited changes: receipt-shape fix, `TypeError` change, measured-revision constants, republished evidence | base `5ec4fa3a` .. head `0f9ce55f` (checked out detached so the already-merged code is the reviewed diff) | "did not reveal any actionable correctness issues"; Codex re-ran `ascii-reflow-receipt` and `ascii-reflow-publication` tests |
| This follow-up | final-head review recorded on the PR | see the PR conversation |

The inherited range contains: `checkUnavailableReflow` now requires marker `schemaVersion === 1` and the binding's `provisionalUntil === 'cell-receipt.json'`
(covered by the added receipt test), `gradeReflowCase` throws `TypeError` for a missing capture, the publication's measured-revision constants and pinned manifest
digest, the republished `report.json`/`summary.json`/`owned-run.zip` and two more retained attempts (14 and 15).

## 4. SonarCloud duplication on #959

Sonar reported 6.4% duplication on new code (131 duplicated lines in 2044 new lines, 5 blocks) against a 3% threshold, with zero new-code issues. The five blocks
were all copies of the existing resize machinery, in two files:

| #959 file and lines | Duplicate of | Resolution |
|---|---|---|
| `ascii-reflow-grading.mjs` 32–43 | `resize.mjs` 19–30 (`validateAction`) | `validateAction` is exported from `resize.mjs`; the copy is deleted |
| `ascii-reflow-grading.mjs` 91–105 | `resize.mjs` 45–59 (per-frame journal loop) | extracted as `validateJournalFrames` in `resize.mjs`, called by both; `frameKind` shared |
| `ascii-reflow-observer.mjs` 16–49, 51–80, 82–121 | `resize-capture.mjs` 16–49, 52–81, 83–120 (whole observer class) | `OwnedReflowObserver` is now a subclass of `OwnedResizeObserver`, overriding only the label, request-kind prefix, nonce prefix and case builder |

No threshold, exclusion or suppression was used, and no new measured input file was added (the shared code lives in two files already in the measured
inventory, so the v52/v59 source inventory is unchanged). The reflow suite's own refusal text and `reflow-`/`NTILDE_RF_` identities are preserved; the
shared validators take the caller's refusal (`ensure`) so reflow errors still read "ASCII reflow".

Behavior equivalence was checked, not assumed: (a) 128 single-field mutations of the synthetic reflow journal (every frame field, every action field, truncation)
give byte-identical reports or identical refusal messages from the pre- and post-refactor grader; (b) the old and new observers, driven through the full
start/bind/read/apply/finish sequence plus invalid requests, retain identical frames, actions, plans and RPC call logs for both the reflow and resize suites;
(c) the existing observer, grading, receipt, collector and resize tests pass, plus new tests for the cross-suite nonce/request-kind boundaries.

A green Sonar gate on this follow-up PR is not by itself evidence that #959's debt is resolved; the per-file duplication of the two files on `main` after the
merge is the applicable result and is recorded on the PR.

## 5. Evidence and remeasurement

No fresh measurement was needed or taken. The retained run `e8d8ebdb956ba201d2924f76dcda48c7` (measured scripts and App at `d8679e11`, source policy v52,
captured inventory v59, receipt schema 58) is unchanged and still rederives: `node --test scripts/terminfo/ascii-reflow-publication.test.mjs` rebuilds the
complete receipt and the projection byte for byte from its archived sources and journals, and all attempt archives keep their pinned digests.

The deduplication does change the bytes of four files in the measured input graph (`resize.mjs`, `resize-capture.mjs`, `ascii-reflow-grading.mjs`,
`ascii-reflow-observer.mjs`), but not their behavior (above). The retained run therefore remains the measurement of the scripts at `d8679e11`, which its
archive contains; it is not re-attributed to the refactored bytes. Any future collection binds the new hashes. This is a limitation: the evidence is not a
measurement of the exact post-refactor bytes, only of behaviorally identical ones.

The terminal's reflow layout mismatch (the history round trip diverging in the live pane-resize path) is untouched, and so is the independent oracle.

## Reproduce

    node scripts/terminfo-inventory.mjs --check --rederive
    node --test scripts/terminfo/qualification-inventory.test.mjs scripts/terminfo/ascii-reflow-publication.test.mjs
