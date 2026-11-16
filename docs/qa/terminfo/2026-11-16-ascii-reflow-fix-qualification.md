# ASCII history reflow fix (#979)

The historical [2026-11-12 qualification](2026-11-12-ascii-reflow-qualification.md)
remains unchanged: its effect verdict is unsupported, and its pinned and stock
verdicts are inconclusive. This follow-up retains a fresh baseline and a separately
identified fixed-App measurement of the same `ntilde-ascii-reflow-effect-v1`
contract. No fixture, model, grading criterion, geometry target or deadline is
relaxed.

## Defect and ownership

The preceding width experiment creates soft wraps on rows 0, 3, 6, 9, 12 and 15.
The next experiment erases the screen and writes new positioned hard lines.
`TerminalBuffer.ClearRowInternal` erased cells and cleared `WrappedColumns`, but
left `IsWrapped` true. The initial cell projection therefore matched the model
while retaining six invisible stale soft-wrap boundaries. Shrinking rejoined
exactly those six neighboring hard-line pairs, including two unused layout cells
per pair, producing 38 displaced rows instead of 44.

The defect belongs to VT row erasure. It reproduces without the UI when the
**complete ordered fixture sequence** is replayed; the earlier history-only VT
reproductions omitted the preceding width experiment and therefore passed. The
live App/PTY resize path exposed this state; no App, PTY, parser, renderer or
reflow-algorithm adjustment is needed. The fix clears `IsWrapped` in the existing
complete-row erase helper; its setter also clears the optional wrap extent.
Erasure keeps row objects/IDs in place, preserves the cursor and pen, and leaves
scrollback intact. EL 2 preserves other rows' wrap identity.

Owning tests pin both fixture byte hashes from `makeReflowCases(40, 140)` and
compare independently sliced logical lines, hard/soft identity, every cell,
complete history, width-fixture attributes, cursor mapping and restoration.
They exercise the history fixture alone and after the width round trip, including
single-character and 37-character parser chunks. Before the fix, the ordered
fixture reproduced the historical 38-versus-44 failure; ED 2 and EL 2 separately
failed the wrap-retirement regression. After the fix these checks pass.

## Live measurements

| Measurement | Effect | History at 36 × 133 | Pinned | Stock |
|---|---|---:|---|---|
| Historical run `e8d8ebdb956ba201d2924f76dcda48c7` | unsupported | 38 | inconclusive | inconclusive |
| Fresh baseline `2b00a54a9b4774e4828d0cd5810cb034` | unsupported | 38 | inconclusive | inconclusive |
| Fixed-App run `7a3c3f1779fb10cfd83ff5f0b8c70c39` | **supported** | **44** | inconclusive | inconclusive |

The fresh baseline used App/scripts production base
`9c692a04aa880372a9461be51e4144e307ab5c83`, source policy v61 and receipt schema 67.
Both width transitions matched the model, including the bold/underline probe and
cursor [1,46] → [1,53] → [1,46]. The history shrink had cursor [35,5] with 38
history rows; restoration matched at 22 history rows and cursor [17,138].

Both live measurements use the shared desktop lease and disposable direct-local
pane. The parent selects each resize and independently verifies actual settled
App geometry, kernel PTY geometry and `-opost`; DA fences precede paired reads.
Each experiment is seeded once, with no reconstruction writes across resizes.
The complete receipts require source/input bindings, unaffected-state controls,
restoration, App/worker cleanup and removal of the private fixture/source tree.
The fixed retest uses the existing `--scope osc633` selection from #975: it
measures reflow unchanged while omitting `osc9` and `notify`. Their separate pinned
predecessor receipt is rederived and retained by that selection. This is combined
evidence, not a fresh complete default run; no new notification or OSC 633 claim
is added by this follow-up.

The fixed run uses scripts `1955928d36d0d6fdf4f7bbe43088aae6d8414a74`, source
policy v62 and receipt schema 69. All six fixture stages match the independent
model. The history shrink has 44 ordered history rows and cursor [35,5];
restoration retains 22 history rows, bottom padding and cursor [17,138]. The
width probe attributes and cursor movement remain model-exact. The same seven
reflow contract, grader, collector and resize-observer inputs are byte-identical
in the fresh baseline and fixed archives, explicitly checked by the manifest.
The stock collector remains 93 supported / 1 unsupported / 161 inconclusive;
`extensions.reflow` itself stays inconclusive.

The fixed App was built from
`cbc70bde25249b39365994477b43fd39b98198a5`. Its source snapshots and the baseline
are retained in `production-sources.zip`; `build-identities.json` names the build
revisions and exact executable, App assembly and VT assembly hashes. These are
retained build/source identities, not reproducible-build attestation.

An initial current-baseline attempt hit the unchanged five-minute collection
deadline before reflow; it carries no reflow verdict. Its full failed evidence is
retained as `attempts/attempt-1-timeout.zip`. Cleanup and desktop lease release
were verified. The unchanged retry completed with the baseline result above.
The first fixed-App attempt then aborted on bytes received outside an expected
terminal response (`attempts/attempt-3-unexpected-input.zip`); the record does not
establish their origin. An unchanged fixed-App retry reached the five-minute
deadline before reflow (`attempts/attempt-4-timeout.zip`). Both retain their full
evidence, verified cleanup and no reflow verdict. The final bounded selection
omits the two unrelated slow notification suites using the already established
selection/predecessor contract; it changes no reflow criterion or limit.

## Evidence and reproduction

Evidence: [manifest](2026-11-16-ascii-reflow-fix/manifest.json),
[current report](2026-11-16-ascii-reflow-fix/report.json),
[current summary](2026-11-16-ascii-reflow-fix/summary.json),
[fresh baseline archive](2026-11-16-ascii-reflow-fix/before-fix.zip),
[fixed-App archive](2026-11-16-ascii-reflow-fix/owned-run.zip),
[build identities](2026-11-16-ascii-reflow-fix/build-identities.json).

```sh
node --test scripts/terminfo/ascii-reflow-publication.test.mjs \
  scripts/terminfo/ascii-reflow-fix-publication.test.mjs
node scripts/terminfo-inventory.mjs --check --rederive
```

The publication checks independently reissue each complete receipt, rederive the
reflow report from retained paired journals/actions and replayed pinned code, and
compare the published projections. The historical unsupported evidence remains
separately tested. The live inventory selects the new measurement without adding
a benchmark ID or rewriting stock, Core or common-harness results. The active
inventory keeps 136 unique IDs and 138 claims, moves one effect verdict from
unsupported to supported (132 supported, four unsupported OSC 633 IDs), and leaves
the pinned-supported count at 93. The frozen stock-inconclusive remainder is
37 not effect-supported: four measured unsupported and 33 unmeasured.

Limits remain those of the original bounded ASCII contract: stored main-screen
state in a disposable local macOS pane. No Unicode/wide/grapheme reflow,
alternate-screen expansion, history attributes, rendered pixels, mux/remote or
other-platform qualification is inferred. #977 repairs real-bundle validation;
it does not add terminal support claims.

Validation: 3,537 VT tests; App gating 8,619 passed / 58 skipped; separate
PlatformBoot 187 passed / four skipped; Replay 76 passed; Architecture 191 passed.
Skips are platform-specific or opt-in cases. No timeout was increased.
