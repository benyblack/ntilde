# `extensions.truecolor`: retrospective exact-probe replay

The pinned `terminfo.dev@4.0.0` **termless** callback for `extensions.truecolor`
returns **supported** on the independently journaled foreground cells from the
existing owned SGR-truecolor run. Its exact feed is byte-for-byte the already
measured `sgr.fg.truecolor` fixture. The existing independent stored/resolved
foreground effect contract also returned **supported**. There is no verdict
divergence. This publication replays a different pinned callback over retained
observations; it is **not a new desktop measurement** and does not change the
carrying run or its receipt.

The callback writes `C ESC[31m A ESC[34m B ESC[38;2;255;128;0m X
ESC[38;2;17;97;201m Y`. In the measured default theme, A and B resolved to
`205,0,0` and `0,0,238`; X and Y stored literal RGB `255,128,0` and
`17,97,201`, with palette indices and default markers cleared, and resolved to
those same values. The independent existing contract also validates neutral and
reset states, unrelated attributes, the other color channel, reverse-video off,
and literal-versus-palette and boundary controls. It does not establish painted
pixels, colon syntax, every RGB value, other themes or platforms.

The exact extension callback has its own criterion and its own inconclusive and
unsupported branches. It is executed unchanged from digest-checked source spans
in the carrying run's receipt-bound collector; its feed must match the measured
fixture. The projection rederives the carrying suite from its journal and run
log, and the publication test reconstructs the complete cleanup receipt from the
retained archive. It exercises changed source/fixture, missing cleanup, wrong
run, corrupted journal, mismatch and mimic controls. The [retained compressed bundle](2026-11-16-extensions-truecolor/terminfo-dev-4.0.0.bundle.mjs.gz)
is decompressed and its source spans are checked during rederivation against the pinned bundle SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`.

The same run's **stock terminal path is inconclusive** for this case: its
separate `255,0,128` write and cursor-position query established consumption,
not cell color. The stock run recorded 93 supported, 1 unsupported and 161
inconclusive, with `suiteComplete=false`; these counts are neither revised nor
combined with the private replay. The frozen Modern requirement already recorded
a semantic pass using `sgr.fg.truecolor`; this publication adds no Modern or
Core point, and makes no common-harness comparison claim.

The carrying [kitty color-control publication](2026-11-15-kitty-color-control-qualification.md)
retains the immutable archive and receipt. Run `55f0ba462cd3cc6fbc315a0e5fad5126`
was measured 2026-11-16T09:15:04.524Z with App version `0.14.0`, App source
revision `d82374aa8e22e7b4e1115344908e3c2bb975c7c6` and scripts revision
`ec6c5b99431af6d8fcee5083f196a585e66f2d13`. The executable SHA-256 was
`f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, the App
assembly SHA-256 was `a6a1db21fd76a0cde3d320534fe09a260d743320fefe7e8bc9ad9fdf1a8371c3`,
and the source policy was `readonly-owned-source-graph-v60`. Source identity and
binary hashes do not by themselves attest a complete source-to-binary build.

Reproduce the projection and receipt checks with:

```sh
node --test scripts/terminfo/extensions-truecolor-publication.test.mjs
node scripts/terminfo-inventory.mjs --check --rederive
```

The [summary](2026-11-16-extensions-truecolor/summary.json) and
[report](2026-11-16-extensions-truecolor/report.json) name the old receipt and
source archive; the original raw run remains unchanged. No terminal production
code was changed by this qualification.
