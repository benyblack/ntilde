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
| `T3` | 24BIT: compatibility and full colon-form SGR | 3 | `AnsiParser.Sgr.cs` (`38;2;r;g;b`, `38:2:<cs>:R:G:B`); SGR RGB/truecolor suites; XTGETTCAP `RGB`/`Tc`/`setrgbf`/`setrgbb`. [Literal color tests](../../../tests/Ntilde.VT.Tests/TruecolorLiteralStorageTests.cs) |
| `Cw` | CLIPBOARD_WRITABLE (conditional) | present | OSC 52 write accepted by default (`AllowOsc52ClipboardWrite`); answered only while `ClipboardWriteAvailable` is true — the same gate as XTGETTCAP `Ms` (`RequiresClipboardWrite`), so headless, persistent and setting-disabled sessions omit it. [Reply implementation](../../../src/Ntilde.VT/AnsiParser.CapabilityReport.cs) · [VT reply tests](../../../tests/Ntilde.VT.Tests/ITerm2CapabilitiesReplyTests.cs) |
| `Lr` | DECSLRM | present | `ModeState.IsLeftRightMarginMode`, `SetLeftRightMargins`; margins tests (`DecrqmTests`, XTGETTCAP `Enmg`/`Dsmg`/`Clmg`/`Cmg`). [Margin tests](../../../tests/Ntilde.VT.Tests/LeftRightMarginTests.cs) |
| `M` | MOUSE | present | DECSET 1000/1002/1003 and the required SGR 1006 encoding (`ModeState`, `TerminalView.Pointer`, SGR-mouse qualification). [Mouse tests](../../../tests/Ntilde.App.Tests/MouseReportingTests.cs) |
| `Sc7` | DECSCUSR: modes 1–4, 5–6, mode-0 reset | 7 | `ApplyCursorStyle` (0–6); `CursorStyleInvalidationTests`, DECRQSS `SP q` report. [Cursor tests](../../../tests/Ntilde.VT.Tests/CursorStyleInvalidationTests.cs) |
| `U` | UNICODE_BASIC | present | grapheme/width pipeline and corpus tests. [Width tests](../../../tests/Ntilde.VT.Tests/UnicodeWidthTests.cs) · [Grapheme rendering](../../../tests/Ntilde.App.Tests/RenderTests/GraphemeClusterDrawPathTests.cs) |
| `Ts3` | TITLES: setting and stacks | 3 | OSC 0/1/2 (`OscTitleTests`) and CSI 22/23 t stacks (`XtWinOpsTitleStackTests`). [Titles](../../../tests/Ntilde.VT.Tests/OscTitleTests.cs) · [Stacks](../../../tests/Ntilde.VT.Tests/XtWinOpsTitleStackTests.cs) |
| `B` | BRACKETED_PASTE | present | DECSET 2004 and paste-terminator sanitization (`TerminalInputSender.PreparePaste` removes ESC and C1 CSI so no early `CSI 201 ~` can be assembled). [Paste](../../../tests/Ntilde.App.Tests/Input/TerminalInputSenderTests.cs) |
| `F` | FOCUS_REPORTING and FILE (the specification assigns both one code) | present | DECSET 1004 answered `CSI I`/`CSI O` (`TerminalInputModeEncoder`); OSC 1337 inline images (`DecoderIndependentImagePlacementTests` and the image family). Non-inline file downloads are outside the implemented image path. [Focus](../../../tests/Ntilde.App.Tests/Input/TerminalInputModeEncoderTests.cs) · [Image placement](../../../tests/Ntilde.VT.Tests/DecoderIndependentImagePlacementTests.cs) · [Non-inline cases](../../../tests/Ntilde.VT.Tests/ITerm2MultipartImageTests.cs) |
| `Gs` | STRIKETHROUGH | present | SGR 9 stored and rendered; DECRQSS SGR echo. [Attributes](../../../tests/Ntilde.App.Tests/SgrAttributeTests.cs) · [Drawing](../../../src/Ntilde.App/Shell/TerminalDrawOperation.cs) |
| `Go` | OVERLINE | present | SGR 53 stored and rendered; DECRQSS SGR echo. [Overline](../../../tests/Ntilde.VT.Tests/OverlineTests.cs) · [Drawing](../../../src/Ntilde.App/Shell/TerminalDrawOperation.cs) |
| `Sy` | SYNC | present | DECSET 2026 / DECRQM (`DecrqmTests`, `SynchronizedRenderingTests`; XTGETTCAP `Sync`). [Sync tests](../../../tests/Ntilde.App.Tests/Core/SynchronizedRenderingTests.cs) |
| `H` | HYPERLINKS | present | OSC 8 (`Osc8*Tests`; XTGETTCAP `H`). [Link tests](../../../tests/Ntilde.VT.Tests/Osc8HyperlinkIdentityTests.cs) |
| `No` | NOTIFICATIONS | present | OSC 9 (`Osc9NotificationProgressTests`). Visible toasts depend on opt-in and focus. [Parser tests](../../../tests/Ntilde.VT.Tests/Osc9NotificationProgressTests.cs) · [App routing](../../../src/Ntilde.App/MainWindow.PaneEvents.cs) |
| `P` | PROGRESS | present | OSC 9;4 (`HandleOscNotificationOrProgress`). The App routes progress to the pane. [Parser tests](../../../tests/Ntilde.VT.Tests/Osc9NotificationProgressTests.cs) · [App routing](../../../src/Ntilde.App/MainWindow.PaneEvents.cs) |

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

## Direct-local declaration assessment

The [2026-11-27 owned pair](2026-11-27-rich-term-features/report.md) establishes that a clean
macOS direct-local child gets no `TERM_FEATURES`, while a child launched with the sentinel
inherits it verbatim. Each child independently requested the capability string on its own PTY.
Both replies contained the same 14 codes with clipboard writes disabled.

The [publication and encoding rules](https://iterm2.com/feature-reporting/#publication) assign the
same feature string to both mechanisms and say to omit `Cw` when writes are disabled. The
specification spells its request `Capabilities`. The [#1031 implementation](https://github.com/benyblack/ntilde/pull/1031)
and these observations use the pinned collector's `RequestCapabilities` spelling.

**Defer a Ntilde-authored launch value.** A running child's inherited environment
cannot follow the pane's live permission. Advertising `Cw` at launch would outlast revocation.
Permanently omitting it would diverge from the enabled reply. The direct pane regression queries
the same parser with writes enabled, disabled and enabled again and checks all three complete
replies. The two desktop runs exercised the disabled state. [App gate](../../../src/Ntilde.App/Controls/TerminalPane.Session.cs) ·
[Pane regression](../../../tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs).

## Limits

The reply is static per host-acceptance state: it advertises implemented capabilities, and
advertisement is not a per-invocation behavioral guarantee — each code's behavior continues to be
owned by its own sequence implementations and tests. No rendering, pixels, remote/mux
presentation, common-harness or score claim is made here; the owned qualification of the pinned
probe and the effect contract lives in the capabilities qualification page.
