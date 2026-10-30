# Owned terminfo.dev adapter pilot — 2026-10-30

The [local macOS adapter](../../../scripts/terminfo-owned.mjs) launched a
disposable Ntilde desktop process with fresh settings and a direct probe worker.
It verified the app/worker process relationship, executable, user and PTY, then
registered a distinct ownership grant with the locally extended collector.
Existing terminal sessions and settings were not used.

| Context | Value |
|---|---|
| Time | 2026-10-30 09:09 CET (08:09 UTC) |
| Platform | macOS 27.0.1; fixture measured 140 columns × 41 rows |
| Supplied app version | `0.12.0+6f077649415fc1eaa34d9be56db4d5caf286324c` |
| Version authority | Operator-supplied sidecar assembly metadata; collector identity remains unverified |
| Collector base / runtime | `terminfo.dev@4.0.0`, Node `v24.22.1` |
| Adapter | `ntilde-owned-macos-v1` — local extension, not stock CLI |
| Suite / probe hash | `16a690f34c0e` |
| Upstream collector source | `cf7d8759c7e637aa8dba1e429f3dddd1c0124158` |
| Derived collector SHA256 | `1c6bdf7c02ef24bf61a58dcdb43fbb9d4377f87ad15a1db0da1d78a2aa5f19e7` |
| Run ID | `39169570112949f380ee974e0ac43db8` |
| Raw SHA256 | `8d65dbb5c1e6ec30a26933ad79af034fddd538ceb66926466d495503d90f65a6` |

Local evidence is retained under `artifacts/terminfo/owned-yN0fZm/` (Git ignored):
raw replies, original suite receipt/dependency lock, initial settings, binary
and adapter hashes, app output and process/TTY ownership proof. This document
does not replace those originals or include them in a fresh checkout.

## Outcomes and limits

| Outcome | Count |
|---|---:|
| Supported observations | 63 |
| Unsupported observations | 11 |
| Inconclusive: insufficient evidence | 152 |
| Inconclusive: no response | 26 |
| Inconclusive: policy refused | 3 |
| Recorded observations | 255 |
| Additional ungraded collector diagnostic | 1 |

The original [inline pilot](2026-10-30-macos.md) had 206 ownership-policy
refusals. Only three remain here: `extensions.osc52-clipboard`,
`extensions.osc52-write` and `extensions.osc52-read`. They require an isolated
clipboard fixture with verified restoration; no OSC 52 bytes were sent.

The 256-definition suite is **incomplete** (`suiteComplete: false`). Its
`cursor.reverse-wrap` callback accesses geometry without declaring that
requirement, producing upstream's `UndeclaredTerminalGeometry` diagnostic.
It has no graded observation. The raw record retains the diagnostic; do not
count it as a Ntilde failure or silently supply a supported result.

Many probes now execute but lack the pixels, exact fixture size, generated input
events or other semantic evidence needed for a conclusion. Cursor movement
does not prove styling. This adapter supplies process ownership and measured
geometry; it does not supply screenshot, resize, input-injection or clipboard
fixtures. The fresh configuration also differs from the inline pilot, so these
counts compare coverage, not a change in the terminal's support score.

The collector's unsupported observations cover advanced Kitty keyboard flags,
OSC 66 sizing, OSC 5522, color-stack push/pop, mode 1048, color-scheme reporting
and soft reset. Treat those as scoped observations on this older sidecar build.
No current capability claim or parser behavior was changed from these results.

## Validation

The live run completed and the launched app and worker exited. The private
app-data root and control socket were removed; ownership/cleanup evidence was
retained. All 18 adapter tests passed, including real-app interruption and early
process exit cleanup, plus invalid ownership and collector-digest rejection.

The transformation preserves the original probe definitions and evidence
grading. Its separate owner grant is issued after measured process/PTY checks;
it does not impersonate a Terminal.app or Linux receipt. The original CLI bundle
is unchanged, and the resulting data explicitly names the local collector
extension. These are local engineering measurements, not upstream-approved
support results.
