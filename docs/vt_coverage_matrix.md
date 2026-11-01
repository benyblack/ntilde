# VT Conformance Matrix

This document is the **single source of truth** for what Ntilde supports (and intentionally does not) in terms of VT/xterm/DEC behavior.

It is designed to be:
- **Actionable**: each row maps to code areas + tests.
- **Auditable**: “Supported” means there is verification evidence (replay, unit test, external suite).
- **Maintainable**: when behavior changes, update **one row** and its linked tests.

---

## Status legend

| Status | Meaning | Verification requirement |
|---|---|---|
| ✅ Supported | Implemented and correct for the documented scope | At least 1 automated test (replay/unit/external) |
| ⚠ Partial | Implemented but with limitations or known deviations | Automated test + deviation note |
| 🧪 Experimental | Works but not yet stable/guaranteed | Optional tests; can change |
| ❌ Not supported | Not implemented | N/A |
| 🚫 Won’t support | Intentionally not supported | Rationale required |

**Verification types**
- **Replay**: `*.rec → *.snap` (golden)
- **Unit**: targeted unit tests (parser/buffer)
- **External**: external suites (e.g., VTTEST) captured into replays

---

## Terminal model assumptions

- Default size: **80×24** unless specified
- Default TERM exposed by PTY layer: **xterm-256color**
- Locale enforced for deterministic captures: **LC_ALL=C, LANG=C**
- Rendering does not affect correctness verification; correctness is asserted on **buffer state**

---

## How to use this matrix

1. When you implement a feature, add/update a row with:
   - **Status**
   - **Evidence** (test path)
   - **Code ownership** (file(s)/module)
2. When a user reports a bug:
   - Add a row or update status to ⚠ Partial
   - Add a replay test reproducing it
3. Before releases:
   - Ensure “✅ Supported” rows have at least one automated verification link.

---

## 1) Input parsing & state machine

| Feature / Sequence | Spec / Notes | Status | Evidence | Ownership (code) | Known deviations |
|---|---|---:|---|---|---|
| C0 controls (BEL, BS, HT, LF, CR) | Basic control chars | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/TabSystemTests.cs`; Replay: `tests/Replays/...` | `Core/AnsiParser.cs`, `Core/TerminalBuffer.cs` | HT now follows stored tab stops instead of inserting spaces; broader C0 coverage is still only partially audited |
| C1 via 7-bit ESC (ESC @.._) | “7-bit C1” translation | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | Recognizes CSI/OSC/DCS/APC plus IND/NEL/RI; unsupported `ESC @.._` controls are ignored with recovery rather than fully implemented |
| 8-bit C1 bytes (0x80–0x9F) | If supported, must be explicit | ❌ Not supported | — | `Core/AnsiParser.cs` | (fill) |
| String terminators (ST = ESC \\, BEL) | OSC/APC termination rules | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | OSC accepts BEL and ST; DCS/APC also accept BEL as permissive recovery behavior, not strict spec compliance |
| Unknown sequence handling | Ignore/print/strict? | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs` | `Core/AnsiParser.cs` | Unknown `ESC @.._` sequences are ignored and parser resumes on the next valid escape or printable content |
| Error recovery on malformed sequences | Robustness | ⚠ Partial | Fuzz/Unit: `tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs`, `tests/Ntilde.App.Tests/AnsiCorpusReplayTests.cs` | `Core/AnsiParser.cs` | Malformed OSC/CSI/DCS/APC recover across chunk boundaries and nested ESC, but this is a best-effort parser policy rather than full conformance coverage |

---

## 2) Cursor movement & positioning (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| CUU/CUD/CUF/CUB (A/B/C/D) | Cursor up/down/forward/back | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/PendingWrapTests.cs` | Parser+Buffer | Explicit CUF movement is covered via pending-wrap reset; dedicated CUU/CUD/CUB targeted coverage is still missing |
| CUP / HVP (H/f) | Positioning, default params | ✅ Supported | Unit + Replay: `tests/Ntilde.App.Tests/CursorPositioningCompletionTests.cs`, `tests/Ntilde.App.Tests/ReplayTests/RegressionTests.cs` | Parser+Buffer | |
| CHA (G) | Horizontal absolute | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs` | Parser+Buffer | |
| CPL (F) | Previous line and column reset | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs` | Parser+Buffer | |
| CNL (E) | Next line and column reset | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs` | Parser+Buffer | |
| CHT (I) | Cursor forward tabulation | ✅ Supported | Unit: `tests/Ntilde.App.Tests/TabSystemTests.cs` | Parser+Buffer | |
| CBT (Z) | Cursor backward tabulation | ✅ Supported | Unit: `tests/Ntilde.App.Tests/TabSystemTests.cs` | Parser+Buffer | |
| VPA/HPA (d/G/`) | Absolute row/col | ✅ Supported | Unit: `tests/Ntilde.App.Tests/CursorPositioningCompletionTests.cs` | Parser+Buffer | |
| HPR/VPR (a/e) | Relative row/col | ✅ Supported | Unit: `tests/Ntilde.App.Tests/CursorPositioningCompletionTests.cs` | Parser+Buffer | |

