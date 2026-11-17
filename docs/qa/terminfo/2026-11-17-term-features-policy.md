# TERM_FEATURES: no feature-reporting declaration, inapplicable pinned probe

Issue [#1003](https://github.com/benyblack/ntilde/issues/1003) is disposed as an explicit
inapplicable/deferred policy for `device.term-features`. This is a source and protocol audit
plus regression checks, not a new owned runtime measurement or graded effect contract.
Audited base: `f50b259e361234c3a661d8c7b51be21d442671ad`.

## Exact pinned question

`terminfo.dev@4.0.0`, bundle SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`,
probeHash `16a690f34c0e`, registers the ID in `../probe-defs/src/device.ts` (the bundled
source label) as `probe("device.term-features", null, term, "legacy")`: it has no termless
path at all, and its only executable code is the terminal-mode function.

| Path | Inputs and actual criterion | Unchanged result |
| --- | --- | --- |
| `termless` | Does not exist (registered `null`) | — |
| `term(ctx)` | Reads only `!!process.env.TERM_FEATURES` in the collector process; never touches `ctx` — no feed, no query, no capture; presence changes only the note | `pass:false`, `inconclusive`, `insufficient-evidence`, evidence `legacy`, for presence and absence alike |

Two structural facts make the terminal under test irrelevant to this probe. The variable is
read from the benchmark collector's own process environment. In terminfo.dev's native
deployment the collector runs inside the terminal it grades, so that environment is the
terminal's child environment; in Ntilde's owned stock baseline the collector parents a
disposable App, so the App cannot write it and the probe observes the operator's shell
environment instead. Either way, the pinned code can return only `pass:false` with evidence
`legacy`: a declaration can change the note, never the verdict. The real-bundle policy test
executes the unchanged `term` function after digest validation and a fresh locked dependency
installation, with the variable absent (negative control) and set to a realistic encoded
feature string (declaration control), and fails on any terminal-context access. This executes
pinned code with controlled inputs; no environment variable is fabricated for a benchmark
score and nothing is inferred from `TERM`.

## Authoritative protocol and ownership

The [iTerm2 feature-reporting specification](https://iterm2.com/feature-reporting), read on
2026-11-17, owns the `TERM_FEATURES` protocol; it is a modification of a proposal from the
now-defunct terminals-wg, and no other standards body defines the variable. A terminal
publishes its feature set through two mechanisms of one protocol: it sets
`TERM_FEATURES={FeatureString}` in its own children's environment, and it answers
`OSC 1337 ; Capabilities ST` with `OSC 1337 ; Capabilities = {FeatureString} ST`.
`{FeatureString}` concatenates a short alphabetic code per advertised feature (`T` 24BIT,
`Cw` CLIPBOARD_WRITABLE, `Sx` SIXEL, `F` FILE, `P` PROGRESS, …); an omitted code means the
feature is not advertised. A declaration is therefore an advertisement, not behavior: every
advertised feature names semantics (esctest cases or test files, where the spec notes them)
whose truthfulness is the declaring terminal's responsibility, and each advertised feature
would need its own owning evidence here. The protocol owner's request form also has no
version or capability negotiation distinct from the codes themselves.

## Ntilde's policy

Ntilde does not implement feature reporting and declares nothing. The native PTY layer builds
each direct pane child's environment by inheriting the user's environment and setting only
`TERM=xterm-256color`, `COLORTERM=truecolor` and `TERM_PROGRAM=Ntilde` (both the Windows env
block and the Unix `CommandBuilder` spawn path; the SSH options set `TERM` only). It never
sets or clears `TERM_FEATURES`. The VT answers only the implemented OSC 1337 forms —
`ReportCellSize`, `File=` inline images and the multipart transfer — and every other
`1337;` payload, including `Capabilities` in any spelling, is ignored without a reply
(regression-tested beside an answered `ReportCellSize` control). A user-exported
`TERM_FEATURES` that a child inherits is the user's declaration, not one Ntilde made or can
honor; it is not overridden, cleared or reported.

The disposition is therefore inapplicable/deferred rather than a measured unsupported
behavior: the pinned probe emits no request to answer and observes no Ntilde-made
declaration. Implementing the protocol later would need, at minimum: the iTerm2 `Capabilities`
reply contract with the exact `{FeatureString}` code table, a child-environment declaration
policy applied consistently across the PTY, SSH and mux spawn paths, owning evidence for
every advertised feature, and a new owned run. Setting the variable in the meantime cannot
produce a measured pass, and deriving a value from `TERM` would fabricate a declaration.

## Existing evidence and limits

The frozen stock baseline records `device.term-features` **inconclusive**
(`2026-11-09-stock-baseline.json`; triage `2026-11-02-inconclusive-triage.json`: outcome
`inconclusive`, reason `insufficient-evidence`, evidence `legacy`), and the device-query
qualification documents it as documented-only with no case
(`2026-11-15-device-queries-qualification.md`), whose publication stays independently
rederivable. This audit changes no measured App/collector input and takes no desktop lease.
It has no new App binary, run identity, runtime observation or cleanup receipt. The added
regressions are deterministic policy checks: the real-bundle replay of the pinned function
over both declaration states, the VT no-Capabilities-reply test, and the App test pinning the
native layer's absence of the variable. They are not desktop measurements.

## Reproduction and accounting

Use Node 24+ for the real pinned bundle (required mode cannot skip):

```sh
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/term-features-policy.test.mjs
node --test scripts/terminfo/devq-publication.test.mjs
node scripts/terminfo-inventory.mjs --check
scripts/build.sh test tests/Ntilde.VT.Tests --filter FullyQualifiedName~ITerm2CapabilitiesPolicyTests
scripts/build.sh test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PaneParserWiringTests&Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Lane!=PlatformBoot"
```

The pinned result stays inconclusive; no separately graded effect or divergence is created.
Stock, frozen Core/Modern requirements and public common-harness scores are unchanged. The ID
remains outside the registered graded inventory: delta zero benchmark IDs, zero claims and
zero support points. At the audited base the generated inventory contains 144 unique IDs and
146 claims, and `device.term-features` is none of them. Historical publications and
receipt/source-policy versions remain unchanged. A future implementation needs a new measured
contract and run.
