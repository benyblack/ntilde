# Shell-integration aggregates: `extensions.osc-633-vscode` and `extensions.semantic-prompts`

This publication qualifies the two pinned `terminfo.dev@4.0.0` **aggregate**
cases (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`,
probeHash `16a690f34c0e`) — the first aggregate ids any batch has qualified, and
one of them deliberately stays **inconclusive**. **No production code changed
and no collection ran**: the batch is a retrospective projection over two
already admitted, separately receipted marker publications, and it adds its own
suite identity (`ntilde-owned-shell-integration-aggregates-projection-v1`)
without rewriting any carrier grade.

## What the pinned aggregate probes can say

Both aggregate probes are declared-capability probes with no fixture bytes and
no readback. Their termless function reports
`{"declared": ctx.capabilities.semanticPrompts === true}` and returns
`pass:false`, inconclusive, `insufficient-evidence`, evidence `legacy`; their
terminal function never writes a byte and returns inconclusive with evidence
`none`. The registrations, quoted from the retained bundle:

- `extensions.semantic-prompts` — "Declared semantic-prompt capability does not
  establish shell prompt integration" / "No shell prompt integration metadata
  or behavior readback for OSC 133".
- `extensions.osc-633-vscode` — "Declared semantic-prompt capability does not
  establish VS Code OSC 633 shell integration" / "No VS Code shell integration
  metadata or behavior readback for OSC 633".

A declared capability is not an observation: neither aggregate can ever produce
a pinned supported verdict. The exact pinned registrations are extracted from
the [retained compressed bundle](2026-11-16-shell-integration-aggregates/terminfo-dev-4.0.0.bundle.mjs.gz)
by SHA-256-checked source spans (`d8205e6e…f4108a5c` for the OSC 633 aggregate,
`05dade3d…3855fb8` for the semantic-prompts aggregate) and replayed in a
sandbox whose terminal context throws on any read — the pinned terminal code
reads nothing, which the replay proves rather than assumes. Each aggregate is
replayed under **both** declared values, `false` (what the stock collector
context declared) and `true` (what an owned observation API that reports the
admitted lifecycle metadata would declare); both resolve to the same
inconclusive observation, and no declared value decides a verdict. A doctored
bundle copy is refused before any replay.

## Aggregate evidence sets

Each aggregate effect is defined by the exact lifecycle/metadata evidence it
needs, named case by case. Dependence is directional: the marker publications
do not claim these aggregates (each carrier's scope explicitly excludes them),
and this suite does not turn their sum into an upstream supported verdict.

- **`extensions.semantic-prompts`** requires the four OSC 133 lifecycle
  publications `extensions.osc133-a`…`-d` (prompt start, prompt end/command
  start, pre-execution, completion) — all admitted **supported** by
  [`2026-11-11-osc133/`](2026-11-11-osc133-qualification.md), effect contract
  `ntilde-osc133-shell-lifecycle-effect-v1`, run `650b9b8230a59a84b0e506f087594395`.
  OSC 133 P prompt metadata is **excluded with a reason**: it is the separately
  owned audit batch (#985), exactly the way the lifecycle batch excluded P from
  its contract. If a future authoritative contract requires P, this evidence
  set grows. Effect contract `ntilde-semantic-prompts-aggregate-effect-v1`:
  **supported**.
- **`extensions.osc-633-vscode`** requires the four OSC 633 lifecycle
  publications `extensions.osc633-a`…`-d` — all admitted **supported** by
  [`2026-11-16-osc633-implementation/`](2026-11-16-osc633-implementation-qualification.md),
  effect contract `ntilde-osc633-shell-lifecycle-effect-v1`, run
  `323882a7d7b0e3c86ce4152fa54e6aa3` — **and** the `extensions.osc633-e`
  command-line and `extensions.osc633-p` property records of the same
  [authoritative VS Code contract](https://code.visualstudio.com/docs/terminal/shell-integration#_supported-escape-sequences).
  E and P have **no implemented, qualified publication yet**; they are recorded
  as required-and-pending, owned by #983. Missing evidence is not a
  demonstrated terminal failure: the aggregate effect contract
  `ntilde-osc633-vscode-aggregate-effect-v1` stays **inconclusive** until #983
  publishes the records, rather than absorbing their absence into a
  lifecycle-only verdict. The OSC 633 F–I continuation records are an
  observation-only boundary: terminfo.dev registers no atomic case for them.

## Result

| Case | Pinned | Effect contract | Stock grade |
|---|---|---|---|
| `extensions.osc-633-vscode` | inconclusive (by design) | **inconclusive** — E/P pending (#983) | inconclusive (unchanged) |
| `extensions.semantic-prompts` | inconclusive (by design) | **supported** | inconclusive (unchanged) |

The semantic-prompts divergence (pinned inconclusive, effect supported) is
recorded as `pinned-inconclusive-by-design`, never absorbed. The OSC 633
aggregate's pinned and effect verdicts agree at inconclusive, so it records no
divergence — the pending records, not a measured absence, keep it there. The
frozen stock baseline keeps both ids at inconclusive; no stock grade, Core
percentage (frozen Core remains 80/80), Modern result or common-harness count
moves.

## Carrier binding and equivalence

Evidence reuse is explicit: the projection binds each carrier by the SHA-256 of
its committed `summary.json` (receipts 45 and 69 respectively), pins the OSC 633
carrier's `owned-run.zip` archive digest, and re-derives both carriers' four
admitted claims from their retained reports with the required effect outcome
and the same effect-contract identity per case. The suite-level receipt binding
is the primary (OSC 633) carrier's; the OSC 133 carrier is bound in
`projection.carriers` and per case. The two dialect contracts share one lineage
(the OSC 633 grading adapter derives from the OSC 133 grading adapter over the
same upstream bundle), while their contract ids stay distinct per dialect —
OSC 133 evidence is separately attributed and never folded into the OSC 633
aggregate. The [frozen historical OSC 633 negative](2026-11-16-osc633-qualification.md)
remains untouched beside the admitted remeasurement.

## Not claimed

No upstream supported verdict: the sum of the marker publications is not
converted into one, and the aggregate effect contracts name that limit in their
own attribution. No real shell, Bash/Zsh/Fish/PowerShell emission, SSH or mux
delivery, Windows or Linux behavior, command exit status, `commandFinished`
event readback, command-assist, hidden-pane or alternate-screen claim. No new
desktop collection, no stock re-measurement, and no score change of any kind.

## Identities

The projection runs no collection of its own. Its top-level identity fields,
context, receipt and stock counts are the primary (OSC 633) carrier run's
(`323882a7d7b0e3c86ce4152fa54e6aa3`, App version `0.14.0`, App and scripts
revision `9c814b13…`, executable SHA-256 `f5814e51…1fb9bf8`, assembly SHA-256
`a2e858f0…1914fa4`, source policy `readonly-owned-source-graph-v62`) and are
labeled as reused for binding only; the OSC 133 carrier run
(`650b9b8230a59a84b0e506f087594395`, App `0.13.0`, revision `45f0e09c…`,
receipt schema 45, policy v39) is bound beside it. The projection claims no
stock run id of its own. Source identity and binary hashes do not by themselves
attest a complete source-to-binary build.

Reproduce the projection, carrier bindings, pinned replay and tamper refusals
with:

```sh
node --test scripts/terminfo/shell-integration-aggregates-publication.test.mjs
node scripts/terminfo-inventory.mjs --check --rederive
```

The [summary](2026-11-16-shell-integration-aggregates/summary.json) and
[report](2026-11-16-shell-integration-aggregates/report.json) retain the full
bindings. Until #983 lands, `extensions.osc-633-vscode` remains measured-and-
inconclusive — the inventory's first `effectMeasuredOther` id — and this batch
closes with exactly that state.
