# XTWINOPS title/icon stack qualification: push and pop (2026-11-11)

Qualifies two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `device.xtwinops-22` (push: save title/icon state) and
`device.xtwinops-23` (pop: restore it). The contract, the oracle and the evidence boundaries are in [title-stack.md](title-stack.md). The cases sit outside the frozen
Core catalogue, so **no Core percentage changes**, and no comparison with another terminal is made (PR 951 owns that). This qualifies **existing behavior (#830)**:
**no production code changed**; the only shared-code changes are opt-in extensions of the measurement tooling.

## Result

| Case | Pinned termless path (live code + parent replay) | Pinned terminal path (what the stock collector grades) | Effect contract | Experiments (reads) | Stock grade |
|---|---|---|---|---|---|
| `device.xtwinops-22` | **supported** | inconclusive by design | **supported** (limits **supported**, boundaries **supported**) | 7 (29) | inconclusive |
| `device.xtwinops-23` | **supported** | inconclusive by design | **supported** (boundaries **supported**) | 8 (29) | inconclusive |

The effect contracts are `ntilde-xtwinops-title-push-effect-v1` and `ntilde-xtwinops-title-pop-effect-v1`: separately identified, distinct fixtures, criteria and
verdicts, never attributed to the pinned probes. The pinned termless and effect verdicts agree, so no termless divergence exists; the terminal path's
`pinned-terminal-inconclusive-by-design` divergence is recorded for both cases. Fifty-eight reads were journaled as 61 paired frames (start, bind, 58 reads, finish);
none needed settling retries. The stock collector, re-run in the same owned run, still grades both ids inconclusive (167 inconclusive / 87 supported / 1 unsupported, as
before), so this qualification **does not raise the stock compatibility score**: the stock grade is the terminal path, which only asks whether the cursor answered.

## Pinned definitions

Both probes share one termless body: feed `OSC 2;pushed-title`, `getTitle()`, feed `CSI 22;0t`, feed `OSC 2;new-title`, `getTitle()`, feed `CSI 23;0t`, `getTitle()`,
and `parserStateResult(… ? true : null)`: supported when the three titles are `pushed-title`, `new-title`, `pushed-title`, otherwise inconclusive; it can never return
unsupported. Their terminal paths differ (push: the first three writes then a cursor query; pop: `CSI 22;0t`, `CSI 23;0t`, a cursor query) and only ever say "title stack
was not measured" / "restored title was not measured". The retained code (both paths) ran through a digest-checked derivation (`ntilde-owned-title-stack-grading-v1`) and was
replayed over the owned observations: the termless path read `pushed-title`, `new-title`, `pushed-title` from the raw title of the paired observation after each exact
stage (the raw icon name stayed at the experiment's baseline throughout); the terminal path answered after its own exact writes. The parent re-executed the termless
probes from the journal and agreed with the live results.

## What the App did (disposable direct-local pane, 40×140)

Every experiment starts from a stage that drains the stack with its own pops, then prepares a distinct run-bound title/icon pair whose last byte is an empty pop and reads
it back first (the values survived: the stack was empty by behavior). Reads are paired complete screen + pane-metadata + session-status captures; the raw title, the raw icon
name, the display title and the actual tab text were read separately every time, with the cells, cursor, cwd, links, input window, colors and history generation held to
the bound baseline. `t`/`i` below are the title and icon values of one experiment.

| Experiment | Observed (title/icon per read) |
|---|---|
| 22 `effect` | base `t0/i0`; **push alone** `t0/i0`; changed `t1/i1`; pop `t0/i0` |
| 22 `selectors` | base `t0/i0`; changed `t1/i1`; title-only push then pop: `t0/i1` (title back, icon keeps its changed value); icon-only push then pop after a new change `t3/i3`: `t3/i1` |
| 22 `nested` | two pushes with `t1/i1` and `t2/i2` current: pops return `t1/i1`, then `t0/i0` (LIFO) |
| 23 `effect` | base `t0/i0`; changed `t1/i1`; restored `t0/i0` |
| 23 `empty` | changed `t1/i1`; pops 0, 1 and 2 with nothing saved: `t1/i1` |
| 23 `selectors` | icon-only pop: `t1/i0`; the title-only pop that follows finds the entry consumed: `t1/i0`; after a new push and change `t3/i3`: title-only pop `t1/i3`, then the icon-only pop finds nothing: `t1/i3` |
| 23 `lifo` | pops return `t1/i1`, `t0/i0`, then a silent third pop `t0/i0` |
| 23 `mixed` | title-only push at `t0/i0`, change, icon-only push at `t1/i1` (its title half copied from the entry below), change to `t2/i2`: first pop `t0/i1`, second pop `t0/i1` (the documented one-sided copy rule) |
| 22 `overflow` (limit) | twelve pushes of distinct states, then `t12/i12`: pop 1 `t11/i11`, pop 10 `t2/i2` (the third-oldest: the two oldest were discarded), pop 11 `t2/i2` (silent) |
| 22 `alternate-screen` (boundary) | a push survives an alternate-screen cycle and a push made inside one survives leaving it; a pop made inside one restores: always `t0/i0` |
| 22 `decstr` (boundary) | DECSTR keeps the stack and retitles nothing: pop `t0/i0` |
| 23 `ris` (boundary) | RIS retitles nothing (`t1/i1`), clears the stack (the pop that follows restores nothing), blanks every cell, homes the cursor and raises the history generation 241 to 242 |

The depth of ten, the alternate-screen/DECSTR retention and the RIS clear are the documented #830 policy (xterm's `MAX_SAVED_TITLES`; xterm itself keeps the stack across
RIS, #830 specifies the clear); they are reported separately from the stack semantics. After DECSTR only the written cells were compared, and after RIS the cells must be blank
with the cursor home; every other read demanded the exact bound screen. The shared metadata validator gained an opt-in mode that accepts a raised history generation for
this suite only (a first attempt aborted on exactly that, see below).

Not measured, deliberately: direct-slot variants (`CSI 22;Ps;Pt t` with a nonzero third parameter), other selectors, window-title or icon report queries, host icon
presentation, and title sanitization.

## Identities

App 0.13.0 (operator supplied) built from `926199a6` (arm64 Release, executable SHA-256 `f5814e51…9bf8`, assembly `e7467b18…ba0c`); scripts at commit
`2e5879bc`; source policy v45 (300 files, manifest `f72cceec…cfea`); receipt schema 51; Node v24.22.2; owned run `931cd1b3…0fe3`; see
[summary](2026-11-11-title-stack/summary.json) for the full context, worker, pane, settings (`6a7cb2c0…0eaf`) and geometry (40×140) bindings; stock run
`a3d66791…52a1`, measured 2026-11-11T18:33Z on macOS 27.0.1. Predecessor suites were reconfirmed in the same run through the established runner: the receipt re-derived every
earlier layer, every earlier benchmark suite's cases are supported (or their effect contracts supported), the five Core layers are receipt-bound by digest, the query suite
reproduces its frozen 33 matched / 7 mismatched and the two findings reconfirm. Stock counts are re-collected in that run (not carried forward) and unchanged.

Attempts, all retained with their actual reasons: the first run (`555fe8b3…`, no receipt) aborted at the first read after RIS because the shared validator forbids any history
generation change; the second (`e21d4938…`) completed on uncommitted scripts; the third (`67ea8f7f…`, commit `3660e050`) completed but used all 64 captures of each observer,
so a Codex review (P2) asked for settling headroom and the plan was trimmed to 58 reads; the fourth (`f88778d5…`, commit `2a35a3e3`) was clean in a local Codex review but is superseded because the pull request's SonarCloud analysis reported eleven findings, several in measured files, whose fixes changed the measured scripts; the published run is the fifth. Archives are in `2026-11-11-title-stack/superseded/`.
A fresh run needs the shared desktop lock; this layer took policy v45 / receipt 51 concurrently with the OSC 7/8 layer, and whichever merges second restacks.

## Newly fixed versus newly qualified

Nothing was fixed: every observed behavior already held, and the oracle predicted all 58 reads of the real App before any adjustment of the App (the only adjustments were to the
measurement tooling). Newly qualified existing behavior: both pinned termless verdicts, both effect contracts, the depth policy and the alternate-screen, DECSTR and RIS boundaries.

## Evidence

[summary](2026-11-11-title-stack/summary.json), [run log](2026-11-11-title-stack/run-log.json), [journal-common](2026-11-11-title-stack/journal-common.json), one
[case file](2026-11-11-title-stack/) per id and one `journal-<case>-<experiment>.json` per experiment (its paired frames). Reproduce:

```bash
node --test scripts/terminfo/title-stack-publication.test.mjs
```

The test reassembles the journal, re-derives the report, the receipt digests and the pinned replay. A fresh measurement needs the shared desktop lock and
`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.13.0` on Node 24.

## Limits

Protocol handling and the App's stored raw title/icon state in a disposable direct-local App only. **No host icon presentation** follows from the raw icon-name readback, no
window-title or icon report query is qualified, and nothing is claimed for remote (SSH, mux) delivery, Windows or Linux, direct-slot addressing or another terminal.
Restoration is the prepared title and icon baseline (with an emptied stack), the initial cells and the original palette; the title and icon found at launch were an earlier
suite's residue (`SH-base-…` and the metadata suite's `MD-0-…`) and are neither restored nor claimed, nor is any stack content that existed before this disposable App.
