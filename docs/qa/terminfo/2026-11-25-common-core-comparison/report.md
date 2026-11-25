# Common-harness Core comparison

These are fresh application runs through the same upstream terminal probes. Ntilde's [separate 80/80 detailed qualification](../2026-11-11-core-reverse-wide-results/report.md) is not substituted into this comparison.

Upstream/adapter source: `0f01f9013053086354c941390bfd7d678c6a7e2b`; suite `ef9c0a6b2d4a`. Measured macOS version: 27.0.1; PTY geometry: 41 × 140.

| Application | Supported | Unsupported | Not concluded | Invalid measurement | Untestable / not tested |
|---|---:|---:|---:|---:|---:|
| Ntilde 0.14.0 | 64 | 0 | 13 | 1 | 2 |
| iTerm2 3.7.9 | 49 | 0 | 22 | 7 | 2 |
| Ghostty 1.4.0 | 64 | 0 | 14 | 0 | 2 |

All counts use the same 80 feature IDs. The exact upstream application probe decides each result. A missing observer, invalid measurement or untestable framework API is not a terminal failure. Defaults and capture conditions are retained with the evidence. Observer availability differs across applications, so these counts are not a capability ranking.

The local adapter adds native launch/ownership and preserves measured Retina screenshots without resampling. Probe definitions and grading rules are unchanged; the upstream revision, adapter patch and sources are retained. Historical website scores are not mixed with these runs.

- **Ntilde:** capture installed. Disposable ownership receipt: shared (a separate run-scoped process proof may authorize writes). Withheld probes: 0; invalidated by native bounds changes: 0.
- **iTerm2:** capture installed. Disposable ownership receipt: shared (a separate run-scoped process proof may authorize writes). Withheld probes: 5; invalidated by native bounds changes: 0.
- **Ghostty:** capture installed. Disposable ownership receipt: shared (a separate run-scoped process proof may authorize writes). Withheld probes: 0; invalidated by native bounds changes: 0.

iTerm2's window and cell queries reported macOS points. The adapter checked the window bounds and backing scale, then corroborated the painted pixel grid with the rounded cell pitch. Calibration verified pixel-for-pixel restoration outside a title-bar strip proved clear of the grid. The raw replies and original native images are retained. [iTerm2 size-report implementation](https://github.com/gnachman/iTerm2/blob/v3.7.9/sources/VT100/VT100Terminal.m#L3006-L3028). [Previous comparison](../2026-11-11-common-core-comparison/report.md).

Reproduce the published projection: `node scripts/terminfo-common-core-publication.mjs --check`. A new native collection additionally needs the recorded applications, upstream dependencies and desktop access.