---

## 3) Erase & insert/delete (CSI)

| Feature / CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| ED (J) | 0/1/2 erase display | ✅ Supported | Replay: `tests/Ntilde.App.Tests/ReplayTests/RegressionTests.cs`, `tests/Ntilde.App.Tests/Fixtures/Replay/vttest_cursor.rec`; Unit: `tests/Ntilde.Platform.Tests/Ssh/NativeSshTerminalParityTests.cs` | Parser+Buffer | |
| EL (K) | 0/1/2 erase line | ✅ Supported | Unit: `tests/Ntilde.Platform.Tests/Ssh/NativeSshTerminalParityTests.cs` | Parser+Buffer | |
| ICH ( @ ) | Insert chars | ⚠ Partial | Code path | Parser+Buffer | Implemented in parser/buffer; needs targeted unit coverage |
| DCH (P) | Delete chars | ⚠ Partial | Code path | Parser+Buffer | Implemented in parser/buffer; needs targeted unit coverage |
| IL (L) / DL (M) | Insert/delete lines | ⚠ Partial | Replay | Buffer | Scroll region interactions |
| ECH (X) | Erase chars | ⚠ Partial | Code path | Parser+Buffer | Implemented in parser/buffer; needs targeted unit coverage |
| REP (b) | Repeat preceding graphic character | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/RepeatCharacterTests.cs` | Parser+Buffer | Repeats only an immediately preceding graphic character (xterm rule); count clamped to one line |

---

## 4) Scrolling, margins, and origin mode

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| DECSTBM (CSI t;b r) | Set top/bottom margins | ⚠ Partial | VTTEST: scroll scenario | Parser+Buffer | |
| IND (ESC D) / RI (ESC M) | Index / Reverse index | ⚠ Partial | Replay | Parser+Buffer | |
| DECOM (origin mode) | Cursor relative to margins | ✅ Supported | Unit: `tests/Ntilde.App.Tests/DecModeTests.cs` | Parser+Buffer | |
| Wraparound DECAWM | Auto wrap | ⚠ Partial | Replay | Buffer | Wide glyph edge cases |
| Smooth scroll | Not required for correctness | 🚫 Won’t support | — | — | Renderer concern |

---

## 5) Screen buffers & modes (DEC private modes)

| Mode | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---|---:|---|---|---|
| Alternate screen | ?1049 / ?47 / ?1047 | Switch + save/restore cursor | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/AlternateScreenTests.cs`; Replay: `tests/Ntilde.App.Tests/ReplayTests/AlternateScreenReplayTests.cs`, `tests/Ntilde.App.Tests/ReplayTests/NativeSshReplayParityTests.cs` | Buffer | Main scrollback is preserved and alt-screen output never enters scrollback. `?47` reuses the existing alternate buffer/state without clearing; `?1047` and `?1049` clear and home the alternate buffer on entry. Nested/redundant alt-screen enters are treated as no-op, and a `?1049` save is consumed by the first exit from alt-screen regardless of whether that exit uses `?47l`, `?1047l`, or `?1049l`. |
| DECSTR (soft reset) | CSI ! p | Preserve contents, scrollback, active screen and current cursor position; reset margins, origin/insert/application-cursor modes, visibility, cursor presentation, SGR and character sets; reset the active saved cursor to home | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/SoftResetAndCursorSaveTests.cs`, `tests/Ntilde.App.Tests/RenderTests/CursorResetRenderTests.cs`; Replay: `tests/Ntilde.VT.Tests/SavedCursorReplayTests.cs`; Mux: `tests/Ntilde.Mux.Tests/Headless/HeadlessSoftResetTests.cs` | Parser+Buffer+App | Uses xterm-compatible autowrap-on defaults. Tabs, mouse/focus/paste/keyboard reporting, in-band resize and SIXEL display mode/metrics survive. Current pending wrap is cleared. Cursor shape/blink return to the presenting host preference through an optional default-appearance marker. Unsupported DEC hardware modes are outside the model. |
| Cursor save/restore (1048) | CSI ? 1048 h / CSI ? 1048 l | Aliases DECSC/DECRC: per-screen position, rendition, pending wrap, origin mode and G0-G3/GL; does not switch screens | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/SoftResetAndCursorSaveTests.cs`; Replay: `tests/Ntilde.VT.Tests/SavedCursorReplayTests.cs`; Mux: `tests/Ntilde.Mux.Tests/Headless/HeadlessSoftResetTests.cs` | Parser+Buffer+Replay | Shares the existing ESC 7/8 and 1049 cursor banks. Restores pending wrap while leaving the current DECAWM autowrap mode unchanged, matching xterm. New replay snapshots carry a complete terminal/parser cut including both screens and the armed 1049 restore; legacy snapshots use default banks. Mode 1048 is an action without a persistent set/reset flag, so DECRQM continues reporting 0. Legacy mux cursor snapshots omit the added mode/charset context and retain legacy restore behavior for those fields. |
| Show cursor | ?25 | | ✅ Supported | Unit: `tests/Ntilde.App.Tests/DecModeTests.cs`; Replay: `tests/Ntilde.App.Tests/ReplayTests/ReplayV2Tests.cs` | Buffer+Renderer | |
| Application cursor keys | ?1 | Impacts input mapping | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/Input/TerminalInputModeEncoderTests.cs`, `tests/Ntilde.App.Tests/CommandAssist/TerminalViewKeyHandlingTests.cs`; Unit/Code: `ReplayV2Tests` | Parser+Input | Unmodified arrows and Home/End switch between CSI and SS3 with the mode; with a modifier held they always use xterm's `CSI 1;m` form (e.g. Ctrl+Left `CSI 1;5D`), as xterm does, since SS3 has no parameter slot |
| Focus event reporting | ?1004 | Emits `CSI I` / `CSI O` on focus transitions | ⚠ Partial | Unit/Code: `DecModeTests`, `TerminalView` | Parser+Input | Mode flag tested; focus emission covered by app path, not headless UI test |
| Bracketed paste | ?2004 | Input feature | ⚠ Partial | Unit | Input layer | |
| Mouse reporting | ?1000/1002/1003/1006 etc | Click (`?1000`) and drag-motion (`?1002`) tracking, plus hover motion with no buttons held under any-event tracking (`?1003`, issue #269); SGR (`?1006`) and legacy X10 coordinate encodings both supported. Motion reports are coalesced to at most one per distinct terminal cell (tracked last-reported cell, reset on buffer attach/resize, alternate-screen switch, pointer exit, and `?1002`/`?1003` mode changes observed between two motion events) so a stationary-but-jittery pointer or a TUI toggling tracking modes doesn't flood the PTY | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/Input/TerminalInputModeEncoderTests.cs`, `tests/Ntilde.App.Tests/Input/TerminalViewMouseMotionTests.cs` | Input layer | `?1001` (highlight tracking) and urxvt (`?1015`)/pixel-position (`?1016`) mouse coordinate encodings are not implemented; only X10 and SGR (`?1006`) coordinate encodings are supported. Coordinates are viewport-relative and 1-based on every report path (motion, press, release, wheel); the legacy X10 encoding clamps coordinates to its 223 ceiling rather than promoting the report to the SGR form the application never requested |
| Cursor style | CSI Ps SP q | DECSCUSR block/beam/underline + blink state | ✅ Supported | Unit: `tests/Ntilde.App.Tests/OscUxTests.cs`, `tests/Ntilde.VT.Tests/CursorStyleInvalidationTests.cs`; App: `tests/Ntilde.App.Tests/Core/TerminalViewCursorBlinkTests.cs` | Parser+Renderer | Style-only changes invalidate once per batch. App timers honor explicit blinking/steady choices over host defaults until reset, while preserving focus/visibility/lifetime gates. |
| Color-scheme query | CSI ? 996 n | Contour dark/light preference | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/ColorSchemeReportingTests.cs`, `tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs`; App: `tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs` | Parser+App | Reports CSI ? 997 ; 1 n for dark or 2 for light, derived from the effective terminal background, including profile overrides. Uses the same perceived RGB luma threshold as UI theme classification; alpha is ignored and an unset background uses black. It reports the terminal preference, not the raw OS appearance. Persistent spawns carry an optional initial background before startup output; absent legacy values retain the dark fallback until presentation arrives. An updated daemon is needed for launch-time color replies. |
| Color-scheme notifications | CSI ? 2031 h/l, CSI ? 2031 $ p | Opt-in background preference updates | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/ColorSchemeReportingTests.cs`, `tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs`; App: `tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs`; Mux: `tests/Ntilde.Mux.Tests/Headless/HeadlessColorSchemeTests.cs` | Parser+App+Mux | Off by default. Only effective-background transitions between dark and light send a report; enabling, unchanged classification and unrelated settings are silent. DECRQM reports live subscription state. DECSTR preserves it, RIS clears it; complete parser snapshots retain it while host colors remain configuration. Shared-session replies originate in the daemon. Palette edits that do not change dark/light classification do not send updates. |
| Kitty keyboard protocol | CSI ? u / CSI > Pm u / CSI < Pm u / CSI = Pm ; Pm u | Progressive-enhancement flags: query, push, pop, set. Per-screen-buffer flag stacks (depth 32, oldest evicted on overflow); RIS clears both | ⚠ Partial | Unit: `tests/Ntilde.VT.Tests/KittyKeyboardProtocolTests.cs`, `tests/Ntilde.VT.Tests/KittyKeyboardEventTests.cs`, `tests/Ntilde.App.Tests/Input/KittyKeyEventEncodingTests.cs`, `tests/Ntilde.App.Tests/Input/KittyKeyEventRoutingTests.cs`, `tests/Ntilde.App.Tests/Input/KittyKeyboardEncodingTests.cs`, `tests/Ntilde.App.Tests/Input/KittyAlternateKeyRoutingTests.cs`, `tests/Ntilde.App.Tests/Input/KittyAllKeyRoutingTests.cs`, `tests/Ntilde.App.Tests/Input/KittyAssociatedTextRoutingTests.cs`, `tests/Ntilde.App.Tests/Input/KittyAssociatedTextEncodingTests.cs`, `tests/Ntilde.VT.Tests/KittyAssociatedTextProtocolTests.cs`, `tests/Ntilde.VT.Tests/KittyAllKeyProtocolTests.cs`, `tests/Ntilde.Platform.Tests/Ssh/NativeSshSessionTests.cs`, `tests/Ntilde.VT.Tests/KittyAlternateKeyProtocolTests.cs`, `tests/Ntilde.App.Tests/Controls/MuxPaneSharingTests.cs`, `tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs`, `tests/Ntilde.App.Tests/CommandAssist/TerminalViewKeyHandlingTests.cs` | Parser+Input | Scope: disambiguate escape codes (`0b1`), report event types (`0b10`), alternate keys (`0b100`), all keys (`0b1000`), and associated text (`0b10000`). Alternate-key fields enrich keys already encoded by another enhancement; flag 4 alone does not change legacy bytes. Available scalar framework symbols supply layout-aware primary/shifted values, and physical positions supply PC-101 base-layout values; equal or unavailable optional values are omitted. Shifted values appear only while Shift is held. Captured identity survives repeat/release and later layout changes. Alt-produced symbols are excluded from primary derivation. Missing/control/combining/format/multi-scalar/malformed symbols keep the legacy primary; shifted punctuation also keeps that primary because Avalonia exposes no unshifted layout glyph for that event. No native keyboard-map query is added; this does not claim complete layout normalization. Repeat/release events use modifier subparameters `:2`/`:3`; press is the default. Releases are paired with forwarded presses and discarded on focus/session/buffer/keyboard-stack changes; repeats retain the pressed logical key across layout changes. Tracking starts after the input call returns, but the existing fire-and-forget transport has no child-delivery acknowledgement and saturated input queues can lose events. Without flag 8, Enter/Tab/Backspace keep their unmodified initial recovery bytes, report negotiated repeats, and have no releases; ordinary text, IME and AltGr stay on TextInput. Flag 8 implies disambiguation and canonical ordinary/recovery/modifier/keypad/media key frames; paired releases still require flag 2. Left/right modifier releases use the current framework modifier mask. All-key mode and unhandled legacy text keydowns capture their flag state and one-shot text policy, so later ceiling/stack/kill-switch changes do not duplicate frames or lose legacy text; a matching key release or focus/session/buffer change clears correlation so a later text-only commit is admitted by current policy, while consumed legacy keys retain their prior text-only behavior. Generation tokens prevent nested callbacks from re-arming or overwriting a consumed/newer text decision. With flag 8 alone, unmatched plain text callbacks (including text-only IME commits) are suppressed. With flags 8+16, actual generated text is encoded as invariant scalar values in CSI-u frames; releases never carry text. Valid forwarded payloads notify the typing observer once for markless history and assist refresh; rejected payloads and key-only fallbacks do not. Text-producing keydowns remain unhandled for the native text callback, with one pending frame and one input-priority fallback job per view. If text arrives, the frame receives its actual payload; if no callback arrives, the key frame is flushed before the next key/release or at the next dispatcher turn. Late or unassociated text uses key code zero. Input-context changes drop unsent old-context frames; old-stack deferred keys cannot acquire a new-stack release. Complete malformed UTF-16 and all C0/C1 payload controls are rejected as a whole; combining/format scalars are allowed in text. Adjacent single-unit backend callbacks can complete one surrogate pair, retaining only one high unit and its original mode across packet key events; context reset or a new known key discards an incomplete fragment, and unmatched halves never become replacement payloads. Flag 16 without flag 8 has no text effect (the protocol leaves that combination undefined). Available key symbols supply primary identity even without flag 4, while missing/invalid metadata retains the existing logical fallback. Unavailable key codes and lock-state bits are not invented; a key without logical or physical identity cannot acquire a repeat/release slot. Undefined future bits (`0b100000` and above) are accepted but masked out on push/set, so the `CSI ? u` reply only advertises flags actually honored. Key encoding covers Esc, modified Enter/Tab/Backspace, and ctrl/alt/super combinations on the spec's legacy-text keys; keypad text remains text when flag 8 is absent. Without event or all-key reporting, functional keys keep their legacy encodings, which for arrows/Home/End/Insert/Delete/PgUp/PgDn/F1-F12 carry modifiers in xterm's PC-style form (e.g. Ctrl+Left is `CSI 1;5D`, Ctrl+Delete `CSI 3;5~`). With event reporting, supported functional keys use canonical CSI frames (F3 uses `13~` to avoid CPR collisions), independent of application cursor mode. AltGr on Windows non-US layouts is reported as Control+Alt; without flag 8, the encoder carves that combination out (falls through to legacy/text) so composed characters are not swallowed - the accepted trade-off is that literal Ctrl+Alt+&lt;key&gt; shortcuts on US layouts also lose kitty encoding, same as the pre-existing Alt-sends-ESC carve-out. Gated end-to-end by `TerminalSettings.EnableKittyKeyboardProtocol` (default on): when off, the App-side encoder is skipped unconditionally and the `CSI ? u` reply always reports flags 0 regardless of the pushed stack state. Persistent spawn requests carry the host keyboard ceiling before startup output is parsed, including the disabled setting; absent launch capabilities default to flag 1. Persistent sessions intersect the supported flag masks of interactive clients; old daemons/clients without the optional mask stay at flag 1, and read-only observers do not reduce capabilities. An attaching GUI receives its negotiated mask before the snapshot is offered; the GUI applies later capability notifications before subsequent output; raw requested stacks survive a temporary ceiling reduction. The tab-broadcast path in `MainWindow` builds its own input independently of this encoder and always sends legacy encoding to broadcast targets regardless of their own flag state. |
| DECRQM / DECRPM (mode query) | CSI Ps $ p / CSI ? Ps $ p → CSI [?] Ps ; Pm $ y | Reports live set/reset state for private modes 1, 6, 7, 25, 47, 80, 1000, 1002, 1003, 1004, 1006, 1047, 1049, 2004, 2026 and ANSI modes 4 (IRM), 12 (SRM), 20 (LNM); unrecognized modes report `Pm=0`. Lets apps (e.g. probing `?2026` synchronized output) detect real support instead of assuming it from unanswered queries | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/DecrqmTests.cs` | Parser | `?9001` (ConPTY passthrough) is accepted by `CSI ? Ps h/l` but has no tracked boolean state, so DECRQM reports it as `Pm=0` (not recognized) rather than guessing. Dispatch keys on the `$` intermediate exactly, so `CSI ! p` performs DECSTR without a reply; bare `CSI p` is ignored. |
| XTVERSION (terminal name/version) | CSI > q / CSI > 0 q → DCS > &#124; Ntilde &lt;version&gt; ST | Reports the terminal's name (matching the `TERM_PROGRAM=Ntilde` the PTY exports) and the build's version without `+` build metadata | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/XtVersionTests.cs` | Parser | Shares the `q` final byte with DECSCUSR (`CSI Ps SP q`); dispatch keys on the `>` leader with no intermediates, and Ps other than 0 stays silent. |
| Kitty in-band resize / CSI 14 t / CSI 16 t | Mode 2048, `CSI 48 ; rows ; cols ; heightPx ; widthPx t`, `CSI 14 t` -> `CSI 4 ; heightPx ; widthPx t`, `CSI 16 t` -> `CSI 6 ; cellHeightPx ; cellWidthPx t` | Terminal-to-child reports are written to stdin only while the client has set mode 2048 (unsolicited reports would be garbage on its stdin); pixel sizes come from the live cell metrics. This is the only resize signal clients like terminal-browser get on Windows (no SIGWINCH, no console polling) | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/InBandResizeTests.cs` | Parser+App | Other xterm window ops (stack, iconify, resize-by-cells, title push/pop via `t`) are ignored rather than answered. The `rows:1`/`rows:2` sub-parameter form of the start/end events is not emitted; plain numeric rows are, which in-band-resize clients parse identically. |
| DSR / CPR / DECXCPR | CSI Ps n / CSI ? Ps n | Operating status (`CSI 5 n` -> `CSI 0 n`) and cursor position. The bare request answers `CSI r ; c R`; the DEC private request `CSI ? 6 n` (DECXCPR) answers in the private form `CSI ? r ; c R`, because a client that asked the private question parses for a private answer and would otherwise drop the reply or mistake it for an unsolicited CPR. Both frames honour DECOM: with origin mode set the row is reported relative to the top of the scrolling region, matching the frame CUP used to set it | ✅ Supported | Unit: `tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs`, `tests/Ntilde.App.Tests/VttestScenarioTests.cs` | Parser | DEC's page parameter is omitted from the DECXCPR reply (`CSI ? r ; c R`, not `CSI ? r ; c ; p R`), as xterm omits it. The other private status reports - `?15` printer, `?25` UDK, `?26` keyboard, `?53` locator - are not implemented and stay silent rather than guessing. `CSI > Ps n` (xterm disable-key-modifiers) is not a status request and is ignored |

---

## 6) SGR attributes & colors

| Feature | CSI | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Basic SGR (0,1,2,3,4,5,7,9,22,23,24,25,27,29) | | Bold/dim/italic/underline/blink/reverse/strike | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/SgrAttributeTests.cs`; VTTEST: sgr scenario | Parser+Buffer+Renderer | Underline style/color tracked separately |
| 8/16 colors | 30–37/90–97, 40–47/100–107 | | ⚠ Partial | Replay | Parser+Buffer | |
| 256-color | 38;5;N / 48;5;N | | ⚠ Partial | Replay | Parser+Buffer | |
| Truecolor | 38;2;r;g;b / 48;2;r;g;b | | ⚠ Partial | Replay | Parser+Buffer | |
| Underline styles and color | 4:0-4:5, 21, 58, 59 | xterm, kitty | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/UnderlineStyleTests.cs`; render: `tests/Ntilde.App.Tests/RenderTests/UnderlineStyleRenderTests.cs`; export: `tests/Ntilde.VT.Tests/Export/TerminalExporterUnderlineTests.cs` | Parser+Buffer+Renderer | Straight/double/curly/dotted/dashed, own RGB or palette color (#270). The ANSI exporters (mux text client, export) write them in colon form, in a sequence of their own after the main SGR |

---

## 7) Tabs

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| HT (tab) movement | | ✅ Supported | Unit: `tests/Ntilde.App.Tests/TabSystemTests.cs` | Parser+Buffer | Custom tab stops are clipped on width shrink; columns exposed by width growth start with default 8-column tab stops |
| Tab stops set/clear | ESC H, CSI g | ✅ Supported | Unit: `tests/Ntilde.App.Tests/TabSystemTests.cs` | Parser+Buffer | `CSI g` supports current-stop clear (`0`/default) and clear-all (`3`); other parameters are ignored |

---

## 8) OSC sequences

| OSC | Purpose | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| OSC 0/2 | Set title | ⚠ Partial | Manual/Unit | App/UI | |
| OSC 7 | CWD reporting | ✅ Supported | Unit: `tests/Ntilde.App.Tests/OscUxTests.cs` | Parser+App | |
| OSC 10/11 | Foreground/background color set/query | ⚠ Partial | Unit: `tests/Ntilde.VT.Tests/RuntimeColorTests.cs`, `tests/Ntilde.App.Tests/OscUxTests.cs` | Parser+Buffer+Renderer | Numeric #RGB/#RRGGBB/#RRRGGGBBB/#RRRRGGGGBBBB, rgb: (1–4 hex digits/channel), finite rgbi: [0,1]; X11 named colors are not supported. Overrides affect existing default-colored cells, remain separate from saved host themes, and mask host color queries until reset. Query fallback remains fg C0C0C0 / bg 000000 when unset |
| OSC 4/104 | Palette set/query and reset | ⚠ Partial | Unit: `tests/Ntilde.VT.Tests/RuntimeColorTests.cs`, `tests/Ntilde.App.Tests/RenderTests/RuntimeColorRenderTests.cs` | Parser+Buffer+Renderer | All 256 indexed colors; numeric values only; multiple index/value pairs and per-index/all reset. At most 256 query replies per OSC; later mutations still apply after the reply cap. Explicit RGB cells stay unchanged; X11 names and special palette indices above 255 are not implemented |
| OSC 12/17/19 | Cursor and selection color set/query | ⚠ Partial | Unit: `tests/Ntilde.VT.Tests/RuntimeColorTests.cs`, `tests/Ntilde.App.Tests/RenderTests/RuntimeColorRenderTests.cs` | Parser+Buffer+Renderer | Numeric values only; unset derived selection colors have no concrete query reply. Other dynamic slots and OSC 21 are not implemented |
| OSC 110/111/112/117/119 | Reset runtime dynamic colors | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/RuntimeColorTests.cs` | Parser+Buffer | Empty payload resets the corresponding override to the current host default |
| OSC 30001 | Save session colors | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/RuntimeColorTests.cs`, `tests/Ntilde.Mux.Tests/Headless/HeadlessColorSchemeTests.cs`, `tests/Ntilde.App.Tests/RenderTests/RuntimeColorRenderTests.cs` | Parser+Buffer+Renderer | Session-wide across screens; newest 16 frames retained, oldest discarded on overflow, empty pop is silent. Saves foreground/background/cursor/selection overrides and all 256 palette overrides; unset values continue following the receiving/current host theme. DECSTR preserves; RIS clears; complete mux/replay snapshots carry deep copies. Host theme metadata remains separate |
| OSC 30101 | Restore session colors | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/RuntimeColorTests.cs`, `tests/Ntilde.Mux.Tests/Headless/HeadlessColorSchemeTests.cs`, `tests/Ntilde.App.Tests/RenderTests/RuntimeColorRenderTests.cs` | Parser+Buffer+Renderer | Session-wide across screens; newest 16 frames retained, oldest discarded on overflow, empty pop is silent. Saves foreground/background/cursor/selection overrides and all 256 palette overrides; unset values continue following the receiving/current host theme. DECSTR preserves; RIS clears; complete mux/replay snapshots carry deep copies. Host theme metadata remains separate |
| OSC 66 | Text sizing | ⚠ Partial | Unit: `tests/Ntilde.VT.Tests/Osc66ProtocolTests.cs`, `tests/Ntilde.VT.Tests/Osc66GridTests.cs`, `tests/Ntilde.VT.Tests/Osc66StateTests.cs`, `tests/Ntilde.VT.Tests/Osc66ReplayTests.cs`, pixels: `tests/Ntilde.App.Tests/RenderTests/Osc66RenderTests.cs` | Parser+Buffer+Renderer | `s` 1–7, `w` 0–7, `n`/`d` 0–15 with nonzero denominator greater than numerator, `v`/`h` 0–2; safe Unicode payload up to 4096 UTF-8 bytes and metadata up to 256 characters. Explicit width packs the entire payload into one block; automatic width scales graphemes individually. Fractions and alignment affect ink only. Immutable rectangular side metadata preserves the 12-byte cell ABI; an additional 16 MiB metadata budget bounds retained blocks. Overwrite, erase, shifts and line edits preserve or erase whole blocks; lower rows are skipped by ordinary text. Complete state transfer, legacy replay checkpoints and render snapshots preserve geometry, including a retained lower fragment whose anchor aged out. Rows containing blocks are physical layout barriers on resize; ordinary lines outside them reflow. Blocks clipped by the new physical width or too tall for the new screen are discarded whole. GUI rendering clips the scaled/shaped text and decorations per row; cursor keeps its ordinary cell shape. ANSI snapshot export emits OSC 66, while an unnegotiated mux text client uses a bounded normal-size fallback. No claim of exact font-pixel parity with Kitty. |
| OSC 52 | Clipboard | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/OscUxTests.cs`, `tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs` | Parser+App | Write-only (issue #268): valid base64 decodes and raises `AnsiParser.OnClipboardWrite`, capped at 1 MiB decoded (rejected on encoded length before decoding, re-checked after); invalid base64 dropped silently; a sequence with no payload separator at all is ignored rather than clearing the clipboard. Targets `c`/`p` both map to the system clipboard, other selection chars ignored. Query (`?`) always gets an empty-payload denial reply, never real clipboard contents — clipboard READ is unsupported by design (security). The echoed selection parameter is whitelisted to `c p q s 0-7` and length-capped first: responses go to the child process's stdin, so an unsanitized echo was a command-injection primitive (PR #280 review). Gated by `TerminalSettings.AllowOsc52ClipboardWrite` (default true, single global setting; per-profile/SSH-scoped opt-in is future work) |
| OSC 8 | Hyperlinks | ✅ Supported | Unit: `tests/Ntilde.App.Tests/OscUxTests.cs` | Parser+Renderer+UI | Ctrl-click open path is app-level |
| OSC 9 / 9;4 | Desktop notification / progress reporting | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/Osc9NotificationProgressTests.cs`, `tests/Ntilde.App.Tests/TerminalProgressPresentationTests.cs` | Parser+App | #271. `9;<text>` flattens/caps (200 chars) and raises `OnDesktopNotification`; `9;4;<state>[;<pct>]` clamps pct 0-100 and raises `OnProgressReported` (state 0-4, pct null when omitted; malformed payloads dropped; bare `4` without `;` is notification text). App side: focus-gated in-app toast (`Osc9NotificationsEnabled`, default false, same policy as long-command notifications), per-tab progress — thin bar in vertical headers, trailing suffix in horizontal titles — and Windows taskbar progress mirroring the selected tab (`Osc9ProgressReportingEnabled`, default true). Progress withdraws on state 0, pane exit, and pane close |
| OSC 133 | Shell integration lifecycle | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/OscShellIntegrationTests.cs` | Parser+Command Assist | Supports A/B/C/D markers; C's command from base64, plain text or kitty's `cmdline_url` (fish 4); other semantic prompt extensions not audited |
| OSC 1337 | iTerm2 inline images | ✅ Supported | Unit: `tests/Ntilde.Rendering.Tests/InlineImageEndToEndTests.cs`, Manual: `docs/qa/QA_GRAPHICS.md` | Parser+Renderer | Requires `inline=1` and the `File=` arg shape; no `name=`/`size=` enforcement |
| OSC 1339 | Windows conpty tunnel | 🧪 Experimental | Manual | Parser+Win | |

---

## 9) Kitty graphics / APC

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Kitty graphics protocol | APC / OSC forms | ✅ Supported | Unit: `tests/Ntilde.App.Tests/GraphicsTests.cs`, `tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs`, `tests/Ntilde.Rendering.Tests/InlineImageEndToEndTests.cs` | Parser+Renderer | Native APC (non-tunneled) on Windows decodes behind `TerminalSettings.AllowNativeKittyGraphics` (default on) — historically probes got `ERR`/images skipped; modern ConPTY passes APC through intact (live-verified 2026-09). Payload formats: container (PNG/...) plus raw `f=24`/`f=32` sized by `s=`/`v=`, `o=z` zlib inflation, and `t=f` file transport read through the host-injected `ReadFileBytes` delegate (App-side reader confined to `%TEMP%`, 64 MB cap). `t=s` (shm) skips gracefully. Placement params (`z`, `U=1`, `p=1`, `C=1`) are accepted but only cursor-anchored placement is implemented. |
| Placement, z-index, scrolling | Complex interactions | ⚠ Partial | Manual | Buffer+Renderer | Pruned images' bitmaps are retired by the buffer; the owning view disposes them once no in-flight snapshot session predates the retire (#166) |

---

## 10) SIXEL

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| SIXEL decode/render | Wired via `Ntilde.Rendering.SkiaImageDecoder` | ✅ Supported | Unit: `tests/Ntilde.Rendering.Tests/SixelDecoderTests.cs`, `tests/Ntilde.Rendering.Tests/SkiaImageDecoderTests.cs` | Decoder+Renderer | |
| SIXEL scrolling behavior | DECSDM (`?80`), xterm polarity | ✅ Supported | Unit: `tests/Ntilde.VT.Tests/SixelScrollingTests.cs`, `tests/Ntilde.Mux.Tests/Headless/HeadlessSixelScrollingTests.cs`, `tests/Ntilde.App.Tests/RenderTests/SixelScrollingRenderTests.cs` | Parser+Buffer+Renderer | Default scrolling reserves rows without writing text cells or wrapping wide images. Set `?80` places at page origin without moving the text cursor; reset restores scrolling. Regional and alternate-screen scrolling clip discarded pixels; right-edge clipping survives widening. Shared sessions receive initial pixel metrics at spawn and ordered updates afterward; GUI panes decode new SIXELs while the daemon measures geometry. Attach snapshots restore retained SIXEL source, placement and clipping for both screens without moving the cursor. Per pane/session, source retention is capped at 256 entries and 2 Mi UTF-16 characters; oldest or oversized sources are omitted when this budget is reached. Active captures share an additional bounded source-reference allowance of 256 entries and 2 Mi characters (at most 8 MiB retained payloads plus capture pins), preserving copied undecoded images through their final draw. Updated GUI/daemon required for shared layout parity. Existing below-image cursor and immediate trailing-newline policy is retained. Cursor-right extension `?8452` is not implemented; placement remains cell-aligned. |

---

## 11) Clipboard, selection, and hyperlinking

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| Selection model | UI behavior | ⚠ Partial | Manual | UI | |
| Copy on select | Configurable | ❌ Not supported | — | UI | |
| Hyperlinks | OSC 8 | ✅ Supported | Unit: `tests/Ntilde.App.Tests/OscUxTests.cs` | Parser+UI | Ctrl-click open path is app-level |

---

## 12) Unicode width, graphemes, and font behavior

| Feature | Notes | Status | Evidence | Ownership | Known deviations |
|---|---|---:|---|---|---|
| wcwidth-like width | CJK/emoji width | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/WidthTests.cs`, `tests/Ntilde.App.Tests/UnicodeWidthModelV2Tests.cs`; Replay: `tests/Ntilde.App.Tests/Fixtures/Replay/mixed_unicode.rec` | Buffer+Renderer | Deterministic 0/1/2-cell model for combining marks, emoji modifiers, ZWJ emoji, variation selectors, and regional-indicator flags. No Unicode-version pin or full UAX #11 conformance table is documented yet. |
| Combining marks | Grapheme clusters | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/GraphemeAttachmentTests.cs`, `tests/Ntilde.App.Tests/SurrogateTests.cs`, `tests/Ntilde.App.Tests/UnicodeWidthModelV2Tests.cs`, `tests/Ntilde.App.Tests/ScrollAndWrapCorrectnessTests.cs` | Buffer+Renderer | Combining/variation attachment is covered for common terminal cases, including pending-wrap boundaries. Full extended-grapheme-cluster conformance is not claimed. |
| ZWJ emoji sequences | | ⚠ Partial | Unit: `tests/Ntilde.App.Tests/GraphemeAttachmentTests.cs`, `tests/Ntilde.App.Tests/WidthTests.cs`, `tests/Ntilde.App.Tests/UnicodeWidthModelV2Tests.cs` | Buffer+Renderer | Chunked ZWJ families, emoji modifiers, VS15/VS16, and chunked regional-indicator flag pairs are covered. Remaining gaps: cursor-addressing CSI remains cell-oriented, and width-changing selectors that arrive after a base glyph already placed at the last column are not guaranteed to retroactively reflow. |

---

## 13) Verification inventory

### External suites
- VTTEST capture adapter: `tests/Ntilde.ExternalSuites/`  
  Recordings: `tests/Replays/Vttest/*.rec`

### Replay suites (goldens)
- Add all `.rec/.snap` pairs under: `Ntilde.Tests/Fixtures/Replay/`

### Unit tests
- Parser/Buffer tests under: `Ntilde.Tests/`

---

## 14) Maintenance rules (non-negotiable)

1. **No “✅ Supported” without evidence.**
2. Evidence must be a stable path in the repo.
3. If behavior differs from xterm/wezterm, document it under “Known deviations”.
4. When you fix a deviation, update the row and add/adjust tests.
5. Every new OSC/APC/CSI feature must add a row here.

---

## 15) Roadmap linkage

When a feature is planned but not implemented, add a row with:
- Status: ❌ Not supported
- Evidence: “Planned”
- Link: roadmap item / issue number

Example:
- `❌ Not supported` → “Roadmap: M4 Font & Text Excellence”
