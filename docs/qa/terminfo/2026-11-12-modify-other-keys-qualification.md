# modifyOtherKeys qualification: generated-input routing and encoding (2026-11-13)

Qualifies exactly one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`input.modify-other-keys`, with levels 1 and 2 as subcases of the one benchmark id (level 3 is out of scope and measured only as an ignored
transition). Unlike the earlier terminfo batches this one pairs the pinned measurement with two first-time capabilities: a new generated-input
fixture (the AgentHost can now inject ordinary modified-key events with their paired backend text through the production App input path) and a
newly verified encoder surface (Ntilde's `CSI 27;m;code~` modifyOtherKeys encoding, which existed but had never been exercised end to end from
a measured event). Ntilde does not advertise `modifyOtherKeys` over XTGETTCAP, and the pinned criterion reads only the capability declaration,
so the pinned verdict is inconclusive by design.

## Result

| Case | Pinned (termless) | Effect contract | Experiments | Stock grade |
|---|---|---|---|---|
| `input.modify-other-keys` | **inconclusive by design** (the criterion validates no generated event) | **supported** (`ntilde-modify-other-keys-effect-v1`) | 58 events (46 required / 9 controls / 3 labels), all matched | inconclusive |

The pinned probe feeds nothing and reads only `ctx.capabilities.extensions.has("modifyOtherKeys")`; the child answered the XTGETTCAP request for the
name with its bare refusal (`DCS 0+r ST`), so the served declaration is `declaredModifyOtherKeys: false` and the pinned observation stays
`insufficient-evidence`. The divergence (`pinned-inconclusive-by-design`) is recorded and the published conclusion is the pinned verdict. The stock
collector, re-collected in the same owned run, still grades the case inconclusive (167 inconclusive / 87 supported / 1 unsupported,
`suiteComplete=false`) — this qualification raises no stock score.

## What the App did (disposable direct-local pane, 40×140)

