# Integrated exact OSC cell-size reports (#993)

Both `extensions.osc1337-cellsize` and `extensions.osc776-cell-size` have
**supported owned effect contracts** in the integrated direct-local macOS App.
Their unchanged pinned probes and fresh stock observations remain
**inconclusive** (`invalid-reply` and `no-response`, respectively). This adds two
qualified inventory IDs and two supported effect claims; it adds no pinned,
frozen Core/Modern or common-harness points.

The [protocol contract](osc-cell-size.md) specifies the authoritative iTerm2 and
urxvt sources, exact probes, units, field order and rounding. This publication
supersedes the active claim from the [first cell-size measurement](2026-11-17-osc-cell-size-qualification.md),
whose archives, failures, receipt and rederivation test remain unchanged.

## Observations

The independently observed view was 40 × 140 cells, each 8.5 × 18.5 DIPs, with
font ascent 14.280059814453125 DIPs and effective/backing scale 2. All 22 paired
frames retained that geometry. All 19 fenced exchanges preserved the complete
attributed fixture, cursor, margins, modes, palette bindings and history, and
completed final restoration. No cell-size query required a resize.

| Case | Documented request | Observed reply | Effect | Pinned / stock |
|---|---|---|---|---|
| `extensions.osc1337-cellsize` | `OSC 1337;ReportCellSize`, BEL or ST | `1337;ReportCellSize=18.5;8.5;2`, ST | supported | inconclusive: `invalid-reply` |
| `extensions.osc776-cell-size` | `OSC 776;?`, BEL or ST | `776;17;37;29`, matching terminator | supported | inconclusive: `no-response` |

iTerm2 reports logical height, width and backing scale. urxvt reports physical
pixel width, height and font ascent; 28.56011962890625 physical ascent rounds
to 29. The oracle derives expectations independently from the view metrics;
it does not call the production formatter. CSI 16 t delivery controls before
and after each case returned `CSI 6;37;17t`. Unsupported forms stayed silent
through completed DA barriers. Both actual pinned paths retain their original
criteria: OSC 1337 rejects a third scale field, while bare OSC 776 omits `;?`.
The fresh scoped stock run reports 93 supported, 161 inconclusive and 1 unsupported.

## Integrated measurement and reproduction

Run `4b63f57a61d3905acb38ac6a8738696f` completed at
`2026-11-17T08:50:10.737Z`, using receipt schema **71**, source policy
**readonly-owned-source-graph-v64** and **432** retained source/input files.
The source manifest SHA-256 is
`555706bebfe1aed48bcd008c3acdbe4ade2843a4b99fdfaeb82bdc1cf966f1cc`.
Both the Release App and scripts are revision
`8da07482a849946cf213d1d4b6781759bde1c56f`; the App informational version is
`0.14.0+8da07482a849946cf213d1d4b6781759bde1c56f`.
Exact executable and assembly hashes are retained in the build identities.
Publication-only changes do not change any of these 432 measured inputs.
This records build/source identity, not a reproducible-build attestation.

The complete receipt validates both the new cell-size family and the merged
Kitty animation family (18 animation controls passed), all required shared
bindings, restoration, process cleanup and fixture removal. The desktop lease
was released after verified cleanup. An earlier invocation refused before App
launch while another collector held the lease; it produced no measured attempt.
No other session's lock or processes were touched.

The scoped collection omits `osc9` and `notify`, rederiving their separately
pinned predecessor evidence. It is combined evidence, not a fresh complete
default run, and adds no notification or OSC 633 claim. Historical policy-v63
cell-size and animation runs retain their distinct 425- and 420-input graphs
and schema-70 receipts. The integrated policy requires both families; missing,
ambiguous or grafted family declarations cannot bypass validation.

Evidence: [manifest](2026-11-17-osc-cell-size-integrated/manifest.json),
[report](2026-11-17-osc-cell-size-integrated/report.json),
[summary and full receipt](2026-11-17-osc-cell-size-integrated/summary.json),
[owned archive](2026-11-17-osc-cell-size-integrated/owned-run.zip),
[build identities](2026-11-17-osc-cell-size-integrated/build-identities.json).

```sh
node --test scripts/terminfo/cell-size-integrated-publication.test.mjs
node --test scripts/terminfo/cell-size-publication.test.mjs scripts/terminfo/kitty-animation-publication.test.mjs
node --test scripts/terminfo/source-policy-integration.test.mjs scripts/terminfo/cell-size.test.mjs
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/cell-size-grading-collector.test.mjs
node scripts/terminfo-inventory.mjs --check
```

Use Node 24+ and Python 3. Trusted current validators reissue the full receipt,
rebuild the paired report byte-for-byte and compare the publication projection.
Archived JavaScript is never executed. The mandatory fresh-bundle test executes
the original integrity-checked pinned probes independently.

## Validation and limits

Owning local checks passed: VT 3,649; MCP 482; targeted App geometry, metadata,
animation and current tab behavior 33; RenderMetrics 48; Replay 76; architecture
191. Separate PlatformBoot previously passed 187 with 4 skips. Historical graph
rederivations, strict metadata, integration refusal tests, fresh real-bundle
replay and all 55 registered publication/finding rederivations passed. The generated
inventory contains 143 IDs, 145 claims and 141 effect-supported IDs, with pinned
supported unchanged at 93. Its offline consistency check passes.
Conformance validates 136 rows with no errors or warnings.

The earlier full local App gating run recorded 8,621 passes, 58 skips and two
failures (a 15.02 ms reflow benchmark against 15 ms, and a fake mux handshake
timeout). Unchanged isolated follow-ups passed: benchmark 5.87 ms and mux class
31 tests. No threshold, timeout or skip was changed. Final-head review and
applicable CI results are recorded on the PR and remain required before merge.

This is one fixed geometry/backing scale on direct-local macOS. Zoom/backing
scale separation is additionally covered by deterministic App tests, without a
live hardware-scale-transition claim. Claims exclude Windows/Linux runtime,
remote/mux, physical input and rendered-pixel appearance. VT owns protocol
parsing and atomic facts; App owns metrics and the independent UI observer.
MCP explanations and conformance cover only exact supported subcommands.
