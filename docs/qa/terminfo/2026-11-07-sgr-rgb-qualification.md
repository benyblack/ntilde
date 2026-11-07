# SGR standard-color RGB-appearance qualification — 2026-11-07

The two named-color criteria left inconclusive by the
[2026-11-06 SGR-colors batch](2026-11-06-sgr-colors-qualification.md) —
`sgr.fg.standard` and `sgr.bg.standard` — are now measured **supported at the
exposed-RGB level** against a fresh disposable macOS desktop instance, by
executing the pinned collector's own `namedColorProbe` criterion over
App-resolved per-cell RGB bound to one recorded palette/theme state, alongside
a completed schema-15 cleanup receipt. The earlier stored-index conclusions
are unchanged and still rederive; nothing about them is restated more strongly
than before.

This is a **local collector-extension measurement**, not stock CLI output. The
same instance's owned-adapter collector run (the layered
`ntilde-owned-sgr-rgb-grading-v1` adapter chain over the pinned
`terminfo.dev@4.0.0`, not the unmodified stock CLI) retains its recorded
identity (86 supported / 1 unsupported / 168 inconclusive, `suiteComplete=false`)
with SGR scoring unchanged from stock definitions, and grades both cases
inconclusive with `consumed` evidence — the pin's own terminal path states
"Cursor advance does not verify SGR styling".

## What each conclusion establishes

| Case | Measured evidence (zero-based cells, theme `Default`, no overrides, DECSCNM off, minimum contrast 1) | Conclusion |
|---|---|---|
| `sgr.fg.standard` | Control `C` resolves its foreground to the theme default (`null` storage marker); `X` (SGR 31) resolves to the theme palette entry 1 = **rgb(205, 0, 0)**; `Y` (SGR 34) resolves to entry 4 = **rgb(0, 0, 238)**; the three values are pairwise distinct in the pinned sense; backgrounds stay on default storage | **supported** (resolution-stage RGB) |
| `sgr.bg.standard` | Control `C` resolves its background to the theme default; `X` (SGR 41) resolves to entry 1 = **rgb(205, 0, 0)**; `Y` (SGR 44) resolves to entry 4 = **rgb(0, 0, 238)**; pairwise distinct; foregrounds stay on default storage | **supported** (resolution-stage RGB) |

Both conclusions sit **beside** the stored-index conclusions of the
SGR-colors suite (31→1, 34→4, 41→1, 44→4 at the storage level, reconfirmed
supported in this same run), not on top of them. The RGB level claims exactly
the storage-to-theme **resolution stage**: each cell's channel was resolved
through the draw path's own precedence (default → theme default, palette
index → ANSI palette entry, literal → stored ARGB) against one coherent
render snapshot. It does **not** claim rendered glyph appearance — minimum
contrast adjustment, opacity compositing and rasterisation are downstream and
explicitly out of scope — and no screenshot evidence is claimed.

The pinned distinguishability semantics are preserved exactly: had the theme's
entries aliased (entry 1 equal to entry 4), the criterion would stay at its
own "Baseline, theme colors, or color readback were indistinguishable"
inconclusive, never unsupported. With the measured theme the values are
distinct, and the pinned code, the independent contract grader and the
parent-side replay agree on every field.

## Evidence chain

The worker replayed each case's exact pinned fixture bytes (byte-identical to
the stored-index batch's fixtures) through the owned fence (every write
followed by a DA ordering barrier). The parent observer read each resulting
screen through the nonce-bound pane with the App's opt-in resolved-color
projection: per-cell resolved foreground/background RGB **plus** the stored
attribute runs of the same snapshot, and the palette/theme binding the values
were resolved against (theme name, defaults, 256-entry ANSI palette, override
flags, DECSCNM, minimum-contrast setting) — captured from one coherent
cells-plus-theme render snapshot under the buffer's read lock. Resolution
stays in the App's rendering layer, which reuses the draw operation's own
resolution precedence; the VT core is unchanged.

Every resolved value must cohere with the binding's palette lookups and with
the stored attribute runs cell by cell (default markers, palette flags with
exact indices, literal storage), and one binding digest must cover the whole
suite — a palette or theme change during observation voids the run. The
pinned `namedColorProbe` code then executed over the RGB-carrying projection
inside the derived collector extension, and the published conclusion requires
the pinned code, the independent contract grader and a second parent-side
replay of the pinned code over the retained journal to agree on every field.
Five frames — start, nonce bind, two read groups, finish — are retained with
hashes; the start and finish frames must match exactly (alternate-screen
restoration).

