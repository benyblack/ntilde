# Ntilde Core TUI baseline evidence map — 2026-11-04

An offline reconciliation of the **80 displayed Core rows**, not a new terminal run or published terminfo.dev compliance score.

Pinned [upstream catalogue](https://github.com/beorn/terminfo.dev/blob/2ee594d336a6ac8030e6bc0d938f22361d5e7447/content/features.json); Ntilde source/test references are frozen at `9648f5171b2d383f4954e2bcf6170c2b92e3f98e`. The website's 80-row list includes TERM, which the [pinned current scorer](https://github.com/beorn/terminfo.dev/blob/2ee594d336a6ac8030e6bc0d938f22361d5e7447/packages/probe-defs/src/environment-groups.ts) treats as manual/ungraded: **79 graded capabilities + 1 manual environment row**.

## Readout

| Measure | Value |
|---|---:|
| Frozen stock Supported | 30 |
| Frozen stock Unsupported | 0 |
| Frozen stock Inconclusive | 49 |
| Not collected: manual TERM | 1 |
| Rows with an additional passed owned case | 52 |
| Rows with an additional failed owned case | 1 |
| Union of stock Supported or a passed owned variant | 75 |

The union is a count of rows with some positive evidence, **not a compliance numerator**. Overlaps count once; one row may retain both positive and negative evidence. Variant inputs, cell flags, physical pixels, history and query replies are distinct scopes. No auxiliary case changes a stock grade.

30/80 is 37.5% of the displayed list. Excluding the one manual environment row gives 30/79 = 38.0% (38% rounded). These describe the frozen stock supported share only. This older collector suite and the current catalogue do not share a common execution plan, so neither fraction is an official or comparable overall Core score.

## Preserved boundaries

- **Bold:** the stored SGR 1 flag passes while the measured explicit-RGB glyph comparison fails. The later [bold correction and deterministic tests](bold-glyphs.md) are source evidence, not a replacement owned run. The appearance negative is not automatically a failure of the narrower Core cell-flag criterion.
- **DECSCNM:** private mode 5 is unrecognized in the retained reply; SGR 7 inverse is a different feature. The tracked-mode source has no private-mode-5 implementation. This is a source limitation, not a rewritten stock Unsupported grade.
- **OSC 1:** App/VT has no separate icon-name state/readback. Title changes from OSC 0/2 do not prove it.
- **reset():** this is a Termless backend API requirement. A desktop app cannot qualify it by sending RIS, which has its own row.
- **TERM:** manual environment verification remains uncollected. A source assignment of xterm-256color does not attest the measured child's environment.
- **Known exact-fixture gaps:** owned EL text cases do not observe EL's green erase background; bounded four-line history does not meet the current ten-extra-line count criterion; separate margin-reset and accumulation cases do not prove the joined reset-plus-overflow fixture. Other scoped variants are stated per row.

## Stock partition by area

| Area | Rows | Supported | Inconclusive | Not collected |
|---|---:|---:|---:|---:|
| Styling and colors | 16 | 0 | 16 | 0 |
| Cursor | 15 | 13 | 2 | 0 |
| Erasure | 10 | 0 | 10 | 0 |
| Screen modes | 4 | 2 | 2 | 0 |
| History and scrolling | 9 | 0 | 9 | 0 |
| Text and controls | 15 | 12 | 3 | 0 |
| Unicode tab stops | 1 | 1 | 0 | 0 |
| Reset | 4 | 2 | 2 | 0 |
| Character sets | 4 | 0 | 4 | 0 |
| Environment | 1 | 0 | 0 | 1 |
| Icon name | 1 | 0 | 1 | 0 |

## Retained measurement

[Published run](2026-11-04-reset-observations.md) and [complete evidence archive](2026-11-04-reset-observations-evidence.zip): `65c6c0bd9d1c10793a084e1e79f9f9eb`, app/adapter source `dc4e2c297d9bcbf89ab0d3d34b3dfacc2fdec4d6`, macOS arm64, pinned terminfo.dev@4.0.0. The archive SHA-256 is `d3d85d29f5d90c17ef8dfb62739d94b29ce46018b258534c0c038e9fa3a7992d`.

The measured identity remains unverified and suiteComplete remains false. The full stock result remains **82 supported / 0 unsupported / 173 inconclusive**. Reverse-wrap's ungraded geometry diagnostic is retained in JSON. No Windows/Linux live-runtime claim or independent binary/source attestation is added.

The generator verifies archive bytes and resolves original case IDs/results; it does not execute archived JavaScript or reconstruct the original desktop. [Whole-archive reproduction](2026-11-04-metadata-audit.md) describes the separate source/journal/PNG verification. Every negative in the archive remains listed in JSON, including the non-Core empty-title observation.

## The 80 rows

Owned references below name the original report and case inside the archive. The JSON supplies its exact /observations/index pointer; the suite inventory supplies report hashes and scope. **Source references are inspected code/tests at the audit revision, not test-run results.** Each row also retains the verbatim pinned upstream requirement in JSON.

### Styling and colors

Source references: [bold-glyphs.md](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/docs/qa/terminfo/bold-glyphs.md), [AnsiParser.Sgr.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.Sgr.cs), [BoldGlyphPixelTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/RenderTests/BoldGlyphPixelTests.cs), [SgrAttributeTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/SgrAttributeTests.cs), [RuntimeColorTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.VT.Tests/RuntimeColorTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Bold (SGR 1)](https://terminfo.dev/sgr/1-bold)<br>`sgr.bold` | inconclusive | [sgr](#suite-sgr) `sgr.bold`: **passed**; [pixel](#suite-pixel) `rendition.bold`: **failed** | Live cell bold flag passed, but the same measured build failed the explicit-RGB bold glyph comparison. A later source fix has deterministic pixel regressions; a fresh owned appearance run is still required. The Core cell-flag criterion and glyph appearance are separate claims. |
| [Faint/dim (SGR 2)](https://terminfo.dev/sgr/2-faint-dim)<br>`sgr.faint` | inconclusive | [rendition](#suite-rendition) `sgr.faint`: **passed**; [pixel](#suite-pixel) `rendition.faint`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Italic (SGR 3)](https://terminfo.dev/sgr/3-italic)<br>`sgr.italic` | inconclusive | [sgr](#suite-sgr) `sgr.italic`: **passed**; [pixel](#suite-pixel) `rendition.italic`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Underline (SGR 4)](https://terminfo.dev/sgr/4-underline)<br>`sgr.underline.single` | inconclusive | [sgr](#suite-sgr) `sgr.underline`: **passed**; [pixel](#suite-pixel) `rendition.underline.1`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Inverse video (SGR 7)](https://terminfo.dev/sgr/7-inverse-video)<br>`sgr.inverse` | inconclusive | [sgr](#suite-sgr) `sgr.inverse`: **passed**; [pixel](#suite-pixel) `rendition.reverse`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Hidden/invisible (SGR 8)](https://terminfo.dev/sgr/8-hidden)<br>`sgr.hidden` | inconclusive | [rendition](#suite-rendition) `sgr.hidden`: **passed**; [pixel](#suite-pixel) `rendition.hidden`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Strikethrough (SGR 9)](https://terminfo.dev/sgr/9-strikethrough)<br>`sgr.strikethrough` | inconclusive | [rendition](#suite-rendition) `sgr.strikethrough`: **passed**; [pixel](#suite-pixel) `rendition.strike`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Standard fg colors (SGR 30-37)](https://terminfo.dev/sgr/30-37-standard-fg)<br>`sgr.fg.standard` | inconclusive | [sgr](#suite-sgr) `sgr.fg.standard`: **passed** | Owned cells cover all eight foreground indices; the pixel palette/RGB case is broader supporting evidence, not per-index pixel parity with another terminal. |
| [Standard bg colors (SGR 40-47)](https://terminfo.dev/sgr/40-47-standard-bg)<br>`sgr.bg.standard` | inconclusive | [sgr](#suite-sgr) `sgr.bg.standard`: **passed** | Owned cells cover all eight background indices; pixel/theme behavior remains a distinct qualification scope. |
| [Default fg color (SGR 39)](https://terminfo.dev/sgr/39-default-fg)<br>`sgr.fg.default` | inconclusive | [sgr](#suite-sgr) `sgr.fg.default`: **passed** | Owned indexed/default flags are measured; the upstream neutral/colored/reset comparison uses a different fixture. |
| [Default bg color (SGR 49)](https://terminfo.dev/sgr/49-default-bg)<br>`sgr.bg.default` | inconclusive | [sgr](#suite-sgr) `sgr.bg.default`: **passed** | Owned indexed/default flags are measured; the upstream neutral/colored/reset comparison uses a different fixture. |
| [Reset bold/dim (SGR 22)](https://terminfo.dev/sgr/22-reset-bold)<br>`sgr.selective-reset.bold` | inconclusive | [sgr](#suite-sgr) `sgr.selective-reset.bold`: **passed**; [rendition](#suite-rendition) `sgr.selective-reset.faint`: **passed** | The archived mixed-style bold-reset and separate faint-reset cases pass. The upstream combined bold+dim+italic fixture is a different exact input; retain the separate cases rather than claiming that exact fixture ran. |
| [Reset underline (SGR 24)](https://terminfo.dev/sgr/24-reset-underline)<br>`sgr.selective-reset.underline` | inconclusive | [sgr](#suite-sgr) `sgr.selective-reset.underline`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Reset inverse (SGR 27)](https://terminfo.dev/sgr/27-reset-inverse)<br>`sgr.selective-reset.inverse` | inconclusive | [sgr](#suite-sgr) `sgr.selective-reset.inverse`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Reset italic (SGR 23)](https://terminfo.dev/sgr/23-reset-italic)<br>`sgr.selective-reset.italic` | inconclusive | [sgr](#suite-sgr) `sgr.selective-reset.italic`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Reset all (SGR 0)](https://terminfo.dev/sgr/0-reset)<br>`sgr.reset` | inconclusive | [sgr](#suite-sgr) `sgr.reset`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |

### Cursor

Source references: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.Csi.cs), [CursorPositioningCompletionTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/CursorPositioningCompletionTests.cs), [DecModeTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/DecModeTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Hide cursor (DECTCEM)](https://terminfo.dev/cursor/dectcem-hide-cursor)<br>`cursor.hide` | inconclusive | [cursor](#suite-cursor) `cursor.visibility`: **passed**; [pixel](#suite-pixel) `cursor.hidden`: **passed** | Both live visibility state and shown/hidden cursor pixels pass in the owned fixture. This differs from the frozen stock 3x6 fixture precondition. |
| [Cursor position (CUP)](https://terminfo.dev/cursor/cup-cursor-position)<br>`cursor.move.absolute` | supported | [cell](#suite-cell) `cursor.move.absolute`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Cursor up (CUU)](https://terminfo.dev/cursor/cuu-cursor-up)<br>`cursor.move.up` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Cursor down (CUD)](https://terminfo.dev/cursor/cud-cursor-down)<br>`cursor.move.down` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Cursor forward (CUF)](https://terminfo.dev/cursor/cuf-cursor-forward)<br>`cursor.move.forward` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Cursor back (CUB)](https://terminfo.dev/cursor/cub-cursor-back)<br>`cursor.move.back` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Cursor home](https://terminfo.dev/cursor/home)<br>`cursor.move.home` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Save/restore cursor (DECSC)](https://terminfo.dev/cursor/decsc-save-restore-cursor)<br>`cursor.save-restore` | supported | [saved](#suite-saved) `saved.cursor.alternate-dec`: **passed**; [saved](#suite-saved) `saved.cursor.main-ansi`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [CUP at screen boundaries](https://terminfo.dev/cursor/cup-screen-boundaries)<br>`cursor.cup-boundaries` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [CUU stops at top](https://terminfo.dev/cursor/cuu-past-top)<br>`cursor.cuu-past-top` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [CUD stops at bottom](https://terminfo.dev/cursor/cud-past-bottom)<br>`cursor.cud-past-bottom` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [CUP with DECOM](https://terminfo.dev/cursor/cup-decom-scroll-region)<br>`cursor.cup-scroll-region` | inconclusive | [cell](#suite-cell) `cursor.cup-scroll-region`: **passed**; [history](#suite-history) `history.margins.origin-home`: **passed** | Owned physical cursor/cell observations resolve origin-relative positioning in a small region. They are not a rerun of the upstream 5;15 fixture. |
| [VPA vertical position absolute (CSI d)](https://terminfo.dev/cursor/vpa-vertical-position-absolute)<br>`cursor.vpa` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [CPL cursor preceding line (CSI F)](https://terminfo.dev/cursor/cpl-cursor-preceding-line)<br>`cursor.cpl` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [HPA horizontal position absolute (CSI \`)](https://terminfo.dev/cursor/hpa-horizontal-position-absolute)<br>`cursor.hpa` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |

### Erasure

Source references: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.Csi.cs), [EditingDefaultParameterTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.VT.Tests/EditingDefaultParameterTests.cs), [EraseInDisplayTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.VT.Tests/EraseInDisplayTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Erase to EOL (EL 0)](https://terminfo.dev/erase/el-0-erase-to-eol)<br>`erase.line.right` | inconclusive | [cell](#suite-cell) `erase.line.right`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Erase to BOL (EL 1)](https://terminfo.dev/erase/el-1-erase-to-bol)<br>`erase.line.left` | inconclusive | [cell](#suite-cell) `erase.line.left`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Erase line (EL 2)](https://terminfo.dev/erase/el-2-erase-line)<br>`erase.line.all` | inconclusive | [cell](#suite-cell) `erase.line.all`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Erase above (ED 1)](https://terminfo.dev/erase/ed-1-erase-above)<br>`erase.screen.above` | inconclusive | [erase](#suite-erase) `erase.display.1`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Erase screen (ED 2)](https://terminfo.dev/erase/ed-2-erase-screen)<br>`erase.screen.all` | inconclusive | [erase](#suite-erase) `erase.display.2`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Erase below (ED 0)](https://terminfo.dev/erase/ed-0-erase-below)<br>`erase.screen.below` | inconclusive | [erase](#suite-erase) `erase.display.default`: **passed**; [erase](#suite-erase) `erase.display.0`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Erase scrollback (ED 3)](https://terminfo.dev/erase/ed-3-erase-scrollback)<br>`erase.screen.scrollback` | inconclusive | [erase](#suite-erase) `erase.saved-lines`: **passed** | Owned ED3 clears the known bounded history and preserves viewport/cursor. This run uses a smaller history than the upstream 30-line fixture. |
| [Erase character (ECH)](https://terminfo.dev/erase/ech-erase-character)<br>`erase.character` | inconclusive | [erase](#suite-erase) `erase.characters.default`: **passed**; [erase](#suite-erase) `erase.characters.0`: **passed**; [erase](#suite-erase) `erase.characters.3`: **passed**; [erase](#suite-erase) `erase.characters.999`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [EL erases with bg color](https://terminfo.dev/erase/el-erase-with-bg-color)<br>`erase.el-with-attrs` | inconclusive | None mapped | The cell suite proves EL text erasure, but does not include erased-cell background attributes. Owned colored ED/ECH cases do not prove this EL requirement. Add a controlled green-background EL case with cell-attribute readback. |
| [ED at scroll region boundary](https://terminfo.dev/erase/ed-scroll-region-boundary)<br>`erase.ed-scroll-region` | inconclusive | [erase](#suite-erase) `erase.display.0.margins`: **passed** | Owned ED0 with margins retains outside cells; the fixture differs from KEEP_THIS at row zero and margins 3;10. |

### Screen modes

Source references: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.Csi.cs), [ModeState.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/ModeState.cs), [AlternateScreenTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/AlternateScreenTests.cs), [DecModeTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/DecModeTests.cs), [SoftResetAndCursorSaveTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.VT.Tests/SoftResetAndCursorSaveTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Enter alt screen (DECSET 1049)](https://terminfo.dev/modes/decset-1049-alt-screen-enter)<br>`modes.alt-screen.enter` | supported | [saved](#suite-saved) `saved.alternate.1049`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Exit alt screen (DECRST 1049)](https://terminfo.dev/modes/decrst-1049-alt-screen-exit)<br>`modes.alt-screen.exit` | inconclusive | [saved](#suite-saved) `saved.alternate.1049`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Reverse video (DECSCNM)](https://terminfo.dev/modes/decscnm-reverse-video)<br>`modes.reverse-video` | inconclusive | None mapped | Private mode 5 is unrecognized in the retained reply and is absent from the audited tracked-mode cases. SGR 7 inverse pixels do not establish DECSCNM. Source limitation; no automatic stock regrade. |
| [Auto-wrap (DECAWM)](https://terminfo.dev/modes/decawm-auto-wrap)<br>`modes.auto-wrap` | supported | None mapped | The stock positive is mode recognition. Keep actual wrap behavior separate; existing PendingWrap tests are source references, not a fresh owned DECAWM on/off experiment. |

### History and scrolling

Source references: [AnsiParser.Csi.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.Csi.cs), [ScrollAndWrapCorrectnessTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/ScrollAndWrapCorrectnessTests.cs), [ScrollbackSideTableTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.VT.Tests/ScrollbackSideTableTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Scrollback accumulates](https://terminfo.dev/scrollback/accumulates)<br>`scrollback.accumulate` | inconclusive | [history](#suite-history) `history.accumulate.lf`: **passed**; [history](#suite-history) `history.accumulate.crlf`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Total line count](https://terminfo.dev/scrollback/total-lines)<br>`scrollback.total-lines` | inconclusive | [history](#suite-history) `history.accumulate.lf`: **passed** | The owned fixture observes exact ordered history and count growth, but only up to four rows. It does not satisfy the upstream current criterion requiring at least ten extra retained lines. |
| [Scroll up (SU)](https://terminfo.dev/scrollback/su-scroll-up)<br>`scrollback.scroll-up` | inconclusive | [history](#suite-history) `history.scroll-up.main.default`: **passed**; [history](#suite-history) `history.scroll-up.main.2`: **passed**; [history](#suite-history) `history.scroll-up.main.margins`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Scroll down (SD)](https://terminfo.dev/scrollback/sd-scroll-down)<br>`scrollback.scroll-down` | inconclusive | [history](#suite-history) `history.scroll-down.main.default`: **passed**; [history](#suite-history) `history.scroll-down.main.2`: **passed**; [history](#suite-history) `history.scroll-down.main.margins`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Reverse index (RI)](https://terminfo.dev/scrollback/ri-reverse-index)<br>`scrollback.reverse-index` | inconclusive | [history](#suite-history) `history.reverse-index.main.top`: **passed**; [history](#suite-history) `history.reverse-index.margins.middle`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Scroll region (DECSTBM)](https://terminfo.dev/scrollback/decstbm-scroll-region)<br>`scrollback.set-region` | inconclusive | [history](#suite-history) `history.margins.set`: **passed**; [history](#suite-history) `history.margins.origin-home`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Alt screen separate scrollback](https://terminfo.dev/scrollback/alt-screen-separate-scrollback)<br>`scrollback.alt-screen` | inconclusive | [history](#suite-history) `history.scroll-up.alt.default`: **passed**; [history](#suite-history) `history.scroll-up.alt.margins`: **passed** | Owned alternate scrolling and 1049 entry/exit retain main history. These strengthen the cursor-only stock attempt without changing its frozen grade. |
| [DECSTBM constrains scrolling](https://terminfo.dev/scrollback/decstbm-constrained-scrolling)<br>`scrollback.decstbm` | inconclusive | [history](#suite-history) `history.scroll-up.main.margins`: **passed**; [history](#suite-history) `history.reverse-index.margins.top`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [DECSTBM reset to full screen](https://terminfo.dev/scrollback/decstbm-reset-full-screen)<br>`scrollback.decstbm-reset` | inconclusive | [history](#suite-history) `history.margins.default`: **passed**; [history](#suite-history) `history.margins.zero`: **passed**; [history](#suite-history) `history.accumulate.lf`: **passed** | Owned cases separately observe full-margin reset and bounded history accumulation. Add one joined reset-plus-overflow case for the upstream 30-line criterion. |

### Text and controls

Source references: [AnsiParser.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.cs), [PendingWrapTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/PendingWrapTests.cs), [ScrollAndWrapCorrectnessTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/ScrollAndWrapCorrectnessTests.cs), [TabSystemTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/TabSystemTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Basic text rendering](https://terminfo.dev/text/basic-text-rendering)<br>`text.basic` | inconclusive | [cell](#suite-cell) `text.basic`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Text wraps at width](https://terminfo.dev/text/wrap-at-width)<br>`text.wrap` | supported | None mapped | Stock cursor wrap is observed, while exact row-one content and DECAWM-off behavior need separately linked cell evidence. |
| [CJK wide chars (2 cols)](https://terminfo.dev/text/cjk-wide-chars)<br>`text.wide.cjk` | supported | [rendition](#suite-rendition) `charsets.utf8.wide`: **passed** | Stock cursor width and owned Unicode wide-cell facts have different methods. The retained rendition case is a sampled wide grapheme, not all CJK/UTF-8 byte forms. |
| [Tab stops](https://terminfo.dev/text/ht-tab-stops)<br>`text.tab` | supported | None mapped | Stock default-tab positions passed. Stored X placement is distinct from a cursor-only response; exact cell readback remains a separate variant. |
| [Carriage return](https://terminfo.dev/text/cr-carriage-return)<br>`text.cr` | supported | None mapped | The stock CPR witness confirms cursor movement only; it does not prove both C overwrote A and B survived. Add paired cell reads of AB\\rC for the exact content claim. |
| [Line feed](https://terminfo.dev/text/lf-line-feed)<br>`text.newline` | supported | None mapped | The stock result permits TTY LF-to-CRLF translation. Owned history experiments separately used parent-verified -opost; do not merge these input-path claims. |
| [Backspace (BS)](https://terminfo.dev/text/bs-backspace)<br>`text.backspace` | supported | None mapped | The stock CPR witness confirms position only. Add paired cell reads of AB\\bC to prove both replacement and the unaffected A. |
| [Index (IND)](https://terminfo.dev/text/ind-index)<br>`text.index` | supported | [history](#suite-history) `history.accumulate.index`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Next line (NEL)](https://terminfo.dev/text/nel-next-line)<br>`text.next-line` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Reverse Index at Scroll Top](https://terminfo.dev/text/reverse-index-scroll)<br>`text.reverse-index-scroll` | inconclusive | [history](#suite-history) `history.reverse-index.margins.top`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [Overwrite at cursor](https://terminfo.dev/text/overwrite-at-cursor)<br>`text.overwrite` | inconclusive | [cell](#suite-cell) `text.overwrite`: **passed** | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [HTS set tab stop (ESC H)](https://terminfo.dev/text/hts-set-tab-stop)<br>`text.hts` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [TBC clear tab stop (CSI g)](https://terminfo.dev/text/tbc-clear-tab-stop)<br>`text.tbc` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [CHT forward tab (CSI I)](https://terminfo.dev/text/cht-forward-tab)<br>`text.cht` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |
| [CBT backward tab (CSI Z)](https://terminfo.dev/text/cbt-backward-tab)<br>`text.cbt` | supported | None mapped | The retained case/query proves only its recorded method and fixture. Replay the pinned upstream requirement in a matched disposable context for a common grade; font/platform completeness is not implied. |

### Unicode tab stops

Source references: [TabSystemTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/TabSystemTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Tab Stops (HT)](https://terminfo.dev/unicode/tab-stops)<br>`unicode.tab-stops` | supported | None mapped | The stock tab-stop behavior is measured. This overlaps text.tab but is a distinct upstream feature identity and is counted once under each declared identity. |

### Reset

Source references: [SavedCursorReplayTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.VT.Tests/SavedCursorReplayTests.cs), [SoftResetAndCursorSaveTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.VT.Tests/SoftResetAndCursorSaveTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [SGR reset clears attributes](https://terminfo.dev/reset/sgr-0-reset-clears-attributes)<br>`reset.sgr` | inconclusive | [sgr](#suite-sgr) `sgr.reset`: **passed** | The owned mixed-style SGR 0 case clears all tracked flags/default indices. Its seeded mixture differs from the upstream bold+italic+inverse X/Y fixture. |
| [Full reset (RIS)](https://terminfo.dev/reset/ris-full-reset)<br>`reset.ris` | supported | [saved](#suite-saved) `saved.reset.full-both-banks`: **passed** | The latest owned both-bank reset passes after a VT fix. The older reset failure remains historical and unchanged; broader power-on defaults are outside the fixture. |
| [Soft reset (DECSTR)](https://terminfo.dev/reset/decstr-soft-reset)<br>`reset.soft` | supported | [saved](#suite-saved) `saved.reset.soft-active-bank`: **passed** | Owned active saved-bank/default-pen restoration passed. Exact application-cursor disablement also has source regressions; it is not inferred solely from saved-bank restoration. |
| [Backend reset() method](https://terminfo.dev/reset/backend-reset-method)<br>`reset.method` | inconclusive | None mapped | This row requires the Termless backend reset() API. A desktop app has no such method endpoint; RIS is a separate row and cannot substitute for it. |

### Character sets

Source references: [AnsiParser.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.cs), [DecSpecialGraphicsTests.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/tests/Ntilde.App.Tests/DecSpecialGraphicsTests.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [DEC Special Graphics](https://terminfo.dev/charsets/dec-special-graphics)<br>`charsets.dec-special` | inconclusive | [rendition](#suite-rendition) `charsets.dec-special`: **passed** | Owned stored codepoints are observed directly. The glyphs' font-pixel appearance is a separate scope. |
| [UTF-8 mode](https://terminfo.dev/charsets/utf8-mode)<br>`charsets.utf8` | inconclusive | [rendition](#suite-rendition) `charsets.utf8.bmp`: **passed**; [rendition](#suite-rendition) `charsets.utf8.wide`: **passed** | Owned stored Unicode samples pass, including BMP and a wide grapheme. This does not qualify every raw UTF-8 byte form or every font/platform. |
| [G0/G1 character set switching](https://terminfo.dev/charsets/g0-g1-charset-switching)<br>`charsets.g0-g1-switching` | inconclusive | [rendition](#suite-rendition) `charsets.g0-g1-switching`: **passed** | Owned SI/SO/G0/G1 stored-codepoint witnesses pass. Use exact upstream ASCII/DEC/ASCII controls before assigning a common collector grade. |
| [DEC line drawing character set](https://terminfo.dev/charsets/dec-line-drawing-chars)<br>`charsets.dec-line-drawing` | inconclusive | [rendition](#suite-rendition) `charsets.dec-line-drawing`: **passed** | Owned stored line-drawing codepoints pass; font pixels and the exact upstream jklmqx fixture are separate claims. |

### Environment

Source references: [lib.rs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.App/native/src/lib.rs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [TERM env variable](https://terminfo.dev/device/env-term)<br>`device.env-term` | not collected (manual) | None mapped | Manual/ungraded in the pinned current scoring rule. Native spawn source sets xterm-256color, but no measured child environment is present in this stock run. |

### Icon name

Source references: [remaining-protocol-variants.md](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/docs/qa/terminfo/remaining-protocol-variants.md), [AnsiParser.Osc.cs](https://github.com/benyblack/ntilde/blob/9648f5171b2d383f4954e2bcf6170c2b92e3f98e/src/Ntilde.VT/AnsiParser.Osc.cs).

| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |
|---|---|---|---|
| [Icon name (OSC 1)](https://terminfo.dev/extensions/osc-1-icon-name)<br>`extensions.osc1-icon` | inconclusive | None mapped | No independent icon-name state/readback is implemented by the audited App/VT path. OSC 0/2 title observations do not establish OSC 1 icon behavior. |

## Suite inventory

### Suite cell

`cell-observations.json` — `ntilde-owned-ascii-cells-v1`; SHA-256 `1559e0640b185239434a350759bf8ec2c4849f5365208d4e499350bfe3bfc620`.

Live App readScreen buffer projection: ASCII text and cursor state only; not pixels or stock collector grades.

Original case outcomes: 8 passed.

### Suite color

`color-observations.json` — `ntilde-owned-runtime-colors-v1`; SHA-256 `5a935a1338ae2a7c5da9b2d2eb73cba7f24cb043080d6e893c99f180808f99c0`.

Owned runtime OSC resets, exact fenced RGB replies and synthetic selected/unselected offscreen PNGs. Unset selection defaults have no concrete reply. No live selection UI, desktop cadence, platform equivalence or stock collector regrading.

Original case outcomes: 10 passed.

### Suite cursor

`cursor-observations.json` — `ntilde-owned-cursor-modes-v1`; SHA-256 `47be8ac3b60261e3c758a44adb0d878c6aca5deb9c36e25af3624f3392c8e663`.

Stored cursor visibility/style/blink/default modes in a pristine owned fixture; no resolved host appearance, pixels, blink phase, scrollback or stock grades.

Original case outcomes: 9 passed.

### Suite editing

`editing-observations.json` — `ntilde-owned-cell-editing-v1`; SHA-256 `7cd4ce1db470c8244d6f1fb48d054dfabb3aecbafbadaf253f2e5477547ce061`.

Owned ASCII erase/editing stored cells, margins/modes/cursor and actual history text. BCE erased blanks qualify their literal background only; all attributes remain recorded. No rendered pixels, Unicode editing, full saved-state or stock grades.

Original case outcomes: 22 passed.

### Suite erase

`erase-observations.json` — `ntilde-owned-erasure-v1`; SHA-256 `601748c340a0442e0860948625acb03f0bd128f84e3cf245580cb640c6bf7ce7`.

Owned ASCII erase/editing stored cells, margins/modes/cursor and actual history text. BCE erased blanks qualify their literal background only; all attributes remain recorded. No rendered pixels, Unicode editing, full saved-state or stock grades.

Original case outcomes: 13 passed.

### Suite history

`history-observations.json` — `ntilde-owned-history-scrolling-v1`; SHA-256 `13d10ded8a44549fcbe161e8c42c456be6b3a6852ee9e65a65332a1c28868f75`.

Owned ASCII history accumulation, SU/SD/RI, margins and origin-relative homing. Complete viewport cells and actual bounded ordered history text, with before/after state and exact restoration. Invalid-region cases qualify margins only with a cursor already home. No resize/reflow, history attribute, Unicode editing, pixel or stock-grade claim.

Original case outcomes: 30 passed.

### Suite metadata

`metadata-observations.json` — `ntilde-owned-pane-metadata-v1`; SHA-256 `9bdca3aaa70d90f5ed831cae05d8f9540714c5eb2067fb373e4a9a0320576444`.

Paired complete stored active cells/attributes, bounded title/cwd/link/shell facts and actual private-window UI controls. Restores the explicitly prepared title/cwd baseline and initial cells; original unprepared null metadata and hidden-control content are not claimed restored. Verified disposable teardown is mandatory. No URL opening, clipboard, pixels, OS taskbar/native notifications, general prompt-part semantics or stock-grade claim.

Original case outcomes: 1 failed, 3 inconclusive, 9 passed.

### Suite pixel

`pixel-observations.json` — `ntilde-owned-render-pixels-v1`; SHA-256 `e1ae25a8311ae6820ca51aadbd7122d7c0c3d869e8d3056a3798e3b10faf328b`.

Owned offscreen renderer pixels at synthetic 530ms half-period phases; local controls and retained primary font/context. No live desktop cadence, cross-platform font/fallback equivalence, clipboard, images or stock grades.

Original case outcomes: 1 failed, 24 passed.

### Suite rendition

`rendition-observations.json` — `ntilde-owned-rendition-charsets-v3`; SHA-256 `ebf98c6cdba839cdfe9320617d3af70f54f8ff06abf0405dfed9d6b47a315e2f`.

Stored rendition flags, overline, and decoded Unicode/DEC charset cells. No pixels, blink timing or stock grades.

Original case outcomes: 19 passed.

### Suite resize

`resize-observations.json` — `ntilde-owned-resize-reflow-v1`; SHA-256 `201711d02f61f8563f13b0187f25c774f653d47cd1492c17cb02e340bd97ba8e`.

Owned ASCII hard-line width reflow, height/history movement and combined shrink/restore. Geometry restoration preserves Ntilde's documented top anchor, including retained history; every original hard line remains represented across history and viewport. Paired complete viewport and bounded ordered history text; parent-selected App actions with independent kernel geometry/output-processing proof. No Unicode, history-attribute, pixel, saved-state or stock-grade claim.

Original case outcomes: 3 passed.

### Suite rgb

`rgb-observations.json` — `ntilde-owned-rgb-underline-v1`; SHA-256 `1180628e39ee3b71617ee39e74cf5733028244276d90640d6e56a0af5f1f10fb`.

Live App stored ARGB, default/palette markers and underline metadata; complete physical-cell runs. No resolved theme colors, pixels or stock grades.

Original case outcomes: 20 passed.

### Suite saved

`saved-observations.json` — `ntilde-owned-saved-alternate-v1`; SHA-256 `b8c061904a2aadfa02415d69b232e28f5a0a9798289963619d7ccd28646b903f`.

Paired complete active cells/attributes, cursor presentation/position, exposed buffer modes and ordered bounded history. Source-known transitions and newly written witnesses qualify alternate retention/clearing, independent saved cursor/pen/charset restoration and reset boundaries. Invisible bank fields are not read directly; no pixels, generic Unicode editing, platform breadth or stock-grade claim.

Original case outcomes: 9 passed.

### Suite sgr

`sgr-observations.json` — `ntilde-owned-sgr-indices-v1`; SHA-256 `54cab4b383cb1945351f42c7260597bd78b4caf568798132215eca60e94c991b`.

Live App ASCII cell flags and palette indices; lossless row-major attribute runs. No RGB, underline metadata, pixels or stock grades.

Original case outcomes: 17 passed.

## Reproduce

```sh
python3 scripts/terminfo-core-report.py --check
python3 scripts/tests/terminfo_core_report_tests.py
```

Regenerate with --write after an intentional reviewed input-map change. Both JSON and Markdown derive from the pinned catalogue subset, original archive and explicit variant mappings. Checks need no network, Git history, private artifacts, desktop or native process.

Upstream names, sequences and requirement excerpts are by Bjorn Stabell, CC BY 4.0; the source attribution/license and extraction changes are retained in core-baseline-inputs.json. Ntilde annotations are separate additions. No historical report, receipt, matrix status or generated conformance artifact is rewritten.
