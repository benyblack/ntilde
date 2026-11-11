# OSC 7 / OSC 8 benchmark layer (extensions.osc7-cwd, extensions.osc8)

This is the contract behind the [2026-11-11 OSC 7 / OSC 8 qualification](2026-11-11-osc7-osc8-qualification.md). It qualifies two pinned terminfo.dev@4.0.0 cases through
the App's existing read-only pane-metadata and session-status observations ([owned-metadata.md](owned-metadata.md), [owned-pane-metadata-api.md](owned-pane-metadata-api.md)).
It adds no production API and changes no production behavior: it newly qualifies existing behavior. It reuses the paired observer of the frozen metadata suite (its 13
experiments, 46 frames and v8 receipt keep their meaning; they already contained one OSC 7 and one OSC 8 observation each, which this layer does not reuse as evidence)
under its own `lnk` prefix, plan and baseline. Both cases sit outside the frozen 80-row Core catalogue, so **no Core percentage changes** and no comparison with another
terminal is established.

## What the pinned probes actually are

Read from `terminfo.dev@4.0.0` (`terminfo.bundle.mjs`, SHA-256 `aa5f2bf9…d1f6`), not inferred from the names:

| Case | Pinned termless code | Pinned terminal code | Pinned verdict |
|---|---|---|---|
| `extensions.osc7-cwd` | **no bytes.** Reports `{"declared": ctx.capabilities.extensions.has("osc7")}`: inconclusive, evidence `legacy`, "Declared OSC 7 capability does not establish working-directory metadata delivery" | resolves inconclusive, evidence `none`, "No working-directory metadata readback for OSC 7"; writes and reads nothing | inconclusive by design |
| `extensions.osc8` | feeds `ESC[1;1H ESC[2K A ESC]8;;https://example.com/osc8-proof BEL LINK ESC]8;; BEL Z`, reads cells (0,0)–(0,5) and each cell's hyperlink metadata; passes when the characters are `ALINKZ` and the links are `[null, uri, uri, uri, uri, null]` (supported / unsupported, evidence `parser-state`; inconclusive when no link metadata or the linked text is not observable) | writes the same bytes, then asks for the cursor position: **always** inconclusive, evidence `consumed` ("Cursor movement cannot verify OSC 8 link metadata or click behavior") | termless path decides over stored cells and link runs; terminal path inconclusive by design |

Two facts follow and are stated rather than worked around:

- **`extensions.osc7-cwd` has no pinned fixture to replay.** There is nothing to execute unchanged. The pinned verdict is inconclusive by design and is preserved; the OSC 7
  bytes below belong to a separately identified effect contract (`ntilde-osc7-cwd-metadata-effect-v1`) that is never attributed to the pinned probe. A divergence
  `pinned-inconclusive-by-design` is recorded.
- **`extensions.osc8`'s termless probe decides** when it can read the stored cells and their hyperlink metadata. It was executed unchanged over the parent-journaled stock read
  (characters from the stored viewport, `getHyperlinkAt` from the complete link runs the pane-metadata API returns). Its verdict is the pinned conclusion. The terminal path
  stays inconclusive (a cursor position cannot verify link metadata) and is recorded beside it as `terminal-path-inconclusive-by-design`; the retained stock-collector grade
  (terminal path) stays inconclusive. The replay context declares the capabilities `hyperlinks` and `osc7`: adapter-side constants naming what the observation API can
  report, not observed facts, and they never decide a verdict (the OSC 7 probe is inconclusive whatever it declares).

