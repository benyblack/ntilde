# Inconclusive terminfo observations — 2026-11-02

All **177** retained inconclusive observations are triaged below: **152 insufficient-evidence, 22 no-response, 3 policy-refused**. The original result stays **78 supported / 0 unsupported / 177 inconclusive**, with `suiteComplete=false` and identity `unverified`. Source inspection is a separate inference; it is not a new probe result or a comparison with other emulators.

Raw SHA-256: `2ad91173e34040c19bd4ee4a97833d724d609e3cf7a2bc99a5fa1b9a99edc018`. Collector source: `cf7d8759c7e637aa8dba1e429f3dddd1c0124158`. App source hint: `5aa7481b2d3187325378168f68c0548111322ac3` (from informational version, not independent identity attestation). Source audit: `c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b`. The [machine-readable inventory](2026-11-02-inconclusive-triage.json) preserves every original observation, each available trace digest, and the audited source-file hashes. The retained local raw artifact is `artifacts/terminfo/owned-lZP2Uv/raw.json`; private settings are neither read nor included.

The 22 missing replies split into 18 unimplemented exact query variants, two conditional selection-color queries, and two Kitty image-number/allocation-ack variants. Basic Kitty/iTerm2/Sixel image decoding does not prove all image-protocol variants. The 152 evidence-limited observations also include source limitations; they are not 152 implemented features or 152 proven missing features.

## Work packages

### Q01 — Observe cells, geometry and history

**30 observations; owner: VT + App QA.** Use a disposable pane with independently confirmed rows/columns; compare cell contents, cursor, margins and scrollback before/after, then retain the observation and cleanup receipt. CPR alone cannot prove content or erasure.

Source context: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Csi.cs), [TerminalBuffer.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/TerminalBuffer.cs), [EraseInDisplayTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.VT.Tests/EraseInDisplayTests.cs), [ScrollAndWrapCorrectnessTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/ScrollAndWrapCorrectnessTests.cs).

### Q02 — Observe attributes and rendered pixels

**40 observations; owner: VT + Rendering + App QA.** Assert decoded cell attributes/charset mapping and independently capture controlled rendered pixels. Use a controlled clock for blink; preserve the input, capture hashes and restored fixture state. Cursor advance is insufficient.

Source context: [AnsiParser.Sgr.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Sgr.cs), [SgrAttributeTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/SgrAttributeTests.cs), [DecSpecialGraphicsTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/DecSpecialGraphicsTests.cs), [RuntimeColorRenderTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/RenderTests/RuntimeColorRenderTests.cs).

### Q03 — Observe host metadata and UI effects

**17 observations; owner: App QA + VT callbacks.** Read the private pane's title, cwd, hyperlink/shell-marker metadata or notification/progress presentation through an independent host observer. Record private setting/focus preconditions and verify restoration. A consumed marker or DA sentinel is insufficient.

Source context: [AnsiParser.Osc.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Osc.cs), [OscShellIntegrationTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/OscShellIntegrationTests.cs), [Osc9NotificationProgressTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.VT.Tests/Osc9NotificationProgressTests.cs), [PaneParserWiringTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs).

### Q04 — Observe image pixels and playback

**2 observations; owner: Rendering + App QA.** Capture decoded/rendered pixels and placement in a private fixture; animation requires timed frame evidence. Do not infer pixel output, placeholders or playback from query recognition or cursor movement.

Source context: [AnsiParser.Images.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Images.cs), [AnsiParser.KittyGraphics.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.KittyGraphics.cs), [GraphicsTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/GraphicsTests.cs), [InlineImageEndToEndTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.Rendering.Tests/InlineImageEndToEndTests.cs).

### Q05 — Generate and validate input events

**3 observations; owner: App input + QA.** Inject a controlled key/keypad/mouse event in an owned pane and compare the actual encoded child-input report, including coordinates and modifiers. A mode response does not prove input encoding.

Source context: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Csi.cs), [TerminalInputModeEncoderTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/Input/TerminalInputModeEncoderTests.cs), [MouseReportingTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/MouseReportingTests.cs).

### Q06 — Observe saved/restored terminal state

**4 observations; owner: VT + App QA.** Compare complete main/alternate buffer contents, saved cursor and relevant mode/attribute state before entry, while active, and after restoration. A responsive cursor after exit is insufficient.

Source context: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Csi.cs), [TerminalBuffer.StateTransfer.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/TerminalBuffer.StateTransfer.cs), [AlternateScreenTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/AlternateScreenTests.cs), [SoftResetAndCursorSaveTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.VT.Tests/SoftResetAndCursorSaveTests.cs).

### Q07 — Unimplemented query variant

**18 observations; owner: VT protocol backlog.** The reviewed dispatcher has no reply for this exact query variant. Add a narrowly scoped contract and request/reply regression before implementation, or explicitly document the variant as intentionally unavailable. Rerun the owned query after any change; retain the original inconclusive grade.

