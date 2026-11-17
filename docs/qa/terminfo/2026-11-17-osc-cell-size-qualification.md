# Exact OSC cell-size reports (#993)

Both `extensions.osc1337-cellsize` and `extensions.osc776-cell-size` have
**supported owned effect contracts**, measured in a disposable direct-local
macOS pane. Their unchanged pinned probes and actual stock observations remain
**inconclusive**. This implements the two documented OSC protocols and adds an
independent geometry observer; CSI 16 t support alone establishes neither OSC.

## Contracts and observed replies

The [protocol contract](osc-cell-size.md) records authoritative iTerm2/urxvt
sources, exact frozen probe paths, units, order, rounding and limits. The parent
observed a 40 × 140 view with 8.5 × 18.5 DIP cells, font ascent
14.280059814453125 DIPs, effective render scale 2 and backing scale 2. All 22
paired frames retained identical geometry. The 19 independently fenced
exchanges preserved the complete attributed fixture, cursor, margins, modes,
palette bindings and history, and completed final restoration.

| Case | Documented request | Observed reply body | Effect | Pinned / stock |
|---|---|---|---|---|
| `extensions.osc1337-cellsize` | `OSC 1337;ReportCellSize` with BEL or ST | `1337;ReportCellSize=18.5;8.5;2`, ST terminated | supported | inconclusive: `invalid-reply` |
| `extensions.osc776-cell-size` | `OSC 776;?` with BEL or ST | `776;17;37;29`, matching request terminator | supported | inconclusive: `no-response` |

iTerm2 orders logical **height, width, backing scale**; physical dimensions are
17 × 37 pixels. urxvt orders pixel **width, height, font ascent**; its ascent is
the measured 28.56011962890625 physical pixels rounded to 29. The independent
oracle uses the App's raw view metrics and distinct effective/backing scales,
without calling a production reply formatter. App tests also distinguish UI
zoom from backing scale, including a held window scale; this live measurement
itself uses one backing scale and does not claim hardware zoom transitions.

Each case has CSI 16 t positive controls before and after its OSC exchanges,
both returning `CSI 6;37;17t`. All malformed/unsupported forms stayed silent
through their own complete DA barriers, including incoming reply forms, wrong
case, trailing parameters and invalid question-mark forms. A query caused no
resize. Earlier suites in the shared carrier run have their own resize work;
that is separate from this fixed-geometry experiment.

The unchanged pinned OSC 1337 criterion requires exactly two positive dimensions
and rejects the modern third scale field. The pinned OSC 776 request is bare
`OSC 776 BEL`, omitting the documented `;?`, and receives ordered silence.
Both termless and terminal paths retain these distinct inconclusive results.
The fresh stock collector also reports these reasons and remains **93 supported,
161 inconclusive, 1 unsupported** overall. No frozen Core/Modern requirement or
public common-harness comparison score is changed by this publication.

## Measurement and retained evidence

Run `113410a700e612e361c599b1df920fb2` completed on 2026-11-17, with receipt
schema **70**, source policy **readonly-owned-source-graph-v63** and **425**
retained source/input files. The source manifest SHA-256 is
`c61e63a795925b4c988d50ce78f5273056819f89251b7b4e0d59b2173996cd64`.
The receipt requires source/dependency/adapter bindings, paired independent
observations, unchanged-state controls, restoration, process cleanup and private
fixture removal. The desktop lease was released after verified cleanup.

The Release App was built from
`4fba0d48f6c3633489403cc238bfd0118b205bd8`; measured scripts are
`ecfc4c42a208a3b7ea6135cff532834d08d2fb2f`. App, VT and AgentHost.Contracts
source bytes are unchanged between these revisions. Later C# changes affect
only MCP explanatory tools, which this observer does not execute. The exact
executable and App/VT/contracts assembly hashes are in the build identities.
Selected production source bytes include both the metric producer and independent
observer under their existing fixed aliases. This is retained build/source
identity, not a complete binary-source or reproducible-build attestation.

The `--scope osc633` selection omits `osc9` and `notify`, retaining and rederiving
their separate pinned predecessor archive. This is **combined evidence**, not a
fresh complete default run, and adds no notification or OSC 633 claim.

Three failed attempts remain immutable in the manifest. The first two aborted
before collection because Avalonia's native RenderTimer could not start while
macOS reported both displays asleep; a temporary wake assertion did not resolve
it. After the user woke the desktop, the third reached metadata-start and was
correctly rejected because the strict projection had not admitted the new
`cellGeometry` field. The fix explicitly validates/preserves that optional typed
field; malformed/stale geometry remains fatal and historical omission stays
omitted. All three attempts have no successful receipt or cell-size verdict.
A subsequent invocation refused before launch while another collector held the
shared lease; no other session's lock or processes were touched. The final run
used the unchanged contracts, controls, geometry bounds and deadlines.

Evidence: [manifest](2026-11-17-osc-cell-size/manifest.json),
[report](2026-11-17-osc-cell-size/report.json),
[summary and complete receipt](2026-11-17-osc-cell-size/summary.json),
[owned archive](2026-11-17-osc-cell-size/owned-run.zip),
[build identities](2026-11-17-osc-cell-size/build-identities.json).

```sh
node --test scripts/terminfo/cell-size-publication.test.mjs
node --test scripts/terminfo/cell-size.test.mjs
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/cell-size-grading-collector.test.mjs
node scripts/terminfo-inventory.mjs --check
```

Use Node 24+ and Python 3. Publication rederivation executes trusted current
graders over bounded retained data, reissues the complete receipt, rebuilds the
paired report byte-for-byte and compares the projection. It does not execute
archived JavaScript. The mandatory CI bundle test instead installs a fresh
integrity-checked pinned collector and executes its original probes.

## Ownership, validation and limits

VT owns exact parsing/framing and an immutable atomic metric record, with no UI,
native interop or I/O. App owns view metrics, interface zoom, actual backing
scale and read-only metadata. Missing/invalid host facts cause silence. The
Avalonia fallback font ascent/descent now consistently use DIPs. MCP explains
only the exact OSC subcommands; bare/malformed forms cannot inherit their
support claim. The catalog, executable contract cases, coverage matrix and
generated conformance report are updated.

Local checks: VT 3,583 passed; architecture 191; targeted App geometry 3;
separate PlatformBoot 187 passed/4 skipped; Replay 76; RenderMetrics 48;
MCP 452. Full App gating completed with 8,621 passed, 58 skipped and two
failures: a reflow benchmark at 15.02 ms against 15 ms, and a fake mux handshake
timeout. Unchanged isolated follow-ups passed (benchmark 5.87 ms; entire mux
lifecycle class 31 passed). No threshold, timeout or skip was adjusted. The
failed full run and isolated TRX results are retained in the batch workspace;
applicable PR checks remain required before merge.

The focused metadata/worker/cell-size follow-up passes 74 checks; all metadata
checks pass 165. Source alias mutation checks, receipt refusal checks, fresh
real-bundle replay and independent production/pipeline/fix reviews cover the
validation path. Final publication/head review and latest-head CI are recorded
on the PR. Claims exclude remote/mux, headless hosts, Windows/Linux runtime,
rendered pixels and physical input. No related batch is started.
