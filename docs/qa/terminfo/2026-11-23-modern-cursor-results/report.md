# Ntilde Modern assessment — four cursor requirements

**49 pass / 0 fail / 20 inconclusive across 69 frozen requirements.**

Combined assessment of the original Modern evidence and one new direct-local macOS cursor collection. Each verdict applies to its frozen criterion and named observation path. The results describe the measured settings and pane. They leave the historical stock grades, Core results and qualification inventory as separate records.

Owned application evidence mapped to the frozen catalogue. Where a stored-cell semantic predicate is selected it is observed through the private App projection, not an upstream headless-library or native capture run. Explicit application-only physical input, raster and OS clipboard requirements are never inferred from generated events, flags or private fixtures.

[Original Modern publication and frozen inputs](../2026-11-15-modern-results/report.md).

## Reading the results

- The 69 criteria are loaded from the original frozen requirements and upstream snapshot. The four cursor verdicts below use the new trace.
- Stored-cell predicates are observed through the owned App projection. They do not establish an upstream headless-library result or an application pixel result. Each row names the selected path.
- Physical/OS-generated input, real-time blink, native focus/paste and changed current controls are not inferred from older flags, synthetic phases, generated events or a similarly named case.
- Pinned 4.0.0 stock grades stay beside the new conclusions. This does not change the 80/80 frozen Core report or the qualification inventory, and the percentage must not be compared with the public terminal leaderboard.

## Upstream attribution

