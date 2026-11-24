# Ntilde Modern assessment — synchronized output and color scheme reporting

**51 pass / 0 fail / 18 inconclusive across 69 frozen requirements.**

The [four-cursor assessment](../2026-11-23-modern-cursor-results/report.md) had 49 pass / 0 fail / 20 inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the frozen criteria, source snapshot, and upstream attribution.

These exchanges measure the App protocol state of modes 2026 and 2031 in one disposable direct-local macOS pane. The frozen criteria have separate library arms. Frame presentation and unsolicited color-scheme notifications are outside this measurement. Core, stock collector results, and the qualification inventory retain their existing grades.

## Protocol exchanges

Each query ends at a DA barrier. The reset-state branch sends DECSET, the first query and barrier, DECRST, and the final query and barrier in one PTY write. The 2026 branch requires the complete exchange inside the App’s 200 ms synchronized-output window.

### Mode 2026

`modes.synchronized-output` — **pass** (supported).

| Stage | Exact framed request | Exact framed reply | DECRPM states | Elapsed ms |
| --- | --- | --- | --- | --- |
| initial | `"\u001b[?2026$p\u001b[c"` | `"\u001b[?2026;2$y\u001b[?62;4;22c"` | 2 | 0.80 |
| set-restore | `"\u001b[?2026h\u001b[?2026$p\u001b[c\u001b[?2026l\u001b[?2026$p\u001b[c"` | `"\u001b[?2026;1$y\u001b[?62;4;22c\u001b[?2026;2$y\u001b[?62;4;22c"` | 1 → 2 | 0.51 |

### Mode 2031

`modes.color-scheme-reporting` — **pass** (supported).

| Stage | Exact framed request | Exact framed reply | DECRPM states | Elapsed ms |
| --- | --- | --- | --- | --- |
| initial | `"\u001b[?2031$p\u001b[c"` | `"\u001b[?2031;2$y\u001b[?62;4;22c"` | 2 | 0.15 |
| set-restore | `"\u001b[?2031h\u001b[?2031$p\u001b[c\u001b[?2031l\u001b[?2031$p\u001b[c"` | `"\u001b[?2031;1$y\u001b[?62;4;22c\u001b[?2031;2$y\u001b[?62;4;22c"` | 1 → 2 | 0.17 |

## Measurement and provenance

Owned run `143a7ed6de7a05544364a898fc6fe975`, started 2026-11-24T13:44:15.907Z. App source `faca305c5e915dbb6a3f5ffbe9e4c927b9c071f5`; measured scripts `4099739ce69ba5c0ba06f5eb581f699bb97f5f9e`; App version `0.14.0` (operator supplied; not independently verified). Geometry 40×140; source policy `readonly-owned-source-graph-v74`; receipt 81, completed 2026-11-24T13:48:47.746Z.

The [owned run](owned-run.zip) has SHA-256 `c0a5bd28fe747bd50c1d8204d228c7b596067f42fb300b854d5ff116a38a9af8`. It retains the worker exchanges in `mmod-run.json`, the pane-bound journal in `mmod-snapshots.jsonl`, and the derived traces in `mmod-observations.json`. The receipt verifies the source manifest, mode exchanges, restored main-screen projection, and process and fixture cleanup.

## Reproduce

Run the independent receipt and report derivation offline with Node 24+ and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --modes
```

The [machine-readable report](report.json) includes every frozen criterion, observation path, stock verdict, and current blocker.

For a fresh capture, build the Release App at the recorded App revision with the build wrapper and [prepare the collector](../../QA_TERMINFO.md) at the measured scripts revision. Run the standalone sidecar on an awake macOS desktop. The launcher acquires the desktop collection lock.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope osc633
```
