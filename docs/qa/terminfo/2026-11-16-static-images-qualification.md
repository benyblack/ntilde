# Static SIXEL and iTerm2 inline-image pixels, qualified (2026-11-16)

Issue [#987](https://github.com/benyblack/ntilde/issues/987), parent [#770](https://github.com/benyblack/ntilde/issues/770). This qualifies the two exact `terminfo.dev@4.0.0` cases
`extensions.sixel` and `extensions.iterm2-images` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`, probe hash `16a690f34c0e`) from
rendered pixels. It adds **no production change and no measured-source change**: the measurement is the existing owned static-image suite
([model and oracle](static-image-model.md)), run fresh on the unmodified current main App; the qualification is a publication-side projection with its own contracts and controls. The cases sit outside the
frozen Core catalogue, so **no Core percentage changes**, and the stock grades stay inconclusive.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.sixel` | inconclusive by design | inconclusive by design | **unsupported** (native raster fidelity; [#851](https://github.com/benyblack/ntilde/issues/851)) | inconclusive |
| `extensions.iterm2-images` | inconclusive by design | inconclusive by design | **supported** | inconclusive |

The four result kinds stay separate. Both pinned callbacks are replayed from the retained pinned bundle (three source spans pinned by SHA-256) and return a constant inconclusive observation whatever the
App declares: the termless path says "Declared … capability does not establish rendered … pixels", the terminal path "No pixel readback …". Both divergences are recorded as
`pinned-inconclusive-by-design`. The stock collector, re-run in the same owned run, still grades both inconclusive (161 inconclusive / 93 supported / 1 unsupported, `suiteComplete=false`), so this **does not
raise the stock compatibility score**.

## Effect contracts

Both are separately identified and never attributed to the pinned probes. Each composes carrier cases of the owned suite and requires blank-before, the independently source-derived pixels at the controlled
anchor with unchanged surroundings, and clear-after, over paired original offscreen PNGs, complete cell/mode reads and fresh raw fences.

- `ntilde-sixel-native-raster-effect-v1`: a fixed [DEC SIXEL](https://vt100.net/docs/vt3xx-gp/chapter14.html) packet (7-bit DCS, explicit 1:1 aspect, declared 16×16 raster, RGB-percent palette, unequal
  four-colour quadrants, P2=1) paints its **native** raster. The carrier case is `sixel.native-raster`.
- `ntilde-iterm2-inline-image-effect-v1`: a single-shot `OSC 1337;File=inline=1` PNG with a 4×2 cell request paints the full envelope when `preserveAspectRatio=0` and a contained undistorted fit by default, with
  an alpha-zero interior revealing the known background (the [iTerm2 documentation](https://github.com/gnachman/iterm2-website/blob/d6249e4f778822ac2ec89cc099607a3bc5ef4e3f/source/_includes/3.7/documentation-images.md#L64)).
  Carrier cases: `iterm.explicit-stretch` and `iterm.default-aspect`, both required.

## What the App did (disposable direct-local pane, 40×140 cells, 9×19 output pixels per cell)

**iTerm2.** Both cases passed all three stages. Blank-before showed no image pixels in the target and an unchanged surrounding; the explicit-stretch image covered the complete 36×38 envelope with all four
asymmetric colour interiors and the transparent hole revealing the `#101010` background; the default-aspect image was a contained 36×36 square; each cleared back to the blank reference. The acknowledgement
(a fresh paired raw fence) and every pixel assertion are retained as separate facts.

**SIXEL.** The blank stage passed. The show stage **failed one assertion, calibrated native raster fidelity**, while every independent control held: the source canvas agrees with the actual bitmap, the float32 ceiling
reservation (1×1 cells), anchor and unclipped native intent agree, the surroundings are unchanged and the write was acknowledged. The independently derived native painted region is about 8.47×8.22 output pixels at
the anchor; the observed painted bounds are 9×17, with 80 mismatching safe native pixels. The whole-cell compatibility projection has 0 mismatches, so the colours, order and anchor are right and the image is
stretched to its cell reservation, which is the defect tracked by #851 (cell reservation is distinct from native raster sizing). That diagnostic cannot earn a native pass and the oracle was not weakened. The clear
stage is therefore **inconclusive** (its show witness did not qualify); its actual disappearance and cleanup assertions are retained. This is a valid negative for the tested stable-metric projection, not a
claim about monitor pixels or every font/DPI/resize scenario. When #851 lands, the case needs a fresh measurement; this publication stays frozen.

## Validity controls

Eight controls must hold before any verdict is reported (each is checked by the publication builder and refused when broken): the complete receipt binds the exact image report and fixture removal; the report is the
paired raw render policy v2 of this run; the exclusive raw reader sealed healthily over all 30 writes; the final paired state is bound to the initial one; every write independently queried the matching
controlled OSC 11 and cell background; every capture carries its own unpredictable paired challenges, none reused from the input phase or another capture (a stale capture cannot qualify); every case began
from an independently blank target; every write was acknowledged while pixels were graded separately.

## Identities

- App: 0.14.0 (operator supplied), Release sidecar built from `49bc7b7627a1f2c7385e2c400d5d0baedaeb96d4` (arm64; host stub SHA-256 `f5814e51…9bf8`, assembly SHA-256 `88a4f5b0…e083`). Scripts: the same revision;
  no measured script changed, only publication-side modules. Source policy v62 (manifest `bd58ab31…0bfc`), receipt schema 69.
- Owned run `569294d0402a900696bef00b68490d9d`, stock run `620df11352a96f08f8a6fd01d0c01e98`, measured 2026-11-16T15:58Z on macOS, Node v24.22.2, scope `owned-osc633-without-notification-suites-v2` (omits only the OSC 9 and
  OSC 777/99 suites, whose evidence stays in the independently revalidated pinned predecessor run `b45702ad…`; combined historical and fresh evidence, not a fresh full-suite receipt).
- Image report `ntilde-owned-static-images-v1` (policy `owned-static-images-paired-raw-render-v2`) SHA-256 `f5260832…89cc`: 21 passed / 1 failed / 1 inconclusive stages over five carrier cases (31 paired frames, 62 original
  PNGs). The two Kitty carrier cases passed their stages; they are retained in the archive but **not claimed here**.
- Earlier retained image measurements are not reused: the [2026-11-04 calibrated run](2026-11-04-image-observations.md) and the image dataset inside the OSC 633 run measured older Apps. This run is the equivalent
  carrier on the current main App; both earlier SIXEL negatives agree with it.

## Evidence

[manifest](2026-11-16-static-images/manifest.json), [owned run archive](2026-11-16-static-images/owned-run.zip), [pinned bundle](2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz),
[summary](2026-11-16-static-images/summary.json) and [report](2026-11-16-static-images/report.json). Reproduce the rederivation:

```bash
node --test scripts/terminfo/static-images-publication.test.mjs
```

The test reissues the complete receipt from the retained bytes (which rederives the image report, all 62 PNG comparisons and the cleanup), replays both pinned callbacks from the retained bundle, rebuilds the
published projection, and refuses stale/reused captures, stage promotion, a missing stage and a coherent native-result rewrite. A fresh measurement needs the shared desktop lock and
`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope osc633` on Node 24.

## Limits

One fixed SIXEL packet and one single-shot PNG at a 4×2 cell request, in a disposable direct-local App on macOS only. Not qualified: animation, Unicode placeholders, multipart or other iTerm2 formats, other SIXEL
rasters or aspect settings, font/DPI changes, different-background compositing, exhaustive raster fidelity, live desktop cadence, remote/mux, Windows/Linux, common-harness or Core scores. A pinned verdict, an
effect verdict, the stock grade and any Core/Modern comparison remain distinct.