Source context: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Csi.cs), [AnsiParser.Osc.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Osc.cs), [AnsiParser.Images.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Images.cs), [vt_coverage_matrix.md](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/docs/vt_coverage_matrix.md).

### Q08 — Conditional selection-color query

**2 observations; owner: VT colors + App theme QA.** OSC 17/19 are implemented, but unset derived selection colors can be null and intentionally produce no concrete reply. In a private fixture, set a known override, query it, restore it and verify restoration; test host-derived defaults separately.

Source context: [AnsiParser.Colors.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Colors.cs), [TerminalBuffer.RuntimeColors.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/TerminalBuffer.RuntimeColors.cs), [RuntimeColorTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.VT.Tests/RuntimeColorTests.cs), [vt_coverage_matrix.md](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/docs/vt_coverage_matrix.md).

### Q09 — Kitty image-number/acknowledgement gap

**2 observations; owner: VT graphics + App ownership QA.** The probe sends uppercase I image numbers. The reviewed implementation reads lowercase i and emits replies for a=q, not correlated allocation acknowledgements for these a=t/T probes. Add image-number allocation/ack/cleanup contract tests; existing basic image decoding is separate evidence.

Source context: [AnsiParser.KittyGraphics.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.KittyGraphics.cs), [GraphicsTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/GraphicsTests.cs), [AnsiParserHardeningTests.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs), [vt_coverage_matrix.md](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/docs/vt_coverage_matrix.md).

### Q10 — Source-reviewed limited or missing variant

**52 observations; owner: VT/App protocol backlog.** The exact spelling/variant has a source limitation listed with this row. Add a focused state/effect contract and owned observer, implement separately if desired, and rerun; do not equate related supported protocols with this variant.

Source context: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Csi.cs), [AnsiParser.Osc.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Osc.cs), [AnsiParser.KittyGraphics.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.KittyGraphics.cs), [vt_coverage_matrix.md](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/docs/vt_coverage_matrix.md).

### Q11 — Keep clipboard ownership refusal

**3 observations; owner: App native QA + collector policy.** These OSC 52 packets were not sent. Keep them refused until the owned collector itself has a disposable clipboard and verified restoration/cleanup. Separate native CI interchange results do not retroactively qualify these observations. OSC 52 reads intentionally return denial.

Source context: [AnsiParser.Osc.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Osc.cs), [ownership.mjs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/scripts/terminfo/ownership.mjs), [clipboard-native.md](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/docs/qa/clipboard-native.md), [2026-11-01-clipboard.md](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/docs/qa/terminfo/2026-11-01-clipboard.md).

### Q12 — Negative query response is not a full capability result

**2 observations; owner: VT protocol QA.** DECRQSS and XTGETTCAP return well-formed failure responses for the requested setting/name. Preserve that evidence; enumerate and test individual supported/unsupported settings before claiming the whole query family.

Source context: [AnsiParser.Images.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.Images.cs), [vt_coverage_matrix.md](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/docs/vt_coverage_matrix.md).

### Q13 — Collector API/legacy evidence gap

**2 observations; owner: QA adapter.** This is an adapter/API observation, not proof of missing VT behavior. Add an owned reset/state readback adapter or replace the legacy feature hint with explicit assertions. Do not invoke an unowned app reset or use a legacy hint as support evidence.

