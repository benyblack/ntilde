# Bounded owned exact-query observations (#771)

The optional owned driver adds a sixteenth suite under receipt schema 11,
`readonly-owned-source-graph-v4` and the fixed 73-file `capturedV11SourceFiles`
map. The query experiment is `owned-exact-query-paired-raw-state-v1`.
This implementation has portable synthetic tests; it adds no actual runtime
qualification until a separately announced, source-pinned collection is retained.
Frozen v8/v9/v10 maps, published bytes and receipts remain independently reproducible.

## Exact transactions and exposed state

The [merged pure model](query-witness-model.md) supplies 40 exact contracts:
18 Q07 and 22 Q12 spellings, each executed once. Four separate controls bind a
private nonce-bearing alternate screen, prepare absolute margins 2..6 with
origin mode enabled, restore full margins/origin/cursor, and finish with RIS
plus restoration of the independently captured original cursor-visible boolean.
All 44 source-selected packets are at most 64 seven-bit bytes. The worker and
journals cannot replace a packet or its expected response. No additional query
is appended to an original request. The exact negative variants remain separate;
a response from a different spelling/family cannot qualify them.

Two ordinary read-only `OwnedCellObserver` instances have exact budgets 48 and
42, below the existing 64 limit. Before and after every packet they capture
complete declared lines, attributes, stored ARGB/underline/overline, cursor
style/default/blink/visibility, exposed buffer modes, absolute margins and
history. Queries must leave these facts unchanged. Control transitions are
derived from the independently captured pristine baseline; final restoration
is exact for both initially visible and hidden cursors. Nondefault margins must
reply `2;6` despite origin mode.

`BufferStateDto` does not expose reverse-video memory. The report explicitly
retains `unobservedState: ["reverseVideo"]`; it makes no reverse-video memory,
preservation or appearance claim. It also observes no hidden parser/title
stacks, private image registry, physical geometry or live desktop cadence.
Neither a CPR/DECRQM reply nor the raw ordering fence replaces independent
App-state readback. There is no App/VT API or renderer change here.

## Ownership, bytes and qualification

The real coordinator freshly verifies the recorded App/worker UID, executable,
lineage, unique command markers, private profile, singleton local pane/parser,
PTY and kernel `stty -opost` facts around each state capture. Those reads share
the existing 4-second request budget. A shaped callback alone is not attestation;
the synthetic fixture is never real process or terminal evidence.

Query collection follows both healthy, sealed and disposed input/image raw
readers. The parent derives all 110 prior challenge pairs from the exact retained
80 input and 30 image captures. Both collector and independent report builder
exclude those pairs and every newly used pair. Each of the 44 writes gets a
new unpredictable paired challenge after the write. The exclusive raw reader
retains exact lowercase hex, byte counts, replies, both nonces, partial failures
and its final disposed seal; response bytes are bounded to 512 and its existing
deadline remains 4500 ms. No text decoding or elapsed-silence inference is used.

Observations retain `expectedEvidence`, `observedEvidence`, `expectedHex`,
`receivedHex` and `contractMatched`. Receipt counts are **matched/mismatched
contracts**, never feature-support scores. A matched valid rejection remains
negative protocol evidence, and matched empty bytes remain observed absence.
An unexpected, missing, duplicated or trailing response can be a completed
semantic mismatch when every independent control and restoration passes.
Malformed ownership, state, journals, ordering, seals or restoration prevents
completion. No cleanup packet is sent after ownership is lost; the parent
retains the first failure and performs verified process/fixture teardown.

If an earlier validated suite has a source-known initial typed refusal, the
query suite may record only a pre-fixture `queryPrerequisiteUnavailable` binding.
It retains the exact unavailable prerequisite bindings/reasons, planned 44
writes/40 contracts and observed zero writes/frames/contracts. There is no query
initial sample, raw reader, packet or fabricated observation. An unhealthy,
partial or failed prefix, or any started-query failure, cannot use this path.
Fully eligible prefixes still require all 110 earlier challenges.

## Retention and receipt boundary

The parent writes `query-initial.json`, `query-actions.jsonl`,
`query-snapshots.jsonl` and `query-observations.json`; initial refusals use
`query-unavailable.json`. Failures retain `query-failed-observation.json` and/or
`query-raw-failure.json`, including the original partial facts. A provisional
binding cannot become schema 11 before all 15 existing suites, actual process
exit, fixture removal and captured-source removal have been verified.

The complete finalizer rederives the query report from the original journals,
exact source plan, validated prefix challenges and parent process facts. All
six executed query dependencies are captured and checked in the fixed source
closure before measured imports. Any retained query declaration, file or import
requires the v11 suffix; coherent stripping cannot acquire an older receipt.
Historical receipts keep their original 51/52, 60 and 67-file scopes. Active
inventory growth never changes a frozen map or regrades a published observation.

## Verification

The portable CI lane runs `query-capture.test.mjs`, `query-receipt.test.mjs` and
`query-integration.test.mjs` alongside the existing pure query tests. They use
the actual raw reader and bounded observers with synthetic App/PTY/process facts,
then a synthetic query suffix after the unchanged real 15-suite v10 prefix.
They cover exact packets, independent contract mismatches, margins, both cursor
baselines, transport envelopes, state mutation, nonce reuse, reader handoff,
malformed/partial/failure journals, coherent result promotion/downgrade, initial
refusal boundaries, source closure and complete 16-suite finalization. Frozen
publication guards also rederive real v8/v9/v10 archives under current and future
active policies. Synthetic execution is separate from actual runtime evidence.
