# Ntilde Modern assessment for OSC 2 and OSC 0

**55 pass / 0 fail / 14 inconclusive across 69 frozen requirements.**

The [viewport assessment](../2026-11-24-modern-viewport-results/report.md) had 53 pass / 0 fail / 16 inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the criteria. This batch changes only the two rows below.

## `extensions.osc2-title` — pass

Both OSC 2 targets read back exactly and the unhandled OSC left the title unchanged.

| Stage | Requested label | Title reply | Icon label reply |
| --- | --- | --- | --- |
| incoming | — | empty | empty |
| save | — | empty | empty |
| control-a | `M-25a4131e8260b5307f2dd4e388d26b43-C2A` | `M-25a4131e8260b5307f2dd4e388d26b43-C2A` | empty |
| control-b | `M-25a4131e8260b5307f2dd4e388d26b43-C2B` | `M-25a4131e8260b5307f2dd4e388d26b43-C2B` | empty |
| target-a | `M-25a4131e8260b5307f2dd4e388d26b43-T2A` | `M-25a4131e8260b5307f2dd4e388d26b43-T2A` | empty |
| target-b | `M-25a4131e8260b5307f2dd4e388d26b43-T2B` | `M-25a4131e8260b5307f2dd4e388d26b43-T2B` | empty |
| unhandled | `M-25a4131e8260b5307f2dd4e388d26b43-NEG` | `M-25a4131e8260b5307f2dd4e388d26b43-T2B` | empty |
| restore | — | empty | empty |

Incoming title restoration was verified and incoming icon label restoration was verified.

## `extensions.osc0-icon-title` — pass

OSC 0 set the title and icon label to the same requested value in independent replies.

| Stage | Requested label | Title reply | Icon label reply |
| --- | --- | --- | --- |
| incoming | — | empty | empty |
| save | — | empty | empty |
| icon-control | `M-25a4131e8260b5307f2dd4e388d26b43-C1` | empty | `M-25a4131e8260b5307f2dd4e388d26b43-C1` |
| title-control | `M-25a4131e8260b5307f2dd4e388d26b43-C2` | `M-25a4131e8260b5307f2dd4e388d26b43-C2` | `M-25a4131e8260b5307f2dd4e388d26b43-C1` |
| target | `M-25a4131e8260b5307f2dd4e388d26b43-T0` | `M-25a4131e8260b5307f2dd4e388d26b43-T0` | `M-25a4131e8260b5307f2dd4e388d26b43-T0` |
| restore | — | empty | empty |

Incoming title restoration was verified and incoming icon label restoration was verified.

Each read uses public CSI 21 t and CSI 20 t in a DA-fenced exchange, then repeats the queries in the opposite order. The CSI 21 t reader reports the requested window title as OSC l and the CSI 20 t reader reports the requested icon label as OSC L. Empty denotes a complete reply with an empty payload. The icon result measures a label through the protocol. Native icon presentation is outside the measurement.

The child read both incoming labels before any setter, saved them with CSI 22 ; 0 t and restored them with CSI 23 ; 0 t after each case. The unhandled OSC 9999 was checked against the App dispatch source. Each case used fresh distinguishable payloads. The OSC 0 icon reader had a separate OSC 1 control and retained its value through the OSC 2 title control.

## Evidence and reproduction

[Owned run](owned-run.zip) `25a4131e8260b5307f2dd4e388d26b43` started 2026-11-24T17:30:39.777Z on macOS 27.0.1 (26A434), arm64 with Node v24.22.2. App 0.14.0 was built from `0a99252e40265d209df886cd9c27f21c6c978d65`. The measured scripts were `133f6f8158cb5dbb9cb8bf24a983fe44c4351bda`.

The 41 × 140 direct-local fixture enabled AllowTitleReport in its disposable settings. The [manifest](manifest.json) pins the archive. The journal retains exact writes, ordered raw replies and DA barriers for both reads of each label. Source policy `readonly-owned-source-graph-v77` and receipt schema 1 bind the App identities and sources, settings, pane observations, label and kernel terminal restoration, and verified fixture cleanup. The archive includes the desktop lease release events. The [JSON report](report.json) retains all 69 rows.

The App behavior already existed. The [OSC title qualification](../2026-11-12-osc-title-qualification.md) and [XTWINOPS query qualification](../2026-11-15-device-queries-qualification.md) retain their own evidence and outcomes. These observations apply to the frozen macOS App criteria. Stock and common-harness results retain their original scope.

Rederive the reports with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --osc-titles
```

For a fresh measurement, build the recorded App revision and run the collector from the measured scripts revision on an awake macOS desktop. The launcher holds the repository desktop lease through verified cleanup.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope modern-osc-titles
```
