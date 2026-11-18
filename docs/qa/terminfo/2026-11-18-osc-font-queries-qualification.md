# Owned macOS font query qualification (#994)

The disposable direct-local App answered the exact OSC 7770 and OSC 7777 size queries with **10 typographic points**, preserving BEL and ST framing. The observed view was configured at 40/3 DIP with unity UI zoom and effective and backing scales both 2. The 40×140 pane remained at 8×18 DIP cells in a 1128×787 DIP client window. The independent observer measured this geometry separately from the parser replies.

| Case | Pinned termless replay | Pinned terminal replay | Owned query effect | Fresh stock |
|---|---|---|---|---|
| `extensions.osc7770-font-size` | Supported | Supported | Supported | Supported |
| `extensions.osc7777-font-window-size` | Supported | Supported | Supported | Supported |
| `extensions.osc710-font-normal` | No callback | Inconclusive by design | Audit only | Inconclusive |

The pinned size probes require a complete matching OSC reply and accept either terminator. The stricter owned contract also checks the exact integer against the independently observed configured size and requires the matching terminator. Each case passed two CSI 16 t delivery controls and nine required checks. The two documented queries replied once each. Seven malformed and unavailable forms, including a deliberately different 18-point absolute setter, stayed silent. Each exchange completed its own DA barrier. The 27-frame journal records unchanged stored state and geometry throughout the queries and equality of the prepared main screen before and after alternate-screen use.

OSC 710 was audited separately. The pinned terminfo.dev 4.0.0 callback performs no terminal operation. urxvt provides an X11 normal-font description service that is unavailable in Ntilde. See the [protocol audit and primary references](osc-font-queries.md). The qualified effect covers read-only queries for exact positive integer point sizes. Font/window mutation, glyph appearance and other host configurations are outside this measurement.

## Retained runs

- Admitted run `20768dd95adc04324b37659b5520839a`. App build `cb745f6e92781c4bff5c2d1a46ac821d78430dd8`, measured scripts `22c0c882367f9dc0a4ad2521fea26f22d512cbe4` on integrated main `4d878fdfadbd6c4d845f7faa7fdd716440867d39`. The App sources were byte-identical after integration. Receipt 76 binds source policy 69 and 461 captured inputs. The established `osc633` scope omits OSC 9 and OSC 99 notification suites and verifies its retained predecessor. The [manifest](2026-11-18-osc-font-queries/manifest.json) binds the [run archive](2026-11-18-osc-font-queries/owned-run.zip), [summary](2026-11-18-osc-font-queries/summary.json) and [report](2026-11-18-osc-font-queries/report.json).
- Attempt `7f95d3818c574edbbc56b78082c47de7` used a 16 DIP font in the original window. Its 34×119 grid failed before the font suite because the pinned erase fixture requires 40×140. The [first archive](2026-11-18-osc-font-queries/failed/attempt-1.zip) retains the refusal and verified teardown.
- Attempt `3167f4b0eae6be7775460946e25a8256` used the existing owned pane resize to establish 40×140 before the first snapshot. At 16 DIP the resulting pane exceeded the existing Core SGR raster pixel budget and the run stopped before the font exchanges. The [second archive](2026-11-18-osc-font-queries/failed/attempt-2.zip) preserves the resize evidence, refusal and teardown. The third run selected a smaller exact point size while retaining the same grid, raster budget and grading controls.

Fresh stock run `d882215708884c9696570ad445df495b` recorded 96 supported, 1 unsupported and 158 inconclusive. Compared with the preceding OSC 22 stock run, only the two font-size IDs changed from inconclusive to supported. The frozen stock baseline stays separate. The live inventory adds two IDs and two claims. Core, Modern and common-harness grades are outside this publication.

## Reproduce

Offline with Node 24+ and Python 3, independently rederive the receipt, both probe verdicts and the published reports from the retained journal. The pinned collector test runs the unchanged callbacks with the exact verified upstream bundle.

```sh
node --test scripts/terminfo/font-size-publication.test.mjs
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/font-size-grading-collector.test.mjs
node scripts/terminfo-inventory.mjs --check --rederive
```

For a new measurement, follow the [owned runner setup](../QA_TERMINFO.md#owned-macos-adapter) using a built, uninstalled sidecar and its actual version. The runner acquires the desktop lease and verifies the private fixture before launching, prepares the 40×140 grid and checks the kernel PTY, then records teardown and releases the lease.

```sh
node scripts/terminfo-owned.mjs --executable /absolute/path/to/Ntilde --app-version ACTUAL_VERSION_AND_REVISION --scope osc633
```
