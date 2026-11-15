# Device query qualification: XTGETXRES, XTREPORTCOLORS, CSI 20 t and CSI 21 t (2026-11-15)

Qualifies four pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `device.xtgetxres`, `device.xtreportcolors`, `device.xtwinops-20` and
`device.xtwinops-21`. `device.term-features` is **documented only** (below). **This batch changed production code**: before it the VT ignored all four requests (the exact-query audit
listed them as unavailable and the witnesses `q07.device.*` recorded matched silence).

## What the App now does

- **XTGETXRES** (`DCS + Q hex-name[;…] ST`): the only resource is `termName` (there is no X resource database), answered `DCS 1 + R <name>=<uppercase hex of the TERM> ST` with the TERM the App gave
  the pane's child (`xterm-256color`; `TerminalPane.ChildTerminalType`, kept equal to the native PTY layer and the SSH options by a test). Any other name is `DCS 0 + R <name> ST`. One DCS per request,
  the status is the first name's and the reply stops after the first unknown name, as xterm's `do_dcs` does. Malformed, truncated, BEL-terminated or more-than-16-name requests are not answered. Persistent
  (mux) sessions answer `DCS 0 + R` (the daemon's parser is not told a TERM). XTGETTCAP `TN` stays refused.
- **xterm palette stack**: `CSI Pm # P` / `CSI Pm # Q` / `CSI # R`, ten slots over the dynamic colors and the 256-color palette, independent of the kitty stack (`OSC 30001/30101`), carried by state
  transfer and cleared by RIS. No parameter or 0 pushes onto / pops from the top; 1..10 stores into / restores from that slot (xterm moves the depth to the slot on a push and to the slot below on a pop; the
  ctlseqs text says "without popping", the source decides). The report is `CSI ? depth ; deepest # Q`.
- **CSI 20 t / CSI 21 t**: `OSC L icon ST` / `OSC l title ST` with the raw OSC 0/1/2 state, **off by default** behind the new `AllowTitleReport` setting (xterm ships both disallowed: the reply is typed into the
  foreground program's input). A label with a control character, or too long to retain, is answered empty. Direct panes only.

## What the pinned probes can say

| Case | Pinned probe | Reachable verdicts |
|---|---|---|
| `device.xtgetxres` | `DCS + Q 7465726d4e616d65 ST`; valid `DCS 1 + R <name>=hex ST` (case-insensitive) | supported; the `DCS 0 + R` refusal is inconclusive |
| `device.xtreportcolors` | `CSI # R`; valid `CSI [0-9;]* # Q` | supported only for a reply **without** a `?` |
| `device.xtwinops-20` / `-21` | `OSC 1;test-icon` / `OSC 2;test-title` then the request; valid `OSC L/l <label>` | supported; a different label contradicts (`-20` only) |
| `device.term-features` | reads `process.env.TERM_FEATURES`, emits no request, returns inconclusive in every case | **inconclusive only** |

**The xterm reply cannot satisfy the pinned XTREPORTCOLORS pattern.** xterm's `xtermReportColors` sets the `?` private marker; the pinned valid pattern has none. The App follows xterm, so the pinned
verdict stays inconclusive (`invalid-reply`) and is recorded as a divergence beside the supported effect contract. The reply was not changed to score on the pinned pattern.

`device.term-features` has no query and no case here; a setting or an environment variable would not make it a measured pass. It stays inconclusive in the stock run and no production behavior was added.

## Result

| Case | Pinned (termless and terminal paths) | Effect contract (`ntilde-device-query-report-effect-v1`) | Stock grade (this run) |
|---|---|---|---|
| `device.xtgetxres` | **supported** | **supported** | **supported** |
| `device.xtreportcolors` | **inconclusive** (`invalid-reply`) | **supported** (divergence `pinned-inconclusive-effect-supported`) | inconclusive |
| `device.xtwinops-20` | **supported** | **supported** | **supported** |
| `device.xtwinops-21` | **supported** | **supported** | **supported** |

The stock collector in the same owned run (`ece4f56d710f9cc36e15115dc2abed23`) grades 91 supported / 1 unsupported / 163 inconclusive (`suiteComplete=false`), three more supported than 88/1/166: the three
stock changes come from the App now answering the unchanged pinned probes (the title reports because this run turns `AllowTitleReport` on). The frozen stock baseline keeps its original grades. No Core point is added.

## What was measured (disposable direct-local pane, alternate screen)

Each case runs on a seeded alternate screen (a nonce row, bold and indexed-red text, a curly-underlined cell, margins 2..6). A request step is one **fenced exchange**: the exact request bytes, then every
raw reply (or its explicit absence) before a DA barrier, retained byte for byte and journaled by the parent beside a complete state read. Every exchange must leave the complete screen, cursor, rendition, margins, modes
and active screen unchanged. The pinned probes run their own code over the journaled bytes of their own exchange (termless path) and the same bytes plus the barrier (terminal path). The independent oracle is written
from xterm's control-sequence text and source (`report_win_label`, `do_dcs` case Q, `xtermPushColors`, `xtermPopColors`, `xtermReportColors`); it shares no code with the parser, and the contract model was also checked
against the C# VT before any desktop run. Roles: `protocol` groups decide a mismatch, `control` groups prove the setup took effect (a failed control is inconclusive), `policy` groups record Ntilde choices xterm does not make.

- **XTGETXRES (10 groups).** the pinned request, the uppercase echo, unknown, case-sensitive and class names, two- and three-name lists (status and stop rule), unanswered odd-length, non-hex and
  empty requests each followed by an answered pinned request.
- **Palette stack (17 groups).** reports on an empty stack, pushes and pops of the background and palette entry 5 read back through `OSC 11` / `OSC 4` replies, empty pops, a numbered store and restore, kitty-stack independence,
  out-of-range slots, the ten-slot bound, a spelling policy group and a drain to depth 0 (deepest stays 10). The case ends with an explicit colour reset; the start and finish screens are compared.
- **Title and icon reports (9 groups each).** the pinned round trip, distinct icon and title state, OSC 0, empty and semicolon labels, title-stack interplay, stability; policy groups for a control character and for non-exact spellings.

All protocol and control groups matched in the final run, and every exchange left the state unchanged.

## Query witnesses (query policy v4)

From `owned-exact-query-paired-raw-state-v4` (v1–v3 runs keep the silence expectation): `q07.device.xtgetxres` owes the `termName` reply and `q07.device.xtreportcolors` owes `CSI ? 0 ; 0 # Q` (the query suite begins
with RIS, which clears the stack) — both matched. `q07.device.xtwinops-20` and `-21` keep the frozen silence expectation, so they record **two mismatches** while the run has the title reports on: the actual replies are the
device-query suite's baseline label (`OSC L/l DQ-base-<run>`), retained as evidence; the witness model cannot name a run-specific label.

## Not claimed

The default-off title gate is unit-tested, not measured (the measured run turns it on). `termName` is the documented session TERM, not read from the child's environment. The xterm stack's deepest counter stays at its
measured value in the disposable App (only RIS resets it) and the pane title is left at the suite's baseline, not the pre-run original. No other X resource, no rendering or painted-pixel claim for the colors, no
remote/mux, persistent-session, Windows/Linux or common-harness claim. Seven older query witnesses record mismatches against their frozen expectations as the exact-query audit documents, unchanged by this batch.

## Identities

App 0.14.0 (operator supplied) built from `8692e250b6199ba909dbf8f40c99bda906571c39` (arm64 Release, executable SHA-256 `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256
`47810c95eadfbdee0b14453d177d5f09bc99560a6d6fb2d3217702f4ab9d9526`); measured scripts `8692e250b6199ba909dbf8f40c99bda906571c39`; source policy `readonly-owned-source-graph-v55` (367 files; manifest SHA-256
`f6d2f04ad851529c4c5555f7c9b6d3cac1b26da6eb2d9453dd4c4f4623f0590c`); query policy `owned-exact-query-paired-raw-state-v4`; initial settings SHA-256 `b544002faad39006f59b0926be0c8808770a20059b2af109545d7628eac8589f`;
receipt schema 61; Node v24.22.2; owned run `aad1ec6c3c949076162c385568a6e939`; collected 2026-11-15T12:47Z on macOS. The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their
receipt suffixes rederive from the retained bytes.

## Retained attempts ([superseded/](2026-11-15-device-queries/superseded/))

1. Aborted mid-run: one XTGETXRES policy request (17 names) exceeded the fence's 64-character exchange limit (`Invalid owned exchange request`). The group was dropped (unit-tested only) and every request length is now tested.
2. Collected every suite, then the parent-side rederivation refused it: popping the xterm palette stack restores the saved colours, not the host's, so cases after the colour-stack case saw another colour binding. The case now
   ends with an explicit colour reset.
3. Completed with all four effect contracts supported and the same pinned verdicts; superseded because the `q07.device.xtreportcolors` witness had been written to owe `0;10` while the query suite's RIS yields `0;0`, a measured-script change.
4. Completed with all four effect contracts supported, measured at `dac43ac5` on source policy v54 / receipt schema 60 / App 0.13.0; superseded because main moved (PR 962, selection colors, took policy v54, and the 0.14.0 release changed the App), so the layer was restacked
   onto policy v55 and remeasured.
5. First run on the restacked tree: failed at start because the restack merge had dropped the device-query bundle keys from the runner's config and the `devqEvidence` argument of the receipt completion. Restored, and covered by a comparison of every device-query reference per file between the old and restacked branches.

6. Completed on the restacked tree at `906234df` (run `62463fd7`) with the same grades; superseded by SonarCloud findings on the PR (a possible null access in the synthetic-evidence fixture, cognitive complexity in the frame parser, the XTGETXRES reply builder and the runtime-color validation, and style items). Fixed without changing behavior, which changes measured scripts and the App, so the run was repeated.

The final run is [owned-run.zip](2026-11-15-device-queries/owned-run.zip); `node --test scripts/terminfo/devq-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the retained journal and
compares the published projection.
