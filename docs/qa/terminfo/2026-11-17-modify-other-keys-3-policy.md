# modifyOtherKeys level 3: unsupported implementation, deferred qualification

Historical audit. The [implementation and owned measurement](2026-11-27-modify-other-keys-3/report.md) supersede the deferred implementation policy. The original pinned verdict remains inconclusive.

Issue [#1002](https://github.com/benyblack/ntilde/issues/1002) is disposed as an explicit
unsupported/deferred policy for `input.modify-other-keys-3`. This is a source audit and
regression check, not a new owned runtime measurement or graded effect contract.
Audited base: `f50b259e361234c3a661d8c7b51be21d442671ad`.

## Exact pinned question

`terminfo.dev@4.0.0`, bundle SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`,
probeHash `16a690f34c0e`, registers this ID in `../probe-defs/src/input.ts`
(the bundled source label). Both paths use `absentEventNote`.

| Path | Inputs and actual criterion | Unchanged result |
| --- | --- | --- |
| `termless(ctx)` | Reads only `ctx.capabilities.extensions.has("modifyOtherKeys")` into `declaredModifyOtherKeys`; no setup, generated event, level negotiation or encoded bytes | `pass:false`, `inconclusive`, `insufficient-evidence`, evidence `legacy`, for either declaration value |
| `term(ctx)` | Resolves immediately; never accesses the terminal context | `pass:false`, `inconclusive`, `insufficient-evidence`, evidence `none` |

Neither path can demonstrate support or failure. In particular, the shared capability
name cannot distinguish level 3 from levels 1/2. The real-bundle policy test executes
both original functions after digest validation and fresh locked dependency installation;
unexpected context accesses fail. This executes pinned code with controlled metadata,
not with a newly observed App declaration. No new fixture or observer is admitted.

## Authoritative semantics and decision

The [xterm manual](https://invisible-island.net/xterm/manpage/xterm.html#VT100-Widget-Resources:modifyOtherKeys),
read on 2026-11-17, defines level 3 as extending reporting to unmodified keys.
It distinguishes ordinary keys from the special cursor, keypad, function and labeled
control-key categories. Its `formatOtherKeys` resource separately chooses function-key-27
or CSI-u framing; choosing CSI-u is not the definition of level 3. The
[xterm control-sequence specification](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
defines resource 4 in XTMODKEYS (`CSI > 4 ; Pv m`) and XTQMODKEYS (`CSI ? 4 m`).
The specification establishes a real distinction from level 2; level 3 is not undefined.

Ntilde deliberately supports levels 0, 1 and 2 only. `ModifyOtherKeysState.SetLevel(3)`
rejects the value without changing either screen's stored level; the query reports the
effective supported level. The App encoder rejects level 3 and leaves unmodified text
on its existing backend text route. Raising the ceiling or mapping 3 to 2 would advertise
semantics that this route does not implement. The policy therefore stays unsupported,
with implementation and qualification deferred rather than inventing a narrower contract.

Before support can be added, specify an independent event-to-byte table for ordinary
unmodified and modified keys, the selected framing and code-point/layout policy, and
the boundary with special keys, Kitty precedence, IME/dead keys and standalone text.
Declare paired key-down/backend-text suppression so a key produces exactly one report
without losing subsequent text. Then collect production App generated events and
independent child stdin bytes, with level-0/1/2 positive delivery controls, negative
no-byte controls behind completed barriers, unaffected state and verified restoration.
Direct encoder tests or a successful query alone cannot admit that effect contract.

## Existing evidence and limits

The frozen [levels-1/2 publication](2026-11-12-modify-other-keys-qualification.md)
retains run `3eb9b7ed6086a62767d54104c014e48e`, App source `2091d3d6`, scripts
`3519ad1a`, operator-supplied App version 0.13.0, source policy v50 and receipt 56.
Its `trans.l3.ignored` experiment requested level 3 over level 2, observed level 2 via
XTQMODKEYS and received `CSI 27;5;97~` for Ctrl+A. That is only an ignored-request
transition control, not level-3 encoding. The publication retains its delivery controls,
restoration and cleanup under its original identities. It remains independently
rederivable and is cited as historical context, without asserting equivalence to the
current App or relabeling its evidence as a fresh level-3 run.

This audit changes no measured App/collector input and takes no desktop lease. It has
no new App binary, run identity, runtime positive/negative controls or cleanup receipt.
The added VT regression tests rejection from each supported level on both screens,
unchanged query replies, positive supported transitions and an explicit reset to zero.
These are deterministic policy checks, not runtime input-delivery measurements.

## Reproduction and accounting

Use Node 24+ for the real pinned bundle (required mode cannot skip):

```sh
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/modify-other-keys-3-policy.test.mjs
node --test scripts/terminfo/modify-other-keys-publication.test.mjs
node scripts/terminfo-inventory.mjs --check
scripts/build.sh test tests/Ntilde.VT.Tests --filter FullyQualifiedName~ModifyOtherKeysProtocolTests
```

The pinned result stays inconclusive; no separately graded effect or divergence is
created. Stock, frozen Core/Modern requirements and public common-harness scores are
unchanged. The ID remains outside the registered graded inventory: delta zero benchmark
IDs, zero claims and zero support points. At the audited base the generated inventory
contains 144 unique IDs and 146 claims. Historical publications and receipt/source-policy
versions remain unchanged. A future implementation needs a new measured contract and run.