| Feature | Ntilde | iTerm2 | Ghostty |
|---|---|---|---|
| [sgr.bold](https://terminfo.dev/sgr/1-bold) | Not concluded | Not concluded | Not concluded |
| [sgr.faint](https://terminfo.dev/sgr/2-faint-dim) | Not concluded | Not concluded | Not concluded |
| [sgr.italic](https://terminfo.dev/sgr/3-italic) | Not concluded | Not concluded | Not concluded |
| [sgr.underline.single](https://terminfo.dev/sgr/4-underline) | Not concluded | Not concluded | Not concluded |
| [sgr.inverse](https://terminfo.dev/sgr/7-inverse-video) | Supported | Not concluded | Supported |
| [sgr.hidden](https://terminfo.dev/sgr/8-hidden) | Not concluded | Not concluded | Not concluded |
| [sgr.strikethrough](https://terminfo.dev/sgr/9-strikethrough) | Not concluded | Not concluded | Not concluded |
| [sgr.fg.standard](https://terminfo.dev/sgr/30-37-standard-fg) | Supported | Supported | Supported |
| [sgr.bg.standard](https://terminfo.dev/sgr/40-47-standard-bg) | Supported | Supported | Supported |
| [sgr.fg.default](https://terminfo.dev/sgr/39-default-fg) | Supported | Supported | Supported |
| [sgr.bg.default](https://terminfo.dev/sgr/49-default-bg) | Supported | Supported | Supported |
| [sgr.selective-reset.bold](https://terminfo.dev/sgr/22-reset-bold) | Not concluded | Not concluded | Not concluded |
| [sgr.selective-reset.underline](https://terminfo.dev/sgr/24-reset-underline) | Supported | Not concluded | Supported |
| [sgr.selective-reset.inverse](https://terminfo.dev/sgr/27-reset-inverse) | Supported | Supported | Supported |
| [sgr.selective-reset.italic](https://terminfo.dev/sgr/23-reset-italic) | Supported | Not concluded | Supported |
| [sgr.reset](https://terminfo.dev/sgr/0-reset) | Not concluded | Not concluded | Not concluded |
| [cursor.hide](https://terminfo.dev/cursor/dectcem-hide-cursor) | Invalid measurement | Supported | Supported |
| [cursor.move.absolute](https://terminfo.dev/cursor/cup-cursor-position) | Supported | Supported | Supported |
| [cursor.move.up](https://terminfo.dev/cursor/cuu-cursor-up) | Supported | Supported | Supported |
| [cursor.move.down](https://terminfo.dev/cursor/cud-cursor-down) | Supported | Supported | Supported |
| [cursor.move.forward](https://terminfo.dev/cursor/cuf-cursor-forward) | Supported | Supported | Supported |
| [cursor.move.back](https://terminfo.dev/cursor/cub-cursor-back) | Supported | Supported | Supported |
| [cursor.move.home](https://terminfo.dev/cursor/home) | Supported | Supported | Supported |
| [cursor.save-restore](https://terminfo.dev/cursor/decsc-save-restore-cursor) | Supported | Supported | Supported |
| [cursor.cup-boundaries](https://terminfo.dev/cursor/cup-screen-boundaries) | Supported | Supported | Supported |
| [cursor.cuu-past-top](https://terminfo.dev/cursor/cuu-past-top) | Supported | Supported | Supported |
| [cursor.cud-past-bottom](https://terminfo.dev/cursor/cud-past-bottom) | Supported | Supported | Supported |
| [cursor.cup-scroll-region](https://terminfo.dev/cursor/cup-decom-scroll-region) | Supported | Supported | Supported |
| [cursor.vpa](https://terminfo.dev/cursor/vpa-vertical-position-absolute) | Supported | Supported | Supported |
| [cursor.cpl](https://terminfo.dev/cursor/cpl-cursor-preceding-line) | Supported | Supported | Supported |
| [cursor.hpa](https://terminfo.dev/cursor/hpa-horizontal-position-absolute) | Supported | Supported | Supported |
| [erase.line.right](https://terminfo.dev/erase/el-0-erase-to-eol) | Supported | Supported | Supported |
| [erase.line.left](https://terminfo.dev/erase/el-1-erase-to-bol) | Supported | Supported | Supported |
| [erase.line.all](https://terminfo.dev/erase/el-2-erase-line) | Supported | Supported | Supported |
| [erase.screen.above](https://terminfo.dev/erase/ed-1-erase-above) | Supported | Supported | Supported |
| [erase.screen.all](https://terminfo.dev/erase/ed-2-erase-screen) | Supported | Supported | Supported |
| [erase.screen.below](https://terminfo.dev/erase/ed-0-erase-below) | Supported | Supported | Supported |
| [erase.screen.scrollback](https://terminfo.dev/erase/ed-3-erase-scrollback) | Not concluded | Not concluded | Not concluded |
| [erase.character](https://terminfo.dev/erase/ech-erase-character) | Supported | Supported | Supported |
| [erase.el-with-attrs](https://terminfo.dev/erase/el-erase-with-bg-color) | Supported | Supported | Supported |
| [erase.ed-scroll-region](https://terminfo.dev/erase/ed-scroll-region-boundary) | Supported | Supported | Supported |
| [modes.alt-screen.enter](https://terminfo.dev/modes/decset-1049-alt-screen-enter) | Supported | Not concluded | Supported |
| [modes.alt-screen.exit](https://terminfo.dev/modes/decrst-1049-alt-screen-exit) | Supported | Supported | Not concluded |
| [modes.reverse-video](https://terminfo.dev/modes/decscnm-reverse-video) | Supported | Not concluded | Supported |
| [modes.auto-wrap](https://terminfo.dev/modes/decawm-auto-wrap) | Supported | Not concluded | Supported |
| [scrollback.accumulate](https://terminfo.dev/scrollback/accumulates) | Not concluded | Not concluded | Not concluded |
| [scrollback.total-lines](https://terminfo.dev/scrollback/total-lines) | Not concluded | Not concluded | Not concluded |
| [scrollback.scroll-up](https://terminfo.dev/scrollback/su-scroll-up) | Supported | Invalid measurement | Supported |
| [scrollback.scroll-down](https://terminfo.dev/scrollback/sd-scroll-down) | Supported | Invalid measurement | Supported |
| [scrollback.reverse-index](https://terminfo.dev/scrollback/ri-reverse-index) | Supported | Invalid measurement | Supported |
| [scrollback.set-region](https://terminfo.dev/scrollback/decstbm-scroll-region) | Supported | Invalid measurement | Supported |
| [scrollback.alt-screen](https://terminfo.dev/scrollback/alt-screen-separate-scrollback) | Supported | Invalid measurement | Supported |
| [scrollback.decstbm](https://terminfo.dev/scrollback/decstbm-constrained-scrolling) | Supported | Invalid measurement | Supported |
| [scrollback.decstbm-reset](https://terminfo.dev/scrollback/decstbm-reset-full-screen) | Supported | Invalid measurement | Supported |
| [text.basic](https://terminfo.dev/text/basic-text-rendering) | Supported | Supported | Supported |
| [text.wrap](https://terminfo.dev/text/wrap-at-width) | Supported | Not concluded | Supported |
| [text.wide.cjk](https://terminfo.dev/text/cjk-wide-chars) | Supported | Supported | Supported |
| [text.tab](https://terminfo.dev/text/ht-tab-stops) | Supported | Supported | Supported |
| [unicode.tab-stops](https://terminfo.dev/unicode/tab-stops) | Supported | Supported | Supported |
| [text.cr](https://terminfo.dev/text/cr-carriage-return) | Supported | Supported | Supported |
| [text.newline](https://terminfo.dev/text/lf-line-feed) | Supported | Supported | Supported |
| [text.backspace](https://terminfo.dev/text/bs-backspace) | Supported | Supported | Supported |
| [text.index](https://terminfo.dev/text/ind-index) | Supported | Supported | Supported |
| [text.next-line](https://terminfo.dev/text/nel-next-line) | Supported | Supported | Supported |
| [text.reverse-index-scroll](https://terminfo.dev/text/reverse-index-scroll) | Supported | Not concluded | Supported |
| [text.overwrite](https://terminfo.dev/text/overwrite-at-cursor) | Supported | Not concluded | Supported |
| [text.hts](https://terminfo.dev/text/hts-set-tab-stop) | Supported | Supported | Supported |
| [text.tbc](https://terminfo.dev/text/tbc-clear-tab-stop) | Supported | Supported | Supported |
| [text.cht](https://terminfo.dev/text/cht-forward-tab) | Supported | Supported | Supported |
| [text.cbt](https://terminfo.dev/text/cbt-backward-tab) | Supported | Supported | Supported |
| [reset.sgr](https://terminfo.dev/reset/sgr-0-reset-clears-attributes) | Not concluded | Supported | Not concluded |
| [reset.ris](https://terminfo.dev/reset/ris-full-reset) | Supported | Supported | Supported |
| [reset.soft](https://terminfo.dev/reset/decstr-soft-reset) | Supported | Not concluded | Supported |
| [reset.method](https://terminfo.dev/reset/backend-reset-method) | Untestable | Untestable | Untestable |
| [charsets.dec-special](https://terminfo.dev/charsets/dec-special-graphics) | Supported | Supported | Supported |
| [charsets.utf8](https://terminfo.dev/charsets/utf8-mode) | Supported | Supported | Supported |
| [charsets.g0-g1-switching](https://terminfo.dev/charsets/g0-g1-charset-switching) | Supported | Supported | Supported |
| [charsets.dec-line-drawing](https://terminfo.dev/charsets/dec-line-drawing-chars) | Supported | Supported | Supported |
| [device.env-term](https://terminfo.dev/device/env-term) | Not tested | Not tested | Not tested |
| [extensions.osc1-icon](https://terminfo.dev/extensions/osc-1-icon-name) | Not concluded | Not concluded | Not concluded |

## Focused screenshot retests

The table above records the original full runs. The focused retests keep the same application binaries, grid, probe definitions and graders. iTerm2 reruns the Core cases that retained native frames, including the [seven earlier scrollback retests](iterm2/focused-retest/cases.json). The adapter checks the tab, session and worker TTY before and after screenshots and keeps the 250 ms paint wait and static-frame checks. Ghostty reruns the five cases that timed out, with the same 30-second deadline and a faster native window census. The [earlier retry](ghostty/focused-retest/cases.json) used the original adapter and timed out on all five cases. Combined evidence includes every outcome from the latest retest.

- iterm2: 33/44 supported in retest `e66265a92483b4e9e9b34c9d6c684012`; latest combined evidence 60 supported / 0 unsupported / 0 invalid / 18 not concluded / 1 untestable / 1 not tested. Original run `e7b598f31bb6b0d7648ff4fb8c6bdab1` remains retained.
- ghostty: 5/5 supported in retest `54669577db59a6415860e338b58fcc5a`; latest combined evidence 69 supported / 0 unsupported / 0 invalid / 9 not concluded / 1 untestable / 1 not tested. Original run `3cd3c30494767f72682a764f347db873` remains retained.

| Retested case | New outcome |
|---|---|
| iterm2 · charsets.dec-line-drawing | Supported |
| iterm2 · charsets.dec-special | Supported |
| iterm2 · charsets.g0-g1-switching | Supported |
| iterm2 · charsets.utf8 | Supported |
| iterm2 · cursor.cup-scroll-region | Supported |
| iterm2 · cursor.hide | Supported |
| iterm2 · erase.character | Supported |
| iterm2 · erase.ed-scroll-region | Supported |
| iterm2 · erase.el-with-attrs | Supported |
| iterm2 · erase.line.all | Supported |
| iterm2 · erase.line.left | Supported |
| iterm2 · erase.line.right | Supported |
| iterm2 · erase.screen.above | Supported |
| iterm2 · erase.screen.all | Supported |
| iterm2 · erase.screen.below | Supported |
| iterm2 · modes.alt-screen.exit | Supported |
| iterm2 · reset.sgr | Not concluded |
| iterm2 · scrollback.accumulate | Not concluded |
| iterm2 · scrollback.alt-screen | Supported |
| iterm2 · scrollback.decstbm | Supported |
| iterm2 · scrollback.decstbm-reset | Supported |
| iterm2 · scrollback.reverse-index | Supported |
| iterm2 · scrollback.scroll-down | Supported |
| iterm2 · scrollback.scroll-up | Supported |
| iterm2 · scrollback.set-region | Supported |
| iterm2 · scrollback.total-lines | Not concluded |
| iterm2 · sgr.bg.default | Supported |
| iterm2 · sgr.bg.standard | Supported |
| iterm2 · sgr.bold | Not concluded |
| iterm2 · sgr.faint | Not concluded |
| iterm2 · sgr.fg.default | Supported |
| iterm2 · sgr.fg.standard | Supported |
| iterm2 · sgr.hidden | Not concluded |
| iterm2 · sgr.inverse | Supported |
| iterm2 · sgr.italic | Not concluded |
| iterm2 · sgr.reset | Not concluded |
| iterm2 · sgr.selective-reset.bold | Not concluded |
| iterm2 · sgr.selective-reset.inverse | Supported |
| iterm2 · sgr.selective-reset.italic | Supported |
| iterm2 · sgr.selective-reset.underline | Supported |
| iterm2 · sgr.strikethrough | Not concluded |
| iterm2 · sgr.underline.single | Not concluded |
| iterm2 · text.overwrite | Supported |
| iterm2 · text.reverse-index-scroll | Supported |
| ghostty · modes.alt-screen.exit | Supported |
| ghostty · sgr.bold | Supported |
| ghostty · sgr.faint | Supported |
| ghostty · sgr.italic | Supported |
| ghostty · sgr.underline.single | Supported |