Source context: [collector.mjs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/scripts/terminfo/collector.mjs), [ownership.mjs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/scripts/terminfo/ownership.mjs), [AnsiParser.cs](https://github.com/benyblack/ntilde/blob/c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b/src/Ntilde.VT/AnsiParser.cs).

## Observation-by-observation inventory

Every row below retains its original inconclusive outcome. Source-limit notes describe the audited implementation, not a replacement grade.

| Feature | Original reason | Work package | Source limitation |
|---|---|---|---|
| `charsets.dec-line-drawing` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `charsets.dec-special` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `charsets.g0-g1-switching` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `charsets.utf8` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `cursor.cup-scroll-region` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `cursor.hide` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `cursor.shape` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `device.decrqss` | insufficient-evidence | Q12 | See the original note/trace and work package; no support inference. |
| `device.term-features` | insufficient-evidence | Q13 | See the original note/trace and work package; no support inference. |
| `device.tertiary-da` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `device.xtgettcap` | insufficient-evidence | Q12 | See the original note/trace and work package; no support inference. |
| `device.xtgetxres` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `device.xtreportcolors` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `device.xtwinops-18` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `device.xtwinops-20` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `device.xtwinops-21` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `device.xtwinops-22` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `device.xtwinops-23` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `editing.deccara` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.deccra` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.decdc` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.decera` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.decfra` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.decic` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.decrara` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.decrqcra` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `editing.decsace` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.decsera` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.delete-chars` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `editing.delete-lines` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `editing.insert-chars` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `editing.insert-lines` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `editing.repeat-char` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `editing.sl` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `editing.sr` | insufficient-evidence | Q10 | These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths. |
| `erase.character` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.ed-scroll-region` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.el-with-attrs` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.line.all` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.line.left` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.line.right` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.screen.above` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.screen.all` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.screen.below` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.screen.scrollback` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `erase.selective` | insufficient-evidence | Q10 | DECSED/DECSEL alias ordinary erase because DECSCA protected-character state is unimplemented. Protection-sensitive behavior is not supported by that alias. |
| `extensions.iterm2-images` | insufficient-evidence | Q04 | See the original note/trace and work package; no support inference. |
| `extensions.kitty-graphics.animation` | insufficient-evidence | Q10 | Graphics dispatch handles a=t/T and q; only cursor-anchored placement is implemented, not the tested animation/Unicode-placeholder variants. |
| `extensions.kitty-graphics.display` | no-response | Q09 | See the original note/trace and work package; no support inference. |
| `extensions.kitty-graphics.transmit` | no-response | Q09 | See the original note/trace and work package; no support inference. |
| `extensions.kitty-graphics.unicode-placeholders` | insufficient-evidence | Q10 | Graphics dispatch handles a=t/T and q; only cursor-anchored placement is implemented, not the tested animation/Unicode-placeholder variants. |
| `extensions.notifications` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc-633-vscode` | insufficient-evidence | Q10 | OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol. |
| `extensions.osc0-icon-title` | insufficient-evidence | Q10 | OSC 0/2 route to window-title changes; there is no separate OSC 1 icon-name handler/state. The combined icon/title variant is partial. |
| `extensions.osc1-icon` | insufficient-evidence | Q10 | OSC 0/2 route to window-title changes; there is no separate OSC 1 icon-name handler/state. The combined icon/title variant is partial. |
| `extensions.osc104-reset-palette` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc110-reset-fg` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc111-reset-bg` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc112-reset-cursor` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc113-reset-pointer-fg` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc114-reset-pointer-bg` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc117-reset-highlight-bg` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc119-reset-highlight-fg` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc133-a` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc133-b` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc133-c` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc133-d` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc133-p` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc1337-capabilities` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc1337-cellsize` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc17-highlight-bg` | no-response | Q08 | See the original note/trace and work package; no support inference. |
| `extensions.osc176-app-id` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc19-highlight-fg` | no-response | Q08 | See the original note/trace and work package; no support inference. |
| `extensions.osc2-title` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc21-kitty-color` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc22-pointer` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc3008-context` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc440-audio` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc5-special-color` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc52-clipboard` | policy-refused | Q11 | See the original note/trace and work package; no support inference. |
| `extensions.osc52-read` | policy-refused | Q11 | See the original note/trace and work package; no support inference. |
| `extensions.osc52-write` | policy-refused | Q11 | See the original note/trace and work package; no support inference. |
| `extensions.osc555-flash` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc633-a` | insufficient-evidence | Q10 | OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol. |
| `extensions.osc633-b` | insufficient-evidence | Q10 | OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol. |
| `extensions.osc633-c` | insufficient-evidence | Q10 | OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol. |
| `extensions.osc633-d` | insufficient-evidence | Q10 | OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol. |
| `extensions.osc633-e` | insufficient-evidence | Q10 | OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol. |
| `extensions.osc633-p` | insufficient-evidence | Q10 | OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol. |
| `extensions.osc666-termprop` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc7-cwd` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc701-locale` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc702-version` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc710-font-normal` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc720-scroll-up` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc776-cell-size` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc777-notify` | insufficient-evidence | Q10 | The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect. |
| `extensions.osc7770-font-size` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc7777-font-window-size` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.osc8` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc9-progress` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.osc99-kitty-notify` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.reflow` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `extensions.semantic-prompts` | insufficient-evidence | Q03 | See the original note/trace and work package; no support inference. |
| `extensions.sixel` | insufficient-evidence | Q04 | See the original note/trace and work package; no support inference. |
| `extensions.sixel-geometry-report` | no-response | Q07 | See the original note/trace and work package; no support inference. |
| `extensions.truecolor` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `input.button-event-mouse` | insufficient-evidence | Q05 | See the original note/trace and work package; no support inference. |
| `input.modify-other-keys` | insufficient-evidence | Q10 | Xterm modifyOtherKeys and pixel/urxvt mouse encodings are not implemented; Kitty keyboard and legacy X10/SGR mouse paths are different contracts. |
| `input.modify-other-keys-3` | insufficient-evidence | Q10 | Xterm modifyOtherKeys and pixel/urxvt mouse encodings are not implemented; Kitty keyboard and legacy X10/SGR mouse paths are different contracts. |
| `input.pixel-mouse` | insufficient-evidence | Q10 | Xterm modifyOtherKeys and pixel/urxvt mouse encodings are not implemented; Kitty keyboard and legacy X10/SGR mouse paths are different contracts. |
| `input.urxvt-mouse` | insufficient-evidence | Q10 | Xterm modifyOtherKeys and pixel/urxvt mouse encodings are not implemented; Kitty keyboard and legacy X10/SGR mouse paths are different contracts. |
| `input.x10-mouse` | insufficient-evidence | Q05 | See the original note/trace and work package; no support inference. |
| `modes.alt-screen.exit` | insufficient-evidence | Q06 | See the original note/trace and work package; no support inference. |
| `modes.alt-scroll-1007` | insufficient-evidence | Q10 | The corresponding DEC private mode has no tracked/implemented case in the reviewed private-mode dispatcher; DECRPM zero is not evidence of its behavior. |
| `modes.altscreen-1047` | insufficient-evidence | Q06 | See the original note/trace and work package; no support inference. |
| `modes.altscreen-47` | insufficient-evidence | Q06 | See the original note/trace and work package; no support inference. |
| `modes.application-keypad` | insufficient-evidence | Q05 | See the original note/trace and work package; no support inference. |
| `modes.deccolm` | insufficient-evidence | Q10 | The corresponding DEC private mode has no tracked/implemented case in the reviewed private-mode dispatcher; DECRPM zero is not evidence of its behavior. |
| `modes.decsclm` | insufficient-evidence | Q10 | The corresponding DEC private mode has no tracked/implemented case in the reviewed private-mode dispatcher; DECRPM zero is not evidence of its behavior. |
| `modes.insert-replace` | insufficient-evidence | Q06 | See the original note/trace and work package; no support inference. |
| `modes.left-right-margin` | insufficient-evidence | Q10 | The corresponding DEC private mode has no tracked/implemented case in the reviewed private-mode dispatcher; DECRPM zero is not evidence of its behavior. |
| `modes.reverse-video` | insufficient-evidence | Q10 | The corresponding DEC private mode has no tracked/implemented case in the reviewed private-mode dispatcher; DECRPM zero is not evidence of its behavior. |
| `modes.utf8-mouse-1005` | insufficient-evidence | Q10 | The corresponding DEC private mode has no tracked/implemented case in the reviewed private-mode dispatcher; DECRPM zero is not evidence of its behavior. |
| `modes.xtpopcolors` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `modes.xtpopsgr` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `modes.xtpushcolors` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `modes.xtpushsgr` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `modes.xtrestore` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `modes.xtsave` | insufficient-evidence | Q10 | These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts. |
| `reset.decaln` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `reset.method` | insufficient-evidence | Q13 | See the original note/trace and work package; no support inference. |
| `reset.sgr` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `scrollback.accumulate` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.alt-screen` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.decstbm` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.decstbm-reset` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.reverse-index` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.scroll-down` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.scroll-up` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.set-region` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `scrollback.total-lines` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `sgr.bg.256` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.bg.bright` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.bg.default` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.bg.standard` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.bg.truecolor` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.blink` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.bold` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.faint` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.fg.256` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.fg.bright` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.fg.default` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.fg.standard` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.fg.truecolor` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.hidden` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.inverse` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.italic` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.overline` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.reset` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.selective-reset.bold` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.selective-reset.inverse` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.selective-reset.italic` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.selective-reset.underline` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.strikethrough` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline-color-indexed` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline-color-reset` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline-color-rgb` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline.color` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline.curly` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline.dashed` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline.dotted` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline.double` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `sgr.underline.single` | insufficient-evidence | Q02 | See the original note/trace and work package; no support inference. |
| `text.basic` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `text.overwrite` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |
| `text.reverse-index-scroll` | insufficient-evidence | Q01 | See the original note/trace and work package; no support inference. |

## Evidence discipline

The geometry-dependent pixel probes measured 41×140, not their required tiny fixtures. CPR, a DA sentinel, consumed input, a rejected setting query, and a legacy feature hint each have narrower meaning than observed effects. Never turn those into a positive or negative capability score.

The three OSC 52 probes were deliberately not sent. Native clipboard qualification and session-only OSC 5522 grants are separate follow-ups; they do not retroactively change collector ownership or its historical grades. Keep refusal until that collector has verified disposable ownership and cleanup.

Before regrading, retain the new source/runtime/collector pins, assertions or pixel hashes, relevant state-restoration evidence and completed ownership receipt. A source test can document a protocol contract; it cannot masquerade as a live desktop observation.

Regenerate with `python3 scripts/terminfo-triage.py --raw <retained-owned-raw.json>`. Validate the committed inventory without a private artifact with `python3 scripts/terminfo-triage.py --check`.
