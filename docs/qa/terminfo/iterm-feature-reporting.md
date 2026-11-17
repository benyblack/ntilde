# iTerm2 feature reporting: RequestCapabilities and the declared feature list

Implements the response half of the [iTerm2 feature-reporting specification](https://iterm2.com/feature-reporting)
for the exact request the pinned `terminfo.dev@4.0.0` probe sends: `OSC 1337 ; RequestCapabilities`
(BEL or ST terminated) is answered with one `OSC 1337 ; Capabilities = {FeatureString} ST` reply,
always ST-terminated as the specification writes the response, changing no screen, cursor or mode
state. The VT owns the reply (`src/Ntilde.VT/AnsiParser.CapabilityReport.cs`); the declaration is a
fixed literal per state, not a composed generic mechanism.

## The declared codes

`{FeatureString}` concatenates the specification's short codes. Ntilde declares only codes whose
capability is implemented with owning tests; the specification's table order decides the spelling.
Codes without implementation evidence stay out, and a measured failure is never advertised:

| Code | Feature | Declared value | Owning evidence (selection) |
| --- | --- | --- | --- |
| `T3` | 24BIT: compatibility and full colon-form SGR | 3 | `AnsiParser.Sgr.cs` (`38;2;r;g;b`, `38:2:<cs>:R:G:B`); SGR RGB/truecolor suites; XTGETTCAP `RGB`/`Tc`/`setrgbf`/`setrgbb` |
| `Cw` | CLIPBOARD_WRITABLE (conditional) | present | OSC 52 write accepted by default (`AllowOsc52ClipboardWrite`); answered only while `ClipboardWriteAvailable` is true — the same gate as XTGETTCAP `Ms` (`RequiresClipboardWrite`), so headless, persistent and setting-disabled sessions omit it |
| `Lr` | DECSLRM | present | `ModeState.IsLeftRightMarginMode`, `SetLeftRightMargins`; margins tests (`DecrqmTests`, XTGETTCAP `Enmg`/`Dsmg`/`Clmg`/`Cmg`) |
| `M` | MOUSE | present | DECSET 1000/1002/1003 and the required SGR 1006 encoding (`ModeState`, `TerminalView.Pointer`, SGR-mouse qualification) |
| `Sc7` | DECSCUSR: modes 1–4, 5–6, mode-0 reset | 7 | `ApplyCursorStyle` (0–6); `CursorStyleInvalidationTests`, DECRQSS `SP q` report |
| `U` | UNICODE_BASIC | present | grapheme/width pipeline and corpus tests |
| `Ts3` | TITLES: setting and stacks | 3 | OSC 0/1/2 (`OscTitleTests`) and CSI 22/23 t stacks (`XtWinOpsTitleStackTests`) |
| `B` | BRACKETED_PASTE | present | DECSET 2004 and paste-terminator sanitization (`TerminalInputSender.PreparePaste` removes ESC and C1 CSI so no early `CSI 201 ~` can be assembled) |
| `F` | FOCUS_REPORTING and FILE (the specification assigns both one code) | present | DECSET 1004 answered `CSI I`/`CSI O` (`TerminalInputModeEncoder`); OSC 1337 inline images (`DecoderIndependentImagePlacementTests` and the image family) |
| `Gs` | STRIKETHROUGH | present | SGR 9 stored and rendered; DECRQSS SGR echo |
| `Go` | OVERLINE | present | SGR 53 stored and rendered; DECRQSS SGR echo |
| `Sy` | SYNC | present | DECSET 2026 / DECRQM (`DecrqmTests`, `SynchronizedRenderingTests`; XTGETTCAP `Sync`) |
| `H` | HYPERLINKS | present | OSC 8 (`Osc8*Tests`; XTGETTCAP `H`) |
| `No` | NOTIFICATIONS | present | OSC 9 (`Osc9NotificationProgressTests`) |
| `P` | PROGRESS | present | OSC 9;4 (`HandleOscNotificationOrProgress`) |

Omitted, with the reason recorded rather than silently dropped: `Aw` (ambiguous-width characters
rendered full-width — not implemented), `Uw` (a claimed Unicode width-table version — no
version-pinned table evidence), `Sx` (SIXEL — measured unsupported, retained raster-fidelity
failure). An application that parses the string per the specification reads every declared code;
nothing else is claimed.

## What is not a request

Only the exact payload `1337;RequestCapabilities` is a request. `1337;Capabilities`,
an unsolicited `Capabilities=…` assignment, a query spelling, any other case, and any other
`1337;` payload stay unrouted (silent) — the anti-fabrication regression from the
[TERM_FEATURES audit](2026-11-17-term-features-policy.md) pins this. The image (`1337;File=`,
multipart) and cell-size (`1337;ReportCellSize`) forms keep their own handlers.

## Relationship to the environment-variable half

The specification publishes the same feature string through the `TERM_FEATURES` environment
variable. This implementation adds only the control-sequence reply; the
[`device.term-features` policy audit](2026-11-17-term-features-policy.md) records that the
environment variable stays undeclared — the native PTY layer never writes it, and the pinned
probe for that ID reads a collector environment the terminal cannot influence, so its
inapplicable/deferred disposition is unchanged by this batch.

## Limits

The reply is static per host-acceptance state: it advertises implemented capabilities, and
advertisement is not a per-invocation behavioral guarantee — each code's behavior continues to be
owned by its own sequence implementations and tests. No rendering, pixels, remote/mux
presentation, common-harness or score claim is made here; the owned qualification of the pinned
probe and the effect contract lives in the capabilities qualification page.