The pinned code is retained and executed: a digest-checked derivation (`ntilde-owned-osc78-grading-v1`, layered over the left/right-margin one like its sibling layers) refuses
unless the retained bundle still contains both registrations and the `readHyperlinkMetadata` helper verbatim and once, then only appends exports. The worker runs both paths
over the journaled stock-context reads (the parent attaches the read's hyperlink runs to its answer); the parent re-derives the same results from the retained journal with an
independent reimplementation and, when it holds the bundle, re-executes the termless probes. All three must agree.

## What each effect contract grades

The independent oracle (`osc78-contracts.mjs`) interprets every experiment's exact bytes and predicts the whole screen, the cursor, the title, the host working directory and
every hyperlink run. Expected OSC 7 paths are literals, never produced by a parser.

**OSC 7 (reported working-directory metadata).** From the repository's own parser documentation (`AnsiParser.Osc.cs`): the authority of a `file://` URI is always dropped, the
path is percent-decoded, and the reported value is the pane's host working-directory metadata. The metadata carries the decoded path only, so no host component is exposed.
Nothing here invents a rejection or normalization rule (no unsupported or malformed form is graded).

| Experiment | Role | Meaning |
|---|---|---|
| `stock` | stock | the measured prepared baseline (there is no pinned fixture) |
| `effect` | effect | prior baseline, an ordinary text control (no OSC 7), the first URI, a distinguishable second URI (fresh), a restoring URI |
| `space` | required | `%20` in the transmitted URI is decoded in the reported path |
| `gate-title` | gate | an OSC 2 title change must not move the cwd (and the cwd readback is not merely "the latest OSC") |
| `authority` | observed | a foreign authority (`file://remote-host.example/…`) is dropped by documented design; recorded, never required |
| `st-terminator` | observed | the first URI with an ST terminator |

**OSC 8 (stored hyperlink-bearing cells).** A link is opened by a non-empty URI with an optional `id=` parameter, closed by an empty one, switching straight to another link is
legal, and every cell written while a link is open carries it. The API reports maximal runs with **snapshot-local identities** numbered in order of first appearance; identities
are compared only as a same-link relation inside one snapshot (the partition of the runs), never across reads and never to the OSC id.

| Experiment | Role | Meaning |
|---|---|---|
| `stock` | stock | the exact pinned fixture on the prepared baseline; its read is the pinned replay's source |
| `effect` | effect | a neutral baseline without links, the same text with **no** OSC 8 (correct text without hyperlink metadata), the exact pinned fixture, then more text on another row after the close |
| `gate-close` | gate | only close sequences, never an open: no link metadata appears |
| `id` | required | `id=` parameter and URI exactly reported on the exact cell range, closed-link witness unlinked |
| `two-links` | required | two distinct adjacent links (switching without a close) stay two runs with distinct identities |
| `stale` | required | the pinned link, a plain overwrite of the same cells (no stale link survives), then a second fixture with another URI on the same cells |
| `same-anchor` | observed | two separated runs with the same `id` and URI share one identity |
| `st-terminator` | observed | the pinned fixture with ST terminators |

A case is **supported** when every validity control held and every required read matches the oracle; **unsupported** when a required read mismatches while every control held;
**inconclusive** when a control failed (the prior state, the unmarked bytes, a gate and, for OSC 7, the prepared baseline). The OSC 8 stock read already carries the pinned fixture under test, so a mismatch there is a failing required read (`stock-pinned-fixture`), never an invalid control. Labels never grade. A stable wrong state is evidence: nothing
waits for an expected value to appear, and a typed refusal of the first observation is the only optional outcome.

## Boundaries

These fixtures qualify what the pane reports. They do not establish that the child process's working directory moved (nothing here calls `chdir` or inspects a directory),
that the named path exists, remote-host validity, click handling, browser launch, URI safety policy, URL opening (no URL is ever requested or opened; the URIs use
`example.com` and `example.invalid`), rendered underline appearance, OSC title handling, OSC 133 P, OSC 633, the aggregate semantic-prompts case, Windows or Linux behavior,
or remote (SSH, mux) delivery. Cleanup is exactly what the observation API can show: the suite restores its **prepared** title/cwd baseline (the first observation is whatever
the preceding suites left, itself not the original null metadata), the initial cells and the original palette, and every attempt requires the disposable App and worker to
exit. The report retains the initial and final title and cwd and states which of them was restored; disposal does not retroactively prove restoration.

## Grading, receipt and tests

`osc78-grading.mjs` re-derives every report from the retained paired journal and the worker's run log; `osc78-receipt.mjs` binds it (receipt schema 51, source policy v45) and
refuses any retained byte that no longer rederives, including a typed unavailable record. Portable tests cover the oracle, the independent pinned replay (cross-checked against
the real pinned bundle), each named defect (link-open ignored, text without link metadata, a wrong URI or id, a range reaching the witness or stopping short, stale metadata, merged
links, a closed link affecting later text, an undecoded or dropped encoded space, a stale or repeated second cwd, an unrestored baseline), wrong fixtures, cross-case substitution,
identity mismatch, tampering and incomplete restoration; synthetic fixtures are test-only. The frozen real-App publication is the evidence.
