# Common-harness Core comparison

These are fresh application runs through the same upstream terminal probes. Ntilde's [separate 80/80 detailed qualification](../2026-11-11-core-reverse-wide-results/report.md) is not substituted into this comparison.

Upstream/adapter source: `ff37d198914b870281a2c0461cc682b37fce07ef`; suite `e122f23e91e1`. Measured macOS version: 27.0.1; PTY geometry: 41 × 140.

| Application | Supported | Unsupported | Not concluded | Invalid measurement | Untestable / not tested |
|---|---:|---:|---:|---:|---:|
| Ntilde 0.13.0 | 63 | 0 | 8 | 7 | 2 |
| iTerm2 3.7.7 | 28 | 0 | 50 | 0 | 2 |
| Ghostty 1.4.0 | 69 | 0 | 9 | 0 | 2 |

All counts use the same 80 feature IDs. The exact upstream application probe decides each result. A missing observer, invalid measurement or untestable framework API is not a terminal failure. Defaults and capture conditions are retained with the evidence. Observer availability differs across applications, so these counts are not a capability ranking.

The local adapter adds native launch/ownership and preserves measured Retina screenshots without resampling. Probe definitions and grading rules are unchanged; the upstream revision, adapter patch and sources are retained. Historical website scores are not mixed with these runs.

- **Ntilde:** capture installed. Disposable ownership receipt: shared (a separate run-scoped process proof may authorize writes). Withheld probes: 0; invalidated by native bounds changes: 6.
- **iTerm2:** capture refused — Capture calibration reported geometry {"cellHeight":15,"cellWidth":7,"textHeight":652,"textWidth":990} disagrees with witnessed grid {"cellWidth":14,"cellHeight":30,"textWidth":1960,"textHeight":1230}. Disposable ownership receipt: shared (a separate run-scoped process proof may authorize writes). Withheld probes: 5; invalidated by native bounds changes: 0.
- **Ghostty:** capture installed. Disposable ownership receipt: shared (a separate run-scoped process proof may authorize writes). Withheld probes: 0; invalidated by native bounds changes: 0.

Reproduce the published projection: `node scripts/terminfo-common-core-publication.mjs --check`. A new native collection additionally needs the recorded applications, upstream dependencies and desktop access.

