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

The sidecar was built from App checkout `1aa66dc3f3e932f083ae3c1bde950e47a484c726` with version `0.14.0`. The collector used checkout `c286a0bbcec83145640ae28b40c903edbb5673b3`, Node v24.22.2 and macOS 27.0.1 (26A434) arm64. The observation is scoped to the disposable macOS direct-local App profile.

- [clean launch](clean-run.zip) `d83240b5d5acc1ee79031383bd97e111` bound App PID 22352, PTY child PID 22382, and pane `41632ef7-bd5c-4c67-899b-782bf1ef84bd`. The desktop lease released at 2026-11-24T20:24:34.561Z after verified cleanup.
- [inherited launch](inherited-run.zip) `02c201a8c6dda4c7b6dd59eb6cdec59f` bound App PID 22417, PTY child PID 22437, and pane `5aac54ea-bbed-4652-9e6b-e6242c7165ea`. The desktop lease released at 2026-11-24T20:24:36.156Z after verified cleanup.

Each archive retains the four controlled parent inputs, direct Node profile, raw child JSON, hashes of the App executable, App assembly, managed PTY assembly and native PTY library, process and pane proofs, source graph, restored kernel terminal mode, and lease log. Source policy `readonly-owned-source-graph-v79` and receipt schema 1 bind the observations. [Manifest](manifest.json) · [All 69 rows](report.json).

The first pair used collector `e75fa8407d78a6a609a7c3c431e8b0d78176930f`. Windows CI caught host-dependent joining of recorded macOS paths during archive replay. The App observations repeated after the verifier fix. [First clean launch](superseded-clean-run.zip) · [First inherited launch](superseded-inherited-run.zip).

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