## Collector extension identity

| Measure | Value |
|---|---|
| Layered grading adapter | `ntilde-owned-sgr-rgb-grading-v1` (contracts v1) |
| Derived from | `ntilde-owned-sgr-colors-grading-v1` over `ntilde-owned-ascii-sgr-grading-v1` over `ntilde-owned-macos-v1` over pinned `terminfo.dev@4.0.0` |
| Pinned bundle SHA-256 | `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6` |
| Layered bundle SHA-256 | `3b4af84dc1a774baf8862115f7d907ece7d63f406c1f34d428991b4075bf18f1` |
| Receipt schema | 15; SGR-RGB binding provisional until the shared cleanup receipt |

The derivation refuses unless the retained SGR-colors extension still
contains the exact pinned probe definitions; the upstream bundle and all three
lower-layer adapters stay byte-identical underneath the appended exports, and
the receipt re-derives the layered adapter from the retained bytes of the
whole chain rather than trusting declarations.

## Measurement

| Measure | Value |
|---|---|
| Run / receipt completed | run `a3c82293b63665af56ee96018c2472f7`, receipt 2026-11-07T14:34:35.903Z (schema 15) |
| App informational version | `0.13.0+c54efdc8` |
| App source | `c54efdc8…` (this branch's App-side extension: the opt-in resolved-color readScreen projection with the review-driven capture-dimensions refusal; differs from main) |
| Adapter/scripts source | pinned byte-for-byte by the run's policy-v9 source inventory (`c54efdc8…`, the same commit; recorded by role, not by trust) |
| Owned-adapter collector run | 86 supported / 1 unsupported / 168 inconclusive; `suiteComplete=false`; SGR scoring unchanged from stock definitions (derived adapter output, not unmodified stock CLI bytes) |
| Two-case collector outcomes | inconclusive (`consumed`) |
| Same-run predecessor suites | first batch 4/4 supported; SGR-colors 5/5 supported (reconfirmed under the current contracts v2; the frozen 2026-11-06 publication keeps its measured v1 semantics and still rederives) |
| Geometry | 40 rows × 140 columns |
| Local evidence | `artifacts/terminfo/owned-lHNMLR` (machine-readable copy with digests in the [JSON](2026-11-07-sgr-rgb-qualification.json)) |

The collector run's single `unsupported` remains `extensions.osc5522-clipboard`
("Mode 5522 not supported", evidence `query`) — outside this batch and
unchanged by it.

## Preserved attempts and failures

Two preparation facts are retained with their original shape:

1. One pre-launch invocation was refused by the sidecar gate before any App
   process, fixture or evidence existed (the executable path resolved through a
   source tree containing `Ntilde.App/`, which the `.app`-bundle refusal
   legitimately rejects); the measurement was issued from the mirrored sidecar
   path instead.
2. The **first complete measurement** (run `710c19c4438d398f712fbec3763a4b95`,
   receipt schema 15 completed 2026-11-07T14:07:17.187Z, 2/2 supported) was
   collected before the Codex/CodeRabbit review fixes: its journal predates the
   stored literal-byte coherence input (cellDetails in every RGB frame) and its
   App build predates the capture-dimensions refusal. It is superseded by the
   published run above and retained unmodified in `artifacts/terminfo/owned-rfQ8HV`
   with its facts in the [JSON](2026-11-07-sgr-rgb-qualification.json). No
   historical publication, receipt or archived-evidence rederivation changed.

## Reproduce

```sh
node --test scripts/terminfo/sgr-rgb-*.test.mjs scripts/terminfo/resolved-colors.test.mjs scripts/terminfo/sgr-colors-*.test.mjs
```

The publication test re-derives the whole graded report from the committed
journal and run log, re-checks the contract-grader agreement and observed
values, cross-references the same-run predecessor suites and the collector
run's unchanged two-case outcomes, and verifies older publications
byte-for-byte. The full live measurement rerun requires macOS, Node 24+, the
pinned collector prepared from the checked-in lockfile, an awake display and a
disposable sidecar build ([QA_TERMINFO.md](../QA_TERMINFO.md)).

The VT parser is unchanged; the App gained an opt-in readScreen projection and
the draw path's resolution helpers were made internal so the projection
resolves through the exact same precedence.