| Feature | Ntilde | iTerm2 | Ghostty |
|---|---|---|---|
| [sgr.bold](https://terminfo.dev/sgr/1-bold) | Supported | Not concluded | Supported |
| [sgr.faint](https://terminfo.dev/sgr/2-faint-dim) | Supported | Not concluded | Supported |
| [sgr.italic](https://terminfo.dev/sgr/3-italic) | Supported | Not concluded | Supported |
| [sgr.underline.single](https://terminfo.dev/sgr/4-underline) | Invalid measurement | Not concluded | Supported |
| [sgr.inverse](https://terminfo.dev/sgr/7-inverse-video) | Supported | Not concluded | Supported |
| [sgr.hidden](https://terminfo.dev/sgr/8-hidden) | Not concluded | Not concluded | Not concluded |
| [sgr.strikethrough](https://terminfo.dev/sgr/9-strikethrough) | Invalid measurement | Not concluded | Not concluded |
| [sgr.fg.standard](https://terminfo.dev/sgr/30-37-standard-fg) | Supported | Not concluded | Supported |
| [sgr.bg.standard](https://terminfo.dev/sgr/40-47-standard-bg) | Supported | Not concluded | Supported |
| [sgr.fg.default](https://terminfo.dev/sgr/39-default-fg) | Supported | Not concluded | Supported |
| [sgr.bg.default](https://terminfo.dev/sgr/49-default-bg) | Supported | Not concluded | Supported |
| [sgr.selective-reset.bold](https://terminfo.dev/sgr/22-reset-bold) | Not concluded | Not concluded | Not concluded |
| [sgr.selective-reset.underline](https://terminfo.dev/sgr/24-reset-underline) | Invalid measurement | Not concluded | Supported |
| [sgr.selective-reset.inverse](https://terminfo.dev/sgr/27-reset-inverse) | Supported | Not concluded | Supported |
| [sgr.selective-reset.italic](https://terminfo.dev/sgr/23-reset-italic) | Invalid measurement | Not concluded | Supported |
| [sgr.reset](https://terminfo.dev/sgr/0-reset) | Not concluded | Not concluded | Not concluded |
| [cursor.hide](https://terminfo.dev/cursor/dectcem-hide-cursor) | Invalid measurement | Not concluded | Supported |
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
| [cursor.cup-scroll-region](https://terminfo.dev/cursor/cup-decom-scroll-region) | Supported | Not concluded | Supported |
| [cursor.vpa](https://terminfo.dev/cursor/vpa-vertical-position-absolute) | Supported | Supported | Supported |
| [cursor.cpl](https://terminfo.dev/cursor/cpl-cursor-preceding-line) | Supported | Supported | Supported |
| [cursor.hpa](https://terminfo.dev/cursor/hpa-horizontal-position-absolute) | Supported | Supported | Supported |
| [erase.line.right](https://terminfo.dev/erase/el-0-erase-to-eol) | Supported | Not concluded | Supported |
| [erase.line.left](https://terminfo.dev/erase/el-1-erase-to-bol) | Supported | Not concluded | Supported |
| [erase.line.all](https://terminfo.dev/erase/el-2-erase-line) | Supported | Not concluded | Supported |
| [erase.screen.above](https://terminfo.dev/erase/ed-1-erase-above) | Supported | Not concluded | Supported |
| [erase.screen.all](https://terminfo.dev/erase/ed-2-erase-screen) | Supported | Not concluded | Supported |
| [erase.screen.below](https://terminfo.dev/erase/ed-0-erase-below) | Supported | Not concluded | Supported |
| [erase.screen.scrollback](https://terminfo.dev/erase/ed-3-erase-scrollback) | Not concluded | Not concluded | Not concluded |
| [erase.character](https://terminfo.dev/erase/ech-erase-character) | Supported | Not concluded | Supported |
| [erase.el-with-attrs](https://terminfo.dev/erase/el-erase-with-bg-color) | Supported | Not concluded | Supported |
| [erase.ed-scroll-region](https://terminfo.dev/erase/ed-scroll-region-boundary) | Supported | Not concluded | Supported |
| [modes.alt-screen.enter](https://terminfo.dev/modes/decset-1049-alt-screen-enter) | Supported | Not concluded | Supported |
| [modes.alt-screen.exit](https://terminfo.dev/modes/decrst-1049-alt-screen-exit) | Supported | Not concluded | Supported |
| [modes.reverse-video](https://terminfo.dev/modes/decscnm-reverse-video) | Supported | Not concluded | Supported |
| [modes.auto-wrap](https://terminfo.dev/modes/decawm-auto-wrap) | Supported | Not concluded | Supported |
| [scrollback.accumulate](https://terminfo.dev/scrollback/accumulates) | Not concluded | Not concluded | Not concluded |
| [scrollback.total-lines](https://terminfo.dev/scrollback/total-lines) | Not concluded | Not concluded | Not concluded |
| [scrollback.scroll-up](https://terminfo.dev/scrollback/su-scroll-up) | Supported | Not concluded | Supported |
| [scrollback.scroll-down](https://terminfo.dev/scrollback/sd-scroll-down) | Supported | Not concluded | Supported |
| [scrollback.reverse-index](https://terminfo.dev/scrollback/ri-reverse-index) | Supported | Not concluded | Supported |
| [scrollback.set-region](https://terminfo.dev/scrollback/decstbm-scroll-region) | Supported | Not concluded | Supported |
| [scrollback.alt-screen](https://terminfo.dev/scrollback/alt-screen-separate-scrollback) | Supported | Not concluded | Supported |
| [scrollback.decstbm](https://terminfo.dev/scrollback/decstbm-constrained-scrolling) | Supported | Not concluded | Supported |
| [scrollback.decstbm-reset](https://terminfo.dev/scrollback/decstbm-reset-full-screen) | Supported | Not concluded | Supported |
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
| [text.reverse-index-scroll](https://terminfo.dev/text/reverse-index-scroll) | Invalid measurement | Not concluded | Supported |
| [text.overwrite](https://terminfo.dev/text/overwrite-at-cursor) | Invalid measurement | Not concluded | Supported |
| [text.hts](https://terminfo.dev/text/hts-set-tab-stop) | Supported | Supported | Supported |
| [text.tbc](https://terminfo.dev/text/tbc-clear-tab-stop) | Supported | Supported | Supported |
| [text.cht](https://terminfo.dev/text/cht-forward-tab) | Supported | Supported | Supported |
| [text.cbt](https://terminfo.dev/text/cbt-backward-tab) | Supported | Supported | Supported |
| [reset.sgr](https://terminfo.dev/reset/sgr-0-reset-clears-attributes) | Not concluded | Supported | Not concluded |
| [reset.ris](https://terminfo.dev/reset/ris-full-reset) | Supported | Supported | Supported |
| [reset.soft](https://terminfo.dev/reset/decstr-soft-reset) | Supported | Not concluded | Supported |
| [reset.method](https://terminfo.dev/reset/backend-reset-method) | Untestable | Untestable | Untestable |
| [charsets.dec-special](https://terminfo.dev/charsets/dec-special-graphics) | Supported | Not concluded | Supported |
| [charsets.utf8](https://terminfo.dev/charsets/utf8-mode) | Supported | Not concluded | Supported |
| [charsets.g0-g1-switching](https://terminfo.dev/charsets/g0-g1-charset-switching) | Supported | Not concluded | Supported |
| [charsets.dec-line-drawing](https://terminfo.dev/charsets/dec-line-drawing-chars) | Supported | Not concluded | Supported |
| [device.env-term](https://terminfo.dev/device/env-term) | Not tested | Not tested | Not tested |
| [extensions.osc1-icon](https://terminfo.dev/extensions/osc-1-icon-name) | Not concluded | Not concluded | Not concluded |

## Focused movement retest

The original full-run results remain retained above. One unattempted manual position is labeled not tested while its original raw status is preserved. Every movement-invalidated case was retested with the same upstream collector, shared worker, executable/payload identity and grid. Latest combined counts include every retest outcome, including any unsupported or inconclusive result.

- ntilde: 6/6 supported in retest `0d6902eb35805785d3b67ed55c05d499`; latest combined evidence 69 supported / 0 unsupported / 1 invalid / 8 not concluded / 1 untestable / 1 not tested. Original run `af1f5c275a0b3aab10fdd216ca668d10` remains retained.

| Retested case | New outcome |
|---|---|
| sgr.selective-reset.italic | Supported |
| sgr.selective-reset.underline | Supported |
| sgr.strikethrough | Supported |
| sgr.underline.single | Supported |
| text.overwrite | Supported |
| text.reverse-index-scroll | Supported |
