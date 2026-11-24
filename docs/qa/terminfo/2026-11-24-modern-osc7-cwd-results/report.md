# Ntilde Modern OSC 7 current directory assessment

**56 pass / 0 fail / 13 inconclusive across 69 frozen requirements.**

The [OSC title assessment](../2026-11-24-modern-osc-titles-results/report.md) had 55 pass / 0 fail / 14 inconclusive. This batch updates one row of the [original Modern assessment](../2026-11-15-modern-results/report.md).

## `extensions.osc7-cwd` — pass

Both same-basename targets read back with their distinct parent paths. The unhandled OSC held the path and the incoming URI was restored.

The disposable worker launched with its reported directory unset. It emitted `file://localhost/qa-osc7/cwd-cdbshahs/start` as fixture setup. The App showed the expected path, and a fresh hover verified the incoming path before the first control. The two targets had the same basename and different parent directories. The checked OSC 9999 carried a different URI-shaped payload and left the second target path in place. Restoration replayed the exact fixture URI and reopened the tooltip for a separate verification.

| Stage | Public tooltip path | Private pane metadata |
| --- | --- | --- |
| launch | unset | unset |
| fixture | `/qa-osc7/cwd-cdbshahs/start` | `/qa-osc7/cwd-cdbshahs/start` |
| incoming | `/qa-osc7/cwd-cdbshahs/start` | `/qa-osc7/cwd-cdbshahs/start` |
| control | `/qa-osc7/cwd-cdbshahs/check` | `/qa-osc7/cwd-cdbshahs/check` |
| target-a | `/qa-osc7/cwd-cdbshahs/east/repo` | `/qa-osc7/cwd-cdbshahs/east/repo` |
| target-b | `/qa-osc7/cwd-cdbshahs/west/repo` | `/qa-osc7/cwd-cdbshahs/west/repo` |
| unhandled | `/qa-osc7/cwd-cdbshahs/west/repo` | `/qa-osc7/cwd-cdbshahs/west/repo` |
| restore | `/qa-osc7/cwd-cdbshahs/start` | `/qa-osc7/cwd-cdbshahs/start` |
| restored | `/qa-osc7/cwd-cdbshahs/start` | `/qa-osc7/cwd-cdbshahs/start` |

The App’s tab tooltip now adds the active pane’s reported full directory on a second line. Each DA-fenced action was followed by a fresh native hover and a screenshot of the owned tooltip window. Apple Vision transcribed the captured pixels. Read-only pane metadata corroborates the decoded path. The result covers the path shown on the macOS horizontal tab tooltip. The child’s working directory and the headless OSC 633 P control are separate measurements.

The [earlier OSC 7 metadata qualification](../2026-11-11-osc7-osc8-qualification.md) established decoded pane metadata. This batch adds the full-path tooltip and uses its native pixels for the frozen App criterion.

## Evidence and reproduction

[Owned run](owned-run.zip) `231f707f7857ddc7b07b9e32a6f2943e` started 2026-11-24T18:37:53.997Z on macOS 27.0.1 (26A434), arm64 with Node v24.22.2. The App `0.14.0` came from build checkout `24dbfc89b83bd7d9d6e49b755108ea12014c5fc6`. The collector checkout was `af8aa04ef05d0204a759cd437f65e750f60105a2`.

The 41 × 140 direct-local pane used an isolated profile with persistence off. The journal retains exact writes, raw DA replies, native window identities and ordered hover samples, private corroboration, screenshot hashes and raw Vision lines with bounds. The [manifest](manifest.json) pins the archive. Source policy `readonly-owned-source-graph-v78` and receipt schema 1 bind the run, settings, App and native reader identities, kernel terminal mode restoration and verified fixture cleanup. The archive also includes the desktop lease release. [All 69 rows](report.json).

[Retained attempt 868b8bcbd9821cc78c5f1531a40e569d](attempt-1.zip). The first native screenshot showed the startup tooltip. Cold accurate text recognition exceeded the per-request budget before fixture setup.

[Retained attempt 5009df62eb1736797c2bcacdfb59d450](attempt-2.zip). The cursor moved during the launch hover and the collector refused the reading before fixture setup.

[Retained attempt e6630120b60be18dea2f13927a83a272](attempt-3.zip). The native helper build exceeded the per-request budget before the launch reading.

[Retained attempt a904f10c15c0712bc54da5573fbc6222](attempt-4.zip). The pointer was outside the required departure point and the collector refused the launch reading.

[Retained attempt f781e7f67436d88e3344b837004d9963](attempt-5.zip). An intervening pointer movement invalidated the launch hover before fixture setup.

[Retained attempt 4d0bcbd58277f4f68af6ed6865b999b0](attempt-6.zip). The retained pixels showed the full fixture path. Fast Vision transcription misread slashes, so the collector refused to begin the attempt.

[Retained attempt f87365f78fedbf82f873121046133637](attempt-7.zip). The captured tooltip pixels were retained. The App moved between the initial window lookup and hover, so the collector rejected the launch reading.

[Retained attempt 3af03447e94a9ed716cf07acaf4e664a](attempt-8.zip). The desktop bounds changed between the initial lookup and mouse departure. The collector kept the launch pixels and rejected the stale anchor.

[Retained attempt 006f1fa42e3cd0e7e7b640fd8d2ba38b](attempt-9.zip). All OSC 7 App controls and restoration passed. The SonarCloud fixes changed the collector and fixture path, so this run is retained as superseded.

Rederive the report with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --osc7-cwd
```

Build the App at `24dbfc89b83bd7d9d6e49b755108ea12014c5fc6` and launch the collector from `af8aa04ef05d0204a759cd437f65e750f60105a2` on an awake macOS desktop with screen capture and native pointer event access. The launcher acquires and releases the repository desktop lease.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope modern-osc7-cwd
```

On macOS, independently transcribe an extracted native screenshot with the retained Swift source.

```sh
swiftc -o ./observe-tab-tooltip sources/modern-osc7-cwd-native.swift
./observe-tab-tooltip --ocr tooltip-target-b.png
```
