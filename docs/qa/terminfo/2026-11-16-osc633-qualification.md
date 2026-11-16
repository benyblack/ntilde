# VS Code OSC 633 lifecycle markers A–D (2026-11-16)

This qualification covers the exact `terminfo.dev@4.0.0` cases `extensions.osc633-a` through `extensions.osc633-d`, using pinned bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6` (probe hash `16a690f34c0e`). It adds observation and qualification code; it does not change Ntilde's terminal implementation.

| Case | Lifecycle transition | Pinned termless / terminal | Separate owned effect |
|---|---|---|---|
| `extensions.osc633-a` | prompt starts | inconclusive / inconclusive | unsupported |
| `extensions.osc633-b` | prompt ends; command input starts | inconclusive / inconclusive | unsupported |
| `extensions.osc633-c` | command execution starts | inconclusive / inconclusive | unsupported |
| `extensions.osc633-d` | execution finishes, optionally with exit code | inconclusive / inconclusive | unsupported |

The unchanged pinned probes only establish marker consumption and return inconclusive by design, even when their screen/cursor criteria hold. The stock collector also records all four as inconclusive. The effect results come from the separately specified `ntilde-osc633-shell-lifecycle-effect-v1` contract; they are not upgraded pinned or stock verdicts. Each case retains its explicit pinned/effect divergence in [report.json](2026-11-16-osc633/report.json).

## Observed effect

The parent retained 63 paired screen, pane-metadata and session-status frames. Each experiment prepares a distinguishable prior state with OSC 133 and compares the target with ordinary output and an OSC 133 positive control. The stock-screen, prior-state, unmarked, positive-control and applicable wrong-marker gates all held. A and D can have overlapping observable effects, so neither is used as a wrong-marker gate for the other; each is recorded separately.

- A left the session running when the contract expected prompt state.
- B left input acceptance false and the command-start anchor unset when the contract expected input acceptance and an anchor.
- C left the session in prompt/input state when the contract expected running state.
- D left the session running and retained command/output anchors when the contract expected prompt state and cleared anchors.

The ST-terminated variants likewise matched the unmarked baseline rather than the lifecycle effect. D without an exit-code parameter and D with `7` also matched the baseline; these are supplemental observations, not an independent verification of an exit-code event. Session `exitCode` is the child process exit code, not a command exit code. No claim is made about real shell emission, remote or multiplexed paths, OSC 633 E/P, command-assist behavior, other platforms, or command-finished event payloads. See [contract and observer details](osc633.md).

## Collection and retained evidence

The fresh owned run `ac59f42044dd69e06377ca6719983f76`, measured 2026-11-16 on macOS 27.0.1 with a disposable direct-local Ntilde 0.14.0 sidecar, produced a completed schema 68 receipt and verified cleanup. The stock run `54aa17e2382bec4a26c27c370a89b23f` reports 93 supported, 1 unsupported and 161 inconclusive, with `suiteComplete=false`; this is separate from the effect contract and does not alter the frozen stock baseline or Core requirements.

Measured App source revision `b9d7c6273683d8546b874781f605817345e23ce1`; executable SHA-256 `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`; assembly SHA-256 `4705c332252460798ffbbaaa6df5531f5cec5525b8efe0ba5f986c4f02d2800a`. Measured scripts revision `f8f58f817e232242a063daac95f0526f94535ef8`, source policy `readonly-owned-source-graph-v61`, manifest SHA-256 `7f0e26d84241103447fad11aeec5bf5bfd2bef0f5973629cb2fd4eabc6337cfc`; OSC 633 adapter bundle SHA-256 `c2ce46a237b7b73fcb0ce5f79241990b8b4d4381df692972f5cbd0ac16bd54da`. Main subsequently merged a separate truecolor replay and inventory update; it changed neither the measured App nor the owned source graph.

The first, unscoped [attempt](2026-11-16-osc633/superseded/attempt-1-deadline.md) exhausted the unchanged five-minute deadline and has no valid receipt or admitted verdict. The second [attempt](2026-11-16-osc633/superseded/attempt-2-scoped.md) completed and reached the same outcomes before the default-selection review fix. The final `--scope osc633` run retains that deadline and all request, ownership and cleanup checks while omitting only the OSC 9 and OSC 777/99 suites. Their evidence remains in the distinct full predecessor run `55f0ba462cd3cc6fbc315a0e5fad5126`, whose archive is nested in the fresh archive and independently revalidated both before collection and during receipt issuance. This is combined historical and fresh evidence, not a fresh full-suite receipt. Two later default-selection validations stopped after unexpected input; their retained records are [first](2026-11-16-osc633/superseded/default-validation-interrupted.md) and [second](2026-11-16-osc633/superseded/default-validation-interrupted-2.md). Neither produced a receipt or an admitted verdict. A third [default-selection validation](2026-11-16-osc633/default-validation/owned-run.zip) completed as run `6d2c799d710b798c77ab75f76d9e3017` with schema 68 and verified cleanup. Its OSC 9 effects were supported for both cases (pinned paths inconclusive); OSC 777/99 effects were supported for both, with one stock-and-query witness. It expressly omits OSC 633 and is not the target measurement.

[Manifest](2026-11-16-osc633/manifest.json), [owned run archive](2026-11-16-osc633/owned-run.zip), [summary](2026-11-16-osc633/summary.json), and [report](2026-11-16-osc633/report.json) retain the identities, outcomes, source snapshot, raw collector data, journals, cleanup proof, receipt and predecessor. The publication test pins every listed archive, reissues the target and default-validation receipts including the pinned predecessor, checks the other attempts’ completion states, rebuilds the target report from the paired journal and checks the published projection:

```sh
node --test scripts/terminfo/osc633-publication.test.mjs
```

Collection uses Node 24+ and the repository's owned macOS runner:

```sh
node scripts/terminfo-owned.mjs --executable /absolute/path/to/uninstalled/Ntilde --app-version 0.14.0 --scope osc633
```

The App version is operator supplied. Reproduction requires the pinned collector, a matching Release sidecar, and an undisturbed desktop under the shared lease; later measurements are separate runs, not replacements for these retained bytes.