The suite declared 118 Avalonia input events (the routed-input v3 benchmark plan). Every case is self-contained: it sets its own modifyOtherKeys level
in its own setup because the disposable child shell re-signals a fresh prompt (OSC 133) between events and the parser's documented transient-reset
policy resets the mode. Before every dispatch the harness proves the negotiated level by reading the child's `XTQMODKEYS` reply
(`CSI ? 4 m` → `CSI > 4 ; N m`) through the same raw stdin fence that captures the event bytes; a control whose reply is not the exact expected
sequence fails the case. The generated events flow through the production `MainWindow` input path (CommandAssist interceptor first, kitty-keyboard
precedence honored, the active screen's level driving the encoder); the child's received bytes come from an independent raw stdin capture, and every
expected byte string comes from an independent literal oracle — the production encoder is never asked what it would do.

Selected exact bytes the child received (every one of the 118 events matched its oracle; the table shows the shape):

| Case | Level | Bytes the child received |
|---|---|---|
| `l1.ctrl.a.alias` | 1 | `01` (the X11 C0 alias survives) |
| `l1.ctrl.leftbracket.alias` | 1 | `1b` |
| `l1.ctrl.digit2.alias` | 1 | `00` |
| `l1.ctrl.slash.alias` | 1 | `1f` |
| `l1.alt.a.legacy` | 1 | `1b 61` (ESC prefix) |
| `l1.enter.shift` | 1 | `CSI 27;2;13~` |
| `l1.tab.shift.backtab` | 1 | `CSI Z` (back-tab stays back-tab) |
| `l1.ctrl.digit1` | 1 | `CSI 27;5;49~` (no alias exists) |
| `l2.ctrl.a` | 2 | `CSI 27;5;97~` |
| `l2.ctrlshift.a` | 2 | `CSI 27;6;65~` (Shift applies to the US key) |
| `l2.alt.a` | 2 | `CSI 27;3;97~` |
| `l2.ctrl.leftbracket.alias` | 2 | `CSI 27;5;91~` |
| `l2.enter.alt` | 2 | `CSI 27;3;13~` |
| `l2.escape.ctrl` | 2 | `CSI 27;5;27~` |
| `l2.backspace.ctrl.legacy` | 2 | `7f` (the one L2 C0 exception, deliberate) |
| `l2.space.shift.withText` | 2 | `CSI 27;2;32~` — with the backend text ` `: the duplicate text suppression kept exactly one copy at the child |
| `l2.shift.a.withText` | 2 | `41` with text `A`: the paired key-down/backend-text event delivered `A` once |
| `l2.keyup.ctrl.a` | 2 | *(no bytes)* — key-up dispatches and encodes nothing |
| `trans.set1.from0 → trans.reset.form` | 1→0 | `CSI 27;5;49~` then `01`: the documented reset forms restore legacy encoding |
| `trans.l3.ignored` | 3 (requested) | `CSI 27;5;97~` — the parser ignores level 3 and keeps level 2's encoding |
| `kitty.pop.restores` | 2 under 1 pushed | `CSI 27;5;97~`: kitty flags win while pushed; popping restores the modifyOtherKeys level |

The disabled-mode controls re-collected the legacy bytes (Ctrl+A `01`, Alt+A `1b 61`, Shift+Enter `0d`, Shift+Tab `CSI Z`, Shift+Backspace `7f`,
Ctrl+Escape `1b`), the Meta and Ctrl+Alt/AltGr key-downs stayed deliberately unhandled with no bytes, and the finish frame answers every mode query at
its default with `CSI > 4 ; 0 m` — the negotiated state is restored after the suite. The three label experiments (declaration, unhandled text, unhandled
label) assert refusal and silence rather than bytes.

## Identities

App 0.13.0 (operator supplied) built from `2091d3d6` sources (arm64 Release, executable SHA-256 `f5814e51…9bf8`; the restack merged no production
source changes, so the binary is bit-identical to the pre-restack build); scripts at the restacked head `3519ad1a`; source policy
`readonly-owned-source-graph-v50` (336 captured files, adding the six modifyOtherKeys measurement modules to the XTGETTCAP layer); receipt schema 56;
inventory v57 (106 benchmark ids / 108 claims); Node v24.22.2; owned run `3eb9b7ed6086a62767d54104c014e48e`; measured 2026-11-13T12:27:46Z on
macOS 27.0.1. The operator-supplied build-source label is not binary-to-source attestation. Predecessors reconfirmed in the same run: the routed-input
layer's three cases stay supported, the query suite reproduces its frozen 33 matched / 7 mismatched, the static-image suite its frozen 21/1/1, and the
OSC title, insert/replace and XTGETTCAP layers rederive their published verdicts (2+1 supported, 1 supported, 1 inconclusive). Stock counts are
re-collected in that run (not carried forward) and unchanged.

Attempts, all retained with their actual reasons: attempt 1 (`owned-hnT45w`, PR-era) aborted at the case 68 control because the child shell's fresh
OSC 133 prompt resets modifyOtherKeys between events — the plan was rewritten so every case sets its own level (inputs changed once, deliberately).
After the restack onto the merged XTGETTCAP main, five runs collected every suite cleanly but died at the finalizer with the bounded primary
sha256 `6a81690a…` ("Invalid or incomplete owned routed-input evidence."): the earlier fixup commits had dropped `retainRoutedInputEvidence`'s
`inputSuiteId` parameter and `inputPlan` option, so the parent re-derived with the frozen v2 plan against the 118-event v3 journal
(`owned-maGwRw` and `owned-KmeUtD` are retained unmodified; three earlier directories were removed after the cause was identified). One relaunch
aborted instantly because `terminfo-owned.mjs` still wrote the stale v49 snapshot-manifest policy, and one (`owned-b32X8a`) reached receipt assembly
and aborted on a missing `capturedV57SourceFiles` import in `metadata-receipt.mjs`. All are measurement-machinery regressions, not App or contract
changes; `owned-fB8DJc` is the completion, and every published digest is bound by its cell receipt (schema 56).

## Limits

Generated App events are not physical keyboard input; the measurement proves routing and exact child-received bytes for declared Avalonia events in a
disposable direct-local pane. App admission is not child receipt, and direct-local delivery does not qualify SSH, mux or Windows terminals. Levels 1 and
2 only: level 3 is measured solely as an ignored request, `formatOtherKeys=1` and the Kitty expansion of these events are out of scope (kitty appears
only as a precedence control in a verified neutral state), and no IME, layout, physical-key, stock-score or SSH/mux/Windows claim is made.
`input.modify-other-keys-3` remains a separate pinned id and stays inconclusive with evidence `none`.
