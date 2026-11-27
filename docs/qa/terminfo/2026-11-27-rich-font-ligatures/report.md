# Rich TUI font ligatures

The owned `extensions.font-ligatures` fixture is **supported** after fixing the App settings path and renderer. The digest-pinned terminfo.dev 4.0.0 bundle has no registration for this case. The frozen Rich TUI row has no stock or common-harness grade. [Frozen assessment](../2026-11-26-rich-tui/report.md)

## Glyphs and cells

The pinned face is Fira Code 6.2 Regular (`Version 6.002`, PostScript `FiraCode-Regular`) at 26 DIP. The release SHA-256 is `5992ab9640e2df491b2f609467b1de60e8bc39b2c28db184342a0592d98f6117`. The App resolved a macOS font stream with SHA-256 `17b8fa486c637b165ef747570fe84539e5c9423f1a10483423df6948f72893b1`. All 18 OpenType tables agree byte for byte.

Independent HarfBuzz shaping identified the glyph IDs and advances with contextual ligatures disabled and enabled. The checked-in oracle samples those glyph outlines independently of App rendering. Each App crop agreed with its expected outline more closely than with the other outline, within the fixed coverage tolerance. [Glyph reference](oracle.json)

| Fixture | Changed pixels, off to on | Changed pixels, on to partially selected | Selection (zero-based row, column) |
| --- | ---: | ---: | --- |
| `L=>R` | 215 | 631 | (2, 3) |
| `L!=R` | 242 | 671 | (4, 4) |

Every pixel outside the two operator cells stayed fixed for each comparison. The selected crops exactly matched the ordinary selected controls. Clearing the synthetic selection restored the unselected pixels, and the final off run reproduced the first off run byte for byte at the pixel level.

The App reads retained `L=>R` and `L!=R` throughout. The child CPRs were `CSI 3;4R`, `CSI 3;5R`, `CSI 5;4R`, `CSI 5;5R`, agreeing with physical App reads at both logical columns of each operator.

On the baseline App, the private settings requested ligatures on and the pane reported them off. Those pixels matched the fixed App with ligatures off. The fix carries the global or profile setting into the pane, uses the font’s ASCII substitutions on logical columns, and splits the shaping run at the selection boundaries. Tests cover both halves of each operator, selection removal and effective settings.

## Reproduce

Four disposable local tabs used macOS 27.0.1 (26A434), arm64 with a 23 × 74 grid. The ordinary captures use scale 1 and a fixed presentation time with the cursor hidden in the fixture. The selection is a render-only App control. The receipt binds the exact PTY bytes, screen reads, original PNGs, profile, executable/assembly digests, source snapshot, restoration and lock release. [Retained archive](owned-run.zip) · [Machine-readable result](report.json)

Build the baseline `db411becbe33922691e29d78b0ab7dc443d26b14` and fixed App `c591fd155a150447f86f69c724e72102a9185c18` into separate private directories using `scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`.

Install the pinned Regular face from [the Fira Code release](https://github.com/tonsky/FiraCode/releases/tag/6.2). Use Node 24+ in collector revision `c591fd155a150447f86f69c724e72102a9185c18`. Run `NTILDE_TERMINFO_LIGATURES=on node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version VERSION+APP_SHA --scope font-ligatures` once against the baseline, then with `off`, `on`, `off` against the fixed App. Each invocation acquires and releases the shared desktop lease.

Run `node scripts/terminfo/font-ligatures-publication.mjs --check` to verify all retained runs and rederive the verdict. To regenerate the shaping oracle use `python scripts/terminfo-font-ligature-oracle.py docs/qa/terminfo/2026-11-27-rich-font-ligatures/oracle.json --check` with fontTools 4.61.1 and uharfbuzz 0.53.3. The case covers these two ASCII operators and this resolved face in the measured macOS renderer.
