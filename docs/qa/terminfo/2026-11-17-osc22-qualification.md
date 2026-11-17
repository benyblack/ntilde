# OSC 22 pointer-shape qualification (#990)

`extensions.osc22-pointer` now has a bounded direct-local App implementation and retained owned effect evidence. The contract was specified before collection in [osc22-pointer.md](osc22-pointer.md), from the [Kitty pointer-shape specification](https://sw.kovidgoyal.net/kitty/pointer-shapes/) and the pinned [Avalonia Cursor implementation](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/src/Avalonia.Base/Input/Cursor.cs).

| Criterion | Result |
|---|---|
| Exact pinned `terminfo.dev@4.0.0` terminal path | **Inconclusive**, preserved: two writes, then CPR only; no pointer readback. No termless function exists. |
| Separate `ntilde-osc22-app-cursor-effect-v1` | **Supported**: eight required checks and nine validity controls held across 15 reads. |
| Same-run stock outcome for this ID | Inconclusive. Stock totals remain 94 supported / 1 unsupported / 160 inconclusive. |
| Native pointer image, position or platform theme realization | Not measured. |

The effect is the requested App state **and the actual Avalonia Cursor object's standard kind**, independently read from its `ToString()` property rather than the request or cache key. A mismapped constructor or external override remains visible. This is a recorded pinned/effect divergence, not a stock, frozen Core/Modern or public-comparison score change. The inventory counts this benchmark ID once; shapes and controls are not additional benchmark IDs.

## Measured behavior

The disposable main screen was 40 rows by 140 columns. Each experiment prepared `text` / `Ibeam` first, so ignoring another setter could not look successful. The exact pinned writes were fed in the stock experiment and the unchanged pinned code was replayed over its journaled one-based cursor (`1;1`). The pinned bundle SHA-256 is `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`, probeHash `16a690f34c0e`.

| Observed request | Actual control cursor |
|---|---|
| `pointer`, including the exact pinned setter and optional `=` | `Hand` |
| `text`, including ST termination after a pointer request | `Ibeam` |
| `crosshair` | `Cross` |
| `default` | `Arrow` |
| Empty reset, including `=` with no name | Null requested override and `Arrow` |

The positive text-delivery control and unknown-name, case, stack-pair, query-form, list and over-length controls held; the latter controls observed unchanged final state, not query silence or interim stack behavior. A valid pointer request after an invalid request worked. All 15 reads also matched the independent stored-cell/cursor/title/cwd/shell/color oracle with OSC 22 removed. Every paired read had no hyperlink override and an observable actual cursor kind. Missing readback or an active link override cannot establish unsupported; wrong required effects can do so only when the validity controls hold.

The complete journal has 18 frames: initial, prepared bind, 15 reads and prepared restoration. Both prepared and restored pointer facts were `{requested:null, controlCursor:"Arrow", linkOverride:false}`. The shared metadata fixture restores its explicit prepared title/cwd and cells; it does not claim restoration of original unprepared null title/cwd, prior hidden-control content or previously ended OSC 3008 records. Disposable teardown and the full cleanup receipt are required for admission.

## Identity and retention

- Admitted run: `412aa71eeb2f93be09465d03b607eb56`, Node `v24.22.2`, direct-local macOS sidecar; receipt **75**, source policy **68**, **455** captured inputs.
- App build: `2e8ef66c8c6ee86758c75538ac57dd42d623c52d`; operator version label `0.14.0+2e8ef66c8c6ee86758c75538ac57dd42d623c52d`. Executable SHA-256 `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`; assembly SHA-256 `3be68fb7b583f0aea02fe4abb365e4874c619979ae72cac52dd49b828e3677b6`.
- Measured scripts: `750f137192ae07588c1e63f87073d5b297a972b3`; selected-source manifest SHA-256 `f9f6c8bb6da6028317b60f03ae9e8ec0b1ac6932274be946effa76f34dd24fe2`.
- OSC 22 derived adapter SHA-256 `191f03faf26dc27448fb1fba9f0e20ff44c6642b898c459a902fdd50c113b13c`; report SHA-256 `45ce176c1ff3b8ae74ee072038f766ad1e42854e56d9a579467208c22de75f4b`.

The final App includes the corrected OSC 720 predecessor `60dfa286`, merged on main as `283db2d8`, and the screen-switch stale-hover fix. The later scripts revision moves expensive full image grading outside the five-second owner RPC; inline seal/restoration checks and mandatory full receipt grading remain intact. The frozen historical source graphs remain available. Revision/version labels are operator-supplied; retained executable, assembly and selected-source hashes are separate evidence, not complete binary-to-source attestation. Later publication-only files are not measured script inputs.

[Manifest](2026-11-17-osc22/manifest.json), [report](2026-11-17-osc22/report.json), [summary and receipt](2026-11-17-osc22/summary.json), [complete owned run](2026-11-17-osc22/owned-run.zip), [attempt ledger](2026-11-17-osc22/attempts.json), [collection logs](2026-11-17-osc22/collection-logs.zip).

The first attempt (`6a6b07d5362038927fc8eb5c06bd2871`, App and scripts `3096b860`) stopped before its OSC 22 observation: the generic owner-dispatch prefix set omitted `ptr`. It has no admitted grade or cleanup receipt. Its failure, incomplete journal and verified teardown remain in [the failed archive](2026-11-17-osc22/superseded/attempt-1.zip). The correction adds exact prefix admission, explicit journal initialization and a regression for all four lifecycle requests and their near misses. All five attempts' lease-release records are retained. No attempt used an existing user tab or native clipboard. The admitted selection omits only the established notification suites; its exact scope-predecessor archive and all other suites are reverified by receipt finalization. No budget, deadline, threshold, ownership guard or failure allowlist was relaxed.

The earlier successful run `a188ef080019680287f85cab559f5adf` (App `3096b860`, scripts `3c139127`) is preserved in [its own archive](2026-11-17-osc22/superseded/attempt-2-pre-hover-fix.zip), [report](2026-11-17-osc22/superseded/attempt-2-report.json) and [summary](2026-11-17-osc22/superseded/attempt-2-summary.json). A subsequent App review found that an old screen's hovered link could mask the next screen's request. The fix clears that stale override and adds a regression. Fresh run `95db74fe99675694c4197ccfca70df84` (App/scripts `bcc85a1f`) is retained in [its archive](2026-11-17-osc22/superseded/attempt-3-before-corrected-predecessor.zip), [report](2026-11-17-osc22/superseded/attempt-3-report.json) and [summary](2026-11-17-osc22/superseded/attempt-3-summary.json).

Both earlier OSC 22 reports independently rebuild and preserve their original cleanup records. Their full receipts are historical: the current corrected OSC 720 plan rejects their old journals. They do not supply the current full-receipt admission or final App measurement.

Attempt four, `72b465974ad2e13e4b0a1113444f8063` (App/scripts `2e8ef66`), failed at the animation seal with “Ntilde owner did not respond.” All 31 animation frames, 30 raw attempts and the disposed raw-reader seal were retained, as were the provisional 18 pointer frames. Its [complete failure archive](2026-11-17-osc22/superseded/attempt-4-animation-rpc-failure.zip) has no receipt or admitted effect. Offline grading of those real frames took about 4.78 seconds inside a five-second owner request. The fix acknowledges the checked wire seal first and performs unchanged full PNG grading after worker completion; grading failure still prevents receipt admission. New tests exercise the deferred state, mandatory finalization, failed grading and malformed raw seals. The final fifth run is fresh, with verified cleanup and lease release.

## Implementation and limits

VT validates four case-sensitive names and empty reset, BEL/ST, optional `=` and the 32-character payload bound, then emits a request with parse-time screen identity. The App owns separate main/alternate requests, native cursor caching, hyperlink priority and resource release. RIS/DECSTR withdraw both overrides; new buffers reset them, idempotent reattachment preserves them, switching screens withdraws the old screen's hover override, and retired views ignore new requests. `readPaneMetadata.pointerShape` exposes the request, actual control kind and link override separately; historical absence remains absent.

Owning VT/App tests cover screen separation, resets, actual-property independence, hover restoration, buffer replacement and retirement. Their broader coverage is distinct from this main-screen runtime effect. Full Kitty stacks, queries, X11 aliases and other CSS shapes remain unsupported. No native cursor pixels, physical mouse/layout, runtime hover or alternate-screen effect, mux/replay persistence, SSH, other-platform runtime or real-emitter claim is made.

## Reproduction

Offline receipt, journal, failed-attempt and publication rederivation (Node 24+, Python 3; no desktop or archived code execution):

```sh
node --test scripts/terminfo/osc22-publication.test.mjs
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/osc22-grading-collector.test.mjs
node scripts/terminfo-inventory.mjs --check --rederive
scripts/build.sh test tests/Ntilde.VT.Tests --filter 'FullyQualifiedName~Osc22PointerShapeTests|FullyQualifiedName~VtCapabilityContractTests'
scripts/build.sh test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter 'Lane!=PlatformBoot&(FullyQualifiedName~PointerShape|FullyQualifiedName~MainWindowAgentMetadataTests)'
```

For a fresh measurement, prepare the repository's exact pinned collector, build a new uninstalled sidecar using `scripts/run-sidecar.sh --no-launch`, and use the owned runner with the actual build version/revision:

```sh
node scripts/terminfo-desktop-lock.mjs status
node scripts/terminfo-owned.mjs --executable /absolute/path/to/uninstalled/Ntilde --app-version ACTUAL_VERSION_AND_REVISION --scope osc633
```

The runner must acquire the shared desktop lease, prove its disposable App/worker/pane, retain complete paired observations and verify restoration and cleanup. A fresh run never replaces these immutable archives.