Catalogue and baseline material: © 2025–2026 Bjorn Stabell, [terminfo.dev](https://terminfo.dev/baseline/modern), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The original licence is retained as `LICENSE` in `upstream-snapshot.zip`. Catalogue and baseline source bytes are unmodified; the page excerpt, 69-row selection, evidence mapping and assessment are identified Ntilde adaptations. Upstream source revision and Git blob references are retained in the [frozen requirements](../2026-11-15-modern-results/requirements-v1.json).

## Measurement and provenance

Reused owned run `59b3fbe52f6062679e5233e0b62fb9d2`, started 2026-11-15T20:53:19.417Z. App source `aaaece9966cd62ae3e0cbf9afe1c87a52fb120db`; measured scripts `aaaece9966cd62ae3e0cbf9afe1c87a52fb120db`; App version `0.14.0` (operator supplied; not independently verified). Geometry 40×140; source policy `readonly-owned-source-graph-v58`; receipt 64, completed 2026-11-15T20:58:17.136Z.

The archive is [the existing mouse-extended publication](../2026-11-15-mouse-extended/owned-run.zip), SHA-256 `fa0704e372ae41946af6b4b9197a9b474e573b9e2f03afc8bbf82f15c9bac13d`. The complete receipt, source graph, observations and cleanup are rederived in an isolated temporary directory. No archived source is executed and no raw artifact is modified.

New direct-local macOS cursor run `5874ddc4bb4d2d04e1d9edcf09016a06`, started 2026-11-23T22:14:28.549Z. App source `88d635619a313b97e5c9afe81bfc6345d572ca3a`; measured scripts `9dcaa0a2e90511d2646530ea172437ed8e3c11ea`; App version `0.14.0` (operator supplied; not independently verified). Geometry 40×140; source policy `readonly-owned-source-graph-v73`; receipt 80, completed 2026-11-23T22:16:49.640Z.

The [cursor run archive](owned-run.zip) has SHA-256 `f4a62656abcf858b6f994d069e5af2a57b55f00776a2c3e8dbda1f7ee4db5d6a`. In it, `mcur-run.json` contains the exact writes and replies, `mcur-snapshots.jsonl` contains both App reads per frame, and `mcur-observations.json` records the derived traces. Each experiment began on a cleared alternate screen at home with origin mode and left/right margin mode off. The final pair matched the incoming main-screen projection after the alternate fixture was cleared and homed. The receipt verifies process and fixture cleanup.

Retained attempt [attempt-1.zip](attempt-1.zip). The static-image predecessor validator refused source policy v73. The collection recorded the cursor transitions and verified cleanup, then stopped without a complete receipt. The runner was corrected and the entire collection repeated.
Original stock totals: 92 supported / 1 unsupported / 162 inconclusive; suiteComplete=false. Those are whole-suite results under the recorded settings, not a Modern score.

## Per-requirement assessment

| ID | Result | Selected observation path | Evidence / blocker |
| --- | --- | --- | --- |
| `sgr.underline.double` | pass | owned stored-cell semantic predicate | sgus-observations.json — sgr.underline.double |
| `sgr.underline.curly` | pass | owned stored-cell semantic predicate | sgus-observations.json — sgr.underline.curly |
| `sgr.underline.dotted` | pass | owned stored-cell semantic predicate | sgus-observations.json — sgr.underline.dotted |
| `sgr.underline.dashed` | pass | owned stored-cell semantic predicate | sgus-observations.json — sgr.underline.dashed |
| `sgr.underline.color` | pass | owned stored-cell semantic predicate | sgcl-observations.json — sgr.underline.color |
| `sgr.blink` | inconclusive | unmeasured current criterion | Only stored blink and synthetic presentation phases are retained. The current criterion requires a real elapsed-time capture series with a stationary control; no such series is retained. |
| `sgr.overline` | pass | owned stored-cell semantic predicate | sgcl-observations.json — sgr.overline |
| `sgr.fg.bright` | pass | owned stored-cell semantic predicate | sgpal-observations.json — sgr.fg.bright |
| `sgr.bg.bright` | pass | owned stored-cell semantic predicate | sgpal-observations.json — sgr.bg.bright |
| `sgr.fg.256` | pass | owned stored-cell semantic predicate | sgpal-observations.json — sgr.fg.256 |
| `sgr.bg.256` | pass | owned stored-cell semantic predicate | sgpal-observations.json — sgr.bg.256 |
| `sgr.fg.truecolor` | pass | owned stored-cell semantic predicate | sgtc-observations.json — sgr.fg.truecolor |
| `sgr.bg.truecolor` | pass | owned stored-cell semantic predicate | sgtc-observations.json — sgr.bg.truecolor |
| `cursor.shape` | pass | owned stored-cell semantic predicate | curq-observations.json — cursor.shape |
| `cursor.horizontal-absolute` | pass | fenced CPR and paired App cursor reads | [cha](#cha) |
| `cursor.next-line` | pass | fenced CPR and paired App cursor reads | [cnl](#cnl) |
| `cursor.position-report` | pass | application protocol reply | raw.json — cursor.vpa |
| `cursor.ansi-save` | pass | fenced CPR and paired App cursor reads | [ansi-round-trip](#ansi-round-trip) |
| `cursor.ansi-restore` | pass | fenced CPR and paired App cursor reads | [ansi-round-trip](#ansi-round-trip) |
| `editing.insert-chars` | pass | owned stored-cell semantic predicate | ched-observations.json — editing.insert-chars |
| `editing.delete-chars` | pass | owned stored-cell semantic predicate | ched-observations.json — editing.delete-chars |
| `editing.insert-lines` | pass | owned stored-cell semantic predicate | ledt-observations.json — editing.insert-lines |
| `editing.delete-lines` | pass | owned stored-cell semantic predicate | ledt-observations.json — editing.delete-lines |
| `editing.repeat-char` | pass | owned stored-cell semantic predicate | ched-observations.json — editing.repeat-char |
| `input.modify-other-keys` | inconclusive | unmeasured current criterion | Generated App key events prove encoder delivery, but the current application criterion requires an OS XTEST plain-a delivery control and injected Ctrl+I under level 2. |
| `input.csi-u` | pass | application protocol reply | raw.json — input.csi-u |
| `input.x10-mouse` | inconclusive | unmeasured current criterion | Generated App mouse events are retained; the current application criterion requires the external click delivery control and click under mode 9. That physical/OS injection path was not measured. |
| `input.button-event-mouse` | inconclusive | unmeasured current criterion | Generated App button-motion bytes are retained, but the current application criterion requires an externally delivered click/drag/release experiment. |
| `modes.bracketed-paste` | pass | application protocol reply | raw.json — modes.bracketed-paste |
| `modes.synchronized-output` | inconclusive | unmeasured current criterion | Stock DECRPM reports reset. The current criterion then requires DECSET, a second query proving set and restoration; the stock recognition-only trace does not perform it. |
| `modes.mouse-tracking` | pass | application protocol reply | raw.json — modes.mouse-tracking |
| `modes.mouse-sgr` | pass | application protocol reply | raw.json — modes.mouse-sgr |
| `modes.mouse-all` | pass | application protocol reply | raw.json — modes.mouse-all |
| `modes.focus-tracking` | inconclusive | unmeasured current criterion | The stock trace proves only DECRPM recognition. The current criterion requires an actual focus-out/focus-in round trip with the input reader armed, delivery key and mode-off control. |
| `modes.origin` | pass | application protocol reply | raw.json — modes.origin |
| `modes.insert-replace` | pass | owned stored-cell semantic predicate | irm-observations.json — modes.insert-replace |
| `modes.application-cursor` | pass | application protocol reply | raw.json — modes.application-cursor |
| `modes.application-keypad` | inconclusive | unmeasured current criterion | Generated keypad events are retained, but the current application criterion requires externally injected KP_Add in both DECKPAM and DECKPNM with a delivery control. |
| `modes.altscreen-47` | pass | owned stored-cell semantic predicate | svrs-observations.json — modes.altscreen-47 |
| `modes.altscreen-1047` | pass | owned stored-cell semantic predicate | svrs-observations.json — modes.altscreen-1047 |
| `modes.altscreen-1048` | pass | application protocol reply | raw.json — modes.altscreen-1048 |
| `modes.alt-scroll-1007` | inconclusive | unmeasured current criterion | Only mode recognition is retained. The current application criterion requires a delivered wheel-up in the alternate screen and actual cursor-key bytes. |
| `scrollback.viewport-hold-output` | inconclusive | unmeasured current criterion | The required qualified inward viewport action and old-anchor hold through fresh output, including the independent synchronized-output repeat, are not retained for this criterion. |
| `extensions.osc2-title` | inconclusive | unmeasured current criterion | Owned raw/display title evidence exists. The current application criterion additionally requires two distinct reader controls, a made-up OSC negative, and incoming-title restoration in the same attempt. The older prepared-baseline contract does not establish all of these. |
| `extensions.osc0-icon-title` | inconclusive | unmeasured current criterion | Raw title and icon writes are supported by the owned contract. The current criterion additionally requires the qualified same-attempt OSC2 title control and restoration of every incoming observable label; the older prepared baseline is not equivalent. |
| `extensions.osc52-clipboard` | inconclusive | unmeasured current criterion | The current application criterion requires both directions through a verified disposable Linux clipboard, with independent readback/seeding and verified restoration. The retained private fixture does not establish that OS clipboard path. |
| `extensions.osc10-fg-color` | pass | application protocol reply | raw.json — extensions.osc10-fg-color |
| `extensions.osc11-bg-color` | pass | application protocol reply | raw.json — extensions.osc11-bg-color |
| `extensions.osc7-cwd` | inconclusive | unmeasured current criterion | Owned pane metadata exposes decoded paths. The current criterion requires serialized URI/control readback or a qualified public application surface and restoration of the incoming URI; those are not interchangeable. |
| `extensions.truecolor` | pass | owned stored-cell semantic predicate | sgtc-observations.json — sgr.fg.truecolor |
| `reset.decaln` | pass | owned stored-cell semantic predicate | svrs-observations.json — reset.decaln |
| `device.primary-da` | pass | application protocol reply | raw.json — device.primary-da |
| `device.status-report` | pass | application protocol reply | raw.json — device.status-report |
| `device.secondary-da` | pass | application protocol reply | raw.json — device.secondary-da |
| `device.decrpm` | pass | application protocol reply | raw.json — device.decrpm |
| `device.xtwinops-18` | pass | application protocol reply | raw.json — device.xtwinops-18 |
| `device.env-colorterm` | inconclusive | unmeasured current criterion | Manual environment criterion. No retained independent child observation establishes the applicable value and inheritance policy for this requirement. |
| `device.env-term-program` | inconclusive | unmeasured current criterion | Manual environment criterion. An App version label is not the child TERM_PROGRAM variable; no applicable child observation is retained. |
| `device.env-vte-version` | inconclusive | unmeasured current criterion | Manual terminal-family criterion. Ntilde is not established as VTE-based; no VTE_VERSION presence or claimed VTE identity is inferred. |
| `device.env-no-color` | inconclusive | unmeasured current criterion | Manual environment/policy criterion. No retained child inheritance observation or user-policy experiment establishes this row. |
| `modes.color-scheme-reporting` | inconclusive | unmeasured current criterion | The stock query reports mode 2031 reset. The current application criterion requires enabling it, observing set, and restoring reset; recognition alone does not meet it. |
| `extensions.osc52-write` | inconclusive | unmeasured current criterion | The owned clipboard fixture does not establish the current application criterion: a real key event, independent X11 clipboard readback and verified restoration on a disposable Linux display. |
| `extensions.osc4-palette` | pass | application protocol reply | raw.json — extensions.osc4-palette |
| `extensions.osc12-cursor-color` | pass | application protocol reply | raw.json — extensions.osc12-cursor-color |
| `extensions.osc104-reset-palette` | pass | owned stored-cell semantic predicate | crst-observations.json — extensions.osc104-reset-palette |
| `extensions.osc110-reset-fg` | pass | owned stored-cell semantic predicate | crst-observations.json — extensions.osc110-reset-fg |
| `extensions.osc111-reset-bg` | pass | owned stored-cell semantic predicate | crst-observations.json — extensions.osc111-reset-bg |
| `extensions.osc112-reset-cursor` | pass | owned stored-cell semantic predicate | crst-observations.json — extensions.osc112-reset-cursor |
| `extensions.clipboard-paste` | inconclusive | unmeasured current criterion | No retained default OS paste action with an independently seeded owned selection, delivery control and off/on/off byte comparisons satisfies this current application criterion. |

## Cursor traces

The positions below are zero-based. CPR uses one-based coordinates. Each transition has two agreeing App reads and a raw CPR ahead of its DA fence.

### cha

| Step | App position | CPR | Journal frame |
| --- | --- | --- | --- |
| home | (0, 0) | `1;1` | 2 |
| text | (0, 5) | `1;6` | 3 |
| target | (0, 2) | `1;3` | 4 |

### cnl

| Step | App position | CPR | Journal frame |
| --- | --- | --- | --- |
| home | (0, 0) | `1;1` | 5 |
| text | (0, 3) | `1;4` | 6 |
| target | (2, 0) | `3;1` | 7 |

### ansi-round-trip

| Step | App position | CPR | Journal frame |
| --- | --- | --- | --- |
| home | (0, 0) | `1;1` | 8 |
| initial | (3, 5) | `4;6` | 9 |
| saved | (3, 5) | `4;6` | 10 |
| displaced | (10, 10) | `11;11` | 11 |
| restored | (3, 5) | `4;6` | 12 |


## Reproduce

Requires Node 24+ and Python 3; runs entirely offline without a desktop lease.

```sh
node scripts/terminfo-modern-results.mjs --check --cursor
```

`report.json` retains every frozen criterion, evidence reference, selected observation, original stock verdict and remaining blocker. The [original publication](../2026-11-15-modern-results/report.md) retains the frozen input files and their upstream attribution.

For a fresh capture, build the Release App at the recorded App revision using the repository build wrapper. Prepare the pinned collector at the measured scripts revision using the [QA guide](../../QA_TERMINFO.md). Run the owned collector with the standalone App on an awake macOS desktop.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope osc633
```
