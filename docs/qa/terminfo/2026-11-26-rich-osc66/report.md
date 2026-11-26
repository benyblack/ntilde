# Rich TUI OSC 66 text sizing

One disposable local Ntilde tab measured `extensions.osc66-text-sizing` on current main. The [frozen Rich TUI map](../2026-11-26-rich-tui/evidence-map.json) already grades the stock case supported.

## Historical replay

The original terminfo.dev 4.0.0 callback rederived the before and after grades from the [committed November 1 trace comparison](../2026-11-01-text-sizing-comparison.json). The before run stayed at `1;1` after both writes. The after run returned `1;1` → `1;3` → `1;5`. The comparison preserves the ordered writes and all three raw replies for each run. The original ignored run directories were unavailable.

## Pinned cursor replies

| Exact fixture stage | Child CPR | Physical App cursor (1-based) |
| --- | --- | --- |
| home | `CSI 1;1R` | 1, 1 |
| width-cpr | `CSI 1;3R` | 1, 3 |
| scale-cpr | `CSI 1;5R` | 1, 5 |

The pinned callback clears the first line and writes one space with `w=2`, then one space with `s=2`. Its terminal verdict rederived as `supported` from the retained replies. The callback grades two columns of advance per write on the same row.

## Owned visible-glyph pixels

| Separate visible fixture | Ordinary ink bounds | OSC 66 ink bounds | Owned outcome |
| --- | --- | --- | --- |
| `w=2;MMMM` | 35 × 11 px | 18 × 11 px | supported |
| `s=2;M` | 8 × 11 px | 14 × 21 px | supported |

Explicit width kept the visible ink inside two cells. Scale enlarged a single glyph across both rows of the two-by-two-cell region. No pixels outside either target changed. The ordinary controls used the same anchor and font. Erasing the fixture restored the guarded blank frame, and leaving the alternate screen restored the original frame.

The 16 PNGs are paired, fixed-phase offscreen App renders, bracketed by the same screen state. The measurements cover ink bounds and confinement for this font and fixture. Native desktop appearance and exact font parity are outside the measured scope.

## Reproduce

The [owned archive](owned-run.zip) contains the child PTY exchanges, screen journal, paired PNGs, profile, source snapshot and cleanup proof. The [machine-readable report](report.json) retains cursor replies and pixel evidence independently. Run `b9fd02ef8d3c007da280ba83a0eac01d` used macOS 27.0.1 (26A434), arm64 and a 41 × 140 grid. App revision `ad81a4495bab53dc48903bc71bc972d9d71b1542`, collector revision `515c874eb3d3ff4ed9c9ae4066d7f05f95d2c71b`. The pane and kernel PTY state were restored, the fixture was removed, and the desktop lease was released.

Build the App revision with `scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`. From the recorded collector revision run `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+ad81a4495bab53dc48903bc71bc972d9d71b1542 --scope osc66`.

Run `node scripts/terminfo/osc66-publication.mjs --check` to rederive the historical cursor grades and verify the new receipt, reply grade and PNG comparisons. This batch leaves stock and inventory totals unchanged.
