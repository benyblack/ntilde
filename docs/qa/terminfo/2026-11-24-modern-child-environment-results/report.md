# Ntilde Modern child environment assessment

**59 pass / 0 fail / 10 inconclusive across 69 frozen requirements.**

The [OSC 7 predecessor](../2026-11-24-modern-osc7-cwd-results/report.md) had 56 pass, 0 fail and 13 inconclusive. The four environment rows are manual. The [grading contract](../modern-child-environment.md) records the frozen catalogue descriptions and the controls used to interpret them.

| Variable | Clean App input | Clean child | Inherited App input | Inherited child | Modern verdict |
| --- | --- | --- | --- | --- | --- |
| `COLORTERM` | absent | <code>truecolor</code> | <code>ntilde-qa-inherited-colorterm</code> | <code>truecolor</code> | pass |
| `TERM_PROGRAM` | absent | <code>Ntilde</code> | <code>ntilde-qa-inherited-term-program</code> | <code>Ntilde</code> | pass |
| `NO_COLOR` | absent | absent | <code>ntilde-qa-user-no-color</code> | <code>ntilde-qa-user-no-color</code> | pass |
| `VTE_VERSION` | absent | absent | absent | absent | inconclusive |

The first two rows assess Ntilde’s truecolor hint and terminal identity. Their clean launches measure provision, and their sentinel launches measure override. The separately audited `NO_COLOR` row measures preservation of a user setting. Each program decides whether to suppress its own ANSI colors.

The Ntilde App uses Avalonia and its own native PTY integration. The clean child had no VTE_VERSION. The frozen reporting convention retains this VTE-specific row in the denominator.

## Evidence and reproduction

The sidecar was built from App checkout `1aa66dc3f3e932f083ae3c1bde950e47a484c726` with version `0.14.0`. The collector used checkout `e75fa8407d78a6a609a7c3c431e8b0d78176930f`, Node v24.22.2 and macOS 27.0.1 (26A434) arm64. The observation is scoped to the disposable macOS direct-local App profile.

- [clean launch](clean-run.zip) `6a5fbc7082e21dc416fee7267a12de96` bound App PID 7780, PTY child PID 7805, and pane `56af4b9b-74c0-46da-9a39-1bf3eae2b330`. The desktop lease released at 2026-11-24T19:54:28.642Z after verified cleanup.
- [inherited launch](inherited-run.zip) `ace366ce2e88cd6ef840d58da91ddc4e` bound App PID 7963, PTY child PID 7969, and pane `563637b8-36ba-44a5-92a1-934b09bc44dc`. The desktop lease released at 2026-11-24T19:54:42.954Z after verified cleanup.

Each archive retains the four controlled parent inputs, direct Node profile, raw child JSON, hashes of the App executable, App assembly, managed PTY assembly and native PTY library, process and pane proofs, source graph, restored kernel terminal mode, and lease log. Source policy `readonly-owned-source-graph-v79` and receipt schema 1 bind the observations. [Manifest](manifest.json) · [All 69 rows](report.json).

Rederive from the retained bytes with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --child-environment
```

To collect fresh controls, build the recorded App checkout and run each launch from the recorded collector checkout on macOS. The launcher acquires and releases the desktop lease for each run.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
NTILDE_TERMINFO_ENVIRONMENT_CONTROL=clean node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope modern-child-environment
NTILDE_TERMINFO_ENVIRONMENT_CONTROL=inherited node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope modern-child-environment
```
