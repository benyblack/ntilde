# XTWINOPS title/icon stack benchmark layer (device.xtwinops-22, device.xtwinops-23)

This is the contract behind the [2026-11-11 title/icon stack qualification](2026-11-11-title-stack-qualification.md). It qualifies two pinned terminfo.dev@4.0.0
cases through the App's existing read-only pane-metadata and session-status observations ([owned-metadata.md](owned-metadata.md),
[owned-pane-metadata-api.md](owned-pane-metadata-api.md)). It adds no production behavior and no production API: it newly qualifies the implementation of
[#830](osc-title-payloads.md#xtwinops-title-stack-830). It reuses the paired observer of the frozen metadata suite under its own `tst` prefix and plan, with two
small opt-in extensions described below. It adds no Core requirement and makes no comparison with other terminals (the common-harness comparison is PR 951's).

## What the pinned probes actually are

Read from `terminfo.dev@4.0.0` (`terminfo.bundle.mjs`, SHA-256 `aa5f2bf9…d1f6`), not inferred from the names. Both cases register the **same termless body** and
differ on the terminal path:

| Path | `device.xtwinops-22` | `device.xtwinops-23` |
|---|---|---|
| termless | `feed(OSC 2;pushed-title)`, `getTitle()`, `feed(CSI 22;0t)`, `feed(OSC 2;new-title)`, `getTitle()`, `feed(CSI 23;0t)`, `getTitle()`; `parserStateResult(original === "pushed-title" && changed === "new-title" && restored === "pushed-title" ? true : null, …)` | the identical body; only the criterion text differs ("restores the title saved before a different title was set") |
| terminal | `write(OSC 2;pushed-title)`, `write(CSI 22;0t)`, `write(OSC 2;new-title)`, then `DSR`: "Cursor answered after title push; title stack was not measured" | `write(CSI 22;0t)`, `write(CSI 23;0t)`, then `DSR`: "Cursor answered after title pop; restored title was not measured" |

Two consequences, both measured rather than assumed:

- The **termless path** returns `supported` when the three titles are as expected and `inconclusive` otherwise ("Title setup was not observable" or "Title push/pop
  did not restore the old title; the failing operation is unknown"). It can never return `unsupported`. Its verdict needs `getTitle()` after each stage, which the stock
  collector does not serve.
- The **terminal path** only asks whether the cursor answered. It is always inconclusive (`evidence: consumed`) and **is the path the stock collector grades**: the
  retained stock run grades both ids inconclusive with exactly that note, which is why both were stock-inconclusive. Carried-forward stock counts are not changed.

The retained code (termless **and** terminal functions) runs through a digest-checked derivation (`ntilde-owned-title-stack-grading-v1`, layered over the
left/right-margin one like its sibling layers). It refuses unless the retained bundle still contains both registrations verbatim and once, then only appends exports.
The worker runs both paths over the journaled stock-context reads: the termless path calls `getTitle()` after the exact pinned feeds and receives the raw title of the
paired observation that followed those feeds; the terminal path receives the cursor of the read that followed its own exact writes. The push case's three terminal
writes are the first three feeds of its stock experiment (the read after them is its `pinned-changed` read); the pop case's two writes run in their own
`terminal-path` experiment, which is read once right after them. The parent re-executes the termless probes from the retained journal and the independent mirror
(`replayTitleStackTermless`/`replayTitleStackTerminal`) must agree with the live results.

The pinned verdict of each case is therefore the **termless result over the owned observations** (supported when all three titles were observed), reported beside the
**terminal-path verdict (inconclusive by design)** and the unchanged stock grade. They are different paths of one probe, and the report keeps them apart:
`conclusion.outcome` is the termless verdict, `pinned.terminal` is the terminal path, and a `divergences` list records `pinned-terminal-inconclusive-by-design` for
both cases (plus `pinned-termless-vs-effect` whenever the termless verdict and the effect verdict differ).

## The contract the effect experiments grade

From [osc-title-payloads.md](osc-title-payloads.md) and xterm's `xtermPushTitle`/`xtermPopTitle` (`misc.c`), implemented independently in the oracle
(`title-stack-contracts.mjs`, which never calls the production implementation):

- `Ps` 0 saves or restores both the window title and the icon name, 1 the icon name, 2 the window title.
- A push copies nothing visible: it saves the current values and changes none. A one-sided push takes the other half from the entry below (the pop-time "try higher
  levels" lookup); with nothing below, that half is absent and restoring it changes nothing.
- A pop removes the whole top entry whatever it restores, and restores only the selected, present halves. A pop on an empty stack is silent.
- The stack holds ten entries (`MAX_SAVED_TITLES`): a push onto a full stack discards the oldest entry, and pops beyond the retained ten are silent. This is a
  deliberate xterm-compatible implementation limit, labelled and reported separately.
- Alternate-screen switches and DECSTR keep the stack; RIS clears it (xterm keeps it; #830 specifies the clear) and does not retitle. These are supplemental boundary
  observations, never the only way a positive control is established.
- Only the bare spelling with at most three parameters is accepted. Nonzero direct-slot addressing and other selectors are **not** qualified and are not measured.

Three things are kept distinct everywhere: the **raw title** (`oscTitle`), the **raw icon name** (`buffer.requestedIconName`, state only: **no host icon
presentation is claimed**) and the **displayed caption** (`displayTitle` and the actual `tabText`). The oracle predicts the display title equal to a nonblank raw title
and the tab text containing it (the frozen metadata suite's own expectation); a stale caption behind a right raw title fails a read, a right caption never hides a
wrong raw icon name, and no window-title or icon report query (`CSI 21 t`) is qualified.

## Experiments

Fifteen experiments, 58 reads, 61 paired frames (start, bind, 58 reads, finish), inside the observer's 64-capture ceiling with three captures spare for settling retries of a transiently disagreeing pair (the first plan used all 64 and a Codex review rightly called that a fragility). **Every experiment** starts from the same stage
(a reset, twelve pops that drain the stack, the prepared title and icon, a cwd and a run nonce on row 9), then writes a distinct run-bound baseline (`<run>-<tag>-t0`,
`-i0`) whose last byte is an empty pop, and reads it back first: the `base` read proves the prepared values survived a pop, which is the stack-empty control. Strings are
bounded and unique per experiment and run, so a stale observation from another experiment or run can never match.

| Owner | Experiment | Role | Reads | What it establishes |
|---|---|---|---|---|
| 22 | `stock` | stock | 4 | the exact pinned termless fixture (and, for the terminal path, its three writes), read after each pinned stage |
| 23 | `stock` | stock | 4 | the same bytes as an independent instance for this case |
| 23 | `terminal-path` | terminal | 1 | the exact pinned `CSI 22;0t` + `CSI 23;0t` writes, read once |
| 22 | `effect` | effect | 4 | combined: base, push alone leaves both values, change both, pop restores both |
| 22 | `selectors` | required | 4 | title-only push then combined pop (title returns, icon keeps its changed value) and icon-only push (icon returns, title keeps its value) |
| 22 | `nested` | required | 4 | two distinct saved states with independently measured intermediate states, popped in LIFO order |
| 22 | `overflow` | limit | 5 | twelve pushes of distinct states: the first pop returns the newest, the tenth the third-oldest, the eleventh finds nothing |
| 23 | `effect` | effect | 3 | combined restore |
| 23 | `empty` | required | 3 | pops (`0`, `1`, `2`) with nothing saved leave measured changed values alone |
| 23 | `selectors` | required | 6 | icon-only and title-only pops restore only the selected half and consume the entry (a second pop finds nothing), for each half |
| 23 | `lifo` | required | 5 | combined nested pops restore LIFO, a third pop is silent |
| 23 | `mixed` | required | 4 | title-only then icon-only pushes: the one-sided copy rule restores `t0`/`i1`, not an assumed model |
| 22 | `alternate-screen` | boundary | 5 | a push survives an alternate-screen cycle, a push made inside one survives leaving it, a pop made inside one restores |
| 22 | `decstr` | boundary | 3 | DECSTR keeps the stack and retitles nothing |
| 23 | `ris` | boundary | 3 | RIS clears the stack (the pop that follows restores nothing), retitles nothing, clears the cells and homes the cursor |

A read is `setup` (prepared baselines and observed mutations: every one re-synchronizes both halves, so a failed restoration never contaminates a later setup read) or
`effect` (what a push or pop produced). A case's effect contract is **supported** when every setup read held and every effect read of its stock, effect and required
experiments matches the oracle; **unsupported** when the setup reads held and an effect read mismatches; **inconclusive** when a setup read (a baseline, an
observation of the mutation) failed. A stable wrong state is evidence: nothing waits for an expected state to appear. The overflow experiment (22) is graded as
`effectContract.limits` and the boundary experiments as `effectContract.boundaries`, each with its own outcome and without changing the stack-semantics verdict.

Cells, cursor, cwd, hyperlinks, input-window state, runtime colors and the history generation must be exactly the bound baseline's in every strict read, so a correct
restored string cannot hide an unrelated change. After DECSTR only the written cells are compared (DECSTR's cursor effect is not part of this contract); after RIS the
cells must be blank and the cursor at home, and the history generation may rise (RIS clears history) but never fall. That is the one reason the shared metadata
validator gained an opt-in `monotonicHistory` mode for this suite: every other suite keeps the exact-generation rule.

## Cleanup and what is restored

Restoration is exactly what the observation API can show. The stage's pops and the finish write (a reset, the drain, the prepared title and icon, the withdrawn runtime
palette override and one more pop) restore the **prepared** title and icon baseline, and the finish frame must show the prepared values (the final pop on an empty stack
left them alone) and the exact initial screen. The original hidden title and icon (a null `oscTitle` at launch) are not observable and are **not claimed restored**, nor
is any stack content that existed before this disposable App; a fresh disposable instance establishes the stack baseline. Every attempt requires the App and worker to exit.

## Extensions to shared code (no behavior change for any frozen suite)

- `copyPaneMetadata` keeps the raw `requestedIconName` when the App reports it (the Core closure's readback); a frame without it stays valid, so retained evidence is unchanged.
- `OwnedMetadataObserver` takes an optional `expose(snapshot)` (small bounded facts handed to the worker from the very observation the parent journals; the graded-plan
  driver keeps them only when present) and an opt-in `monotonicHistory`.

## Boundaries

These fixtures qualify protocol handling and the App's stored title/icon state in a disposable direct-local pane. They do not establish a host icon presentation, a
window-title or icon report query, direct-slot addressing, title sanitization policy, remote (SSH, mux) delivery beyond the existing headless/mux regression tests,
Windows or Linux behavior, or any comparison with another terminal.

## Grading, receipt and tests

`title-stack-grading.mjs` re-derives every report from the retained paired journal and the worker's run log; `title-stack-receipt.mjs` binds it (receipt schema 52, source
policy v46) and refuses any retained byte that no longer rederives, including a typed unavailable record. Portable tests cover the oracle, ignored push and pop, wrong
selectors, wrong order, stale values, restoration of unselected fields, empty pops, depth over- and underflow, cross-case substitution, malformed or missing
observations, identity mismatch, tampering and incomplete cleanup; synthetic fixtures are test-only. The frozen real-App publication is the evidence.
