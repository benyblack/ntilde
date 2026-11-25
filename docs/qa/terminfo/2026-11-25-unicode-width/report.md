# Ntilde Unicode width qualification

Five cases from the eight-case [Unicode baseline](https://terminfo.dev/baseline/unicode) measured in a disposable, ordinary local terminal on macOS.

| Case | Sample | Width from pinned PTY replay | Stored-cell effect |
| --- | --- | ---: | --- |
| [text.combining](https://terminfo.dev/text/combining-characters) | `é` | 1 (supported) | supported |
| [text.wide.emoji](https://terminfo.dev/text/emoji-wide-chars) | `🎉` | 2 (supported) | supported |
| [text.wide.emoji-flags](https://terminfo.dev/text/emoji-regional-indicator-flags) | `🇺🇸` | 2 (supported) | supported |
| [text.wide.emoji-vs16](https://terminfo.dev/text/variation-selector-16-emoji) | `☺️` | 2 (supported) | supported |
| [text.wide.emoji-zwj](https://terminfo.dev/text/emoji-zwj-sequences) | `👨‍👩‍👧` | 2 (supported) | supported |

The stored-cell contract combines the full UTF-8 specimen in the App’s row text, the physical wide and continuation flags, the cursor, and an untouched guard row. The following X occupies the second terminal column for the combining case and the third for the flag, VS16, and ZWJ cases. The single emoji fixture has no X.

The pinned 4.0.0 terminal callback grades ASCII calibration and cursor advance. The replay uses the raw cursor reports from the pinned width helper’s procedure, with DA fences added for ordering. The parser callback is checked separately against the retained cell projections in the real-bundle replay test. The report covers stored text and cursor movement in these fixtures. Glyph appearance and font fallback require different observations.

The historical stock collector already supported these five IDs. This standalone assessment adds owned App evidence for them. The existing qualification inventory tracks stock-inconclusive and ungraded IDs.

## Reproduce

Run `node scripts/terminfo/unicode-width-publication.mjs --check` to rederive the report from [the immutable run archive](owned-run.zip). Run `NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/unicode-width-grading-collector.test.mjs` to compare both probe paths with the original 4.0.0 bundle.

Run `eb455801ddf173ab06638ecfbba1ec37` started 2026-11-25T21:45:56.292Z. Grid 41 × 140. Sidecar 0.14.0+c3aea76129a49b5193e788c97c0858342a2aaef8 on macOS 27.0.1 (26A434), arm64. Measured collector revision `7df112c65696c62fad68dcba72abc74944d314d6`. Fixture cleanup and desktop lease release were verified.

Build the sidecar from the recorded App revision with `scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`.

Collect with `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+c3aea76129a49b5193e788c97c0858342a2aaef8 --scope unicode-width` using the recorded collector revision and the shared desktop lease.
