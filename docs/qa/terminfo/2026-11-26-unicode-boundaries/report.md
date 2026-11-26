# Ntilde Unicode boundary qualification

Three cases measured in a disposable ordinary local tab on macOS. Together with the [five width cases](../2026-11-25-unicode-width/report.md), this covers the eight fixtures in the [Unicode baseline](https://terminfo.dev/baseline/unicode).

| Case | Pinned PTY replay | Owned stored-cell effect |
| --- | --- | --- |
| `unicode.east-asian-ambiguous` | supported | supported |
| `unicode.grapheme-cursor` | supported | supported |
| `unicode.wrap-boundary` | supported | supported |

The ambiguous-width symbol ● occupied 1 column in this profile. The original probe accepts either one or two columns.

The family emoji 👨‍👩‍👧 occupied 2 columns as one stored cluster. The pinned cursor probe measures the advance after the write. In a supplemental Backspace check, the cursor returned to zero-based column 0 and the cluster remained in place.

Both wide-wrap fixtures stored 中 and its continuation at the start of the second row. The parser fixture seeds a full ASCII row and uses CUP to reach the final column with pending wrap cleared. The PTY fixture stops the ASCII seed one column short and observes CPR at row 2, column 3, using one-based coordinates. Each seed row was preserved.

The real-bundle test runs both unchanged 4.0.0 callbacks against their respective retained observations. The effect verdicts come from physical cells and the App cursor. The historical stock baseline already supported these IDs. The owned evidence here covers this App and these fixtures, with glyph appearance and other width policies outside scope.

## Reproduce

Run `node scripts/terminfo/unicode-boundary-publication.mjs --check` to rederive the report from the [owned run archive](owned-run.zip). Run `NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/unicode-boundary-grading-collector.test.mjs` to replay the original callbacks.

Run `dfb282c4652eda8cea4df315a764df57` started 2026-11-25T22:08:26.656Z on macOS 27.0.1 (26A434), arm64, using sidecar `0.14.0+3138158796f02c6bbec9bab7ee28ad6dde6b3249` and collector `94ee43cf41ac059d48deb8a421a5c1d5396b12af`. The grid was 41 × 140. The observer verified cleanup and the desktop lease was released.

Build the recorded App revision with `scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`.

From the recorded collector revision, collect with `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+3138158796f02c6bbec9bab7ee28ad6dde6b3249 --scope unicode-boundaries`.
