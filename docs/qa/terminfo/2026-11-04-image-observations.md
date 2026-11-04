# Owned static-image observations — 2026-11-04

The completed disposable macOS collection retains **23 static-image stages**:
21 passing stages, one calibrated SIXEL native-raster negative and one causally
inconclusive clear stage. Four cell-sized iTerm/Kitty cases pass; the SIXEL case
remains negative under the explicitly chosen stable-metric projection. A completed
collection does not mean every image feature passed.

The [machine-readable publication](2026-11-04-image-observations.json) retains
the full current image report, context, process proof, source inventory and all 15
receipt/count summaries. The [complete evidence ZIP](2026-11-04-image-observations-evidence.zip)
retains every original file, all 15 full reports and journals, 137 captured PNGs,
three source PNGs and the other four immutable image inputs. The JSON duplicates
only the focused image report; the other reports remain complete in the archive.

| Measurement | Value |
|---|---|
| App build and adapter source | `ff169411547425d706bcf9a26e7614b40c39bb64` |
| Supplied version | `0.13.0`; operator supplied, not independently attested |
| Runtime / grid | macOS arm64, Node `v24.22.1`, 40×140 cells |
| Owned run ID | `7aa3740a43eabef4cec7b1ccc0ee69be` |
| Distinct stock run ID | `2d2433d5082b544ef96f6d01fcadd4ae` |
| Source scope / image experiment | Fixed 67 files, graph policy v3 / image policy v2 |
| Full receipt | Schema 10, all 15 suite bindings, fixture removed |
| Image capture plan | 31 paired complete-state frames, 30 admitted writes, 62 original PNGs |
| Input + image byte controls | 110 distinct paired challenges; healthy 80/30-attempt seals |
| Cleanup / source removal / receipt | `19:04:35.822Z` / `19:04:36.264Z` / `19:04:40.155Z` |
| Complete archive | 255 files, 996,321 bytes |
| Archive SHA-256 | `9416be18cb059e379d49e287a4b8e85c2d7ded6a5325754fb096bd8307add451` |
| Image report SHA-256 | `7ae841e2411ace1a94b242b21397deaa5a941f5fcb2cc07dcb7fdfc184cdbd76` |

The fixed cases cover SIXEL native raster, iTerm2 explicit stretch/default aspect,
and Kitty numbered storage, placement, soft/hard deletion and number reuse.
Every image write independently queries the controlled OSC11 background and
protocol modes, then observes a fresh paired raw-byte fence. Exact raw replies,
original PNG pairs, complete cells/modes, actual draw metadata and source inputs
remain separate facts. An acknowledgement cannot make a pixel assertion pass.
Allocation and visible/removal prerequisites constrain later qualification;
actual cleanup operations still run after a semantic negative.

## Calibrated SIXEL negative

The independently parsed fixed packet declares 16×16 and paints 256 pixels in a
16×16 region. Ntilde's pinned three-band decoder canvas is 16×18, with two
transparent tail rows. This canvas policy is distinct from the declared raster
and the actually painted region. Actual bitmap and reservation dimensions agree
with that independent source definition.

| Independent geometry fact | Retained value |
|---|---|
| Parse-time creation denominators | 17×37, exact float32 values, both `sixel-override` |
| Controlled capture grid | 9×19 output pixels per cell |
| Ceiling-cell reservation | 1×1; independently correct using float32 division |
| Native canvas projection | About 8.4706×9.2432 output pixels |
| Native painted projection | About 8.4706×8.2162 output pixels |
| Observed painted bounds, diagnostic only | 9×17 output pixels |
| Safe native pixel mismatches | 80 |
| Whole-cell compatibility mismatches | 0; diagnostic only |

Native expectation uses source coordinates divided by the creation denominators,
then projected through the controlled grid. It is never fitted to observed bounds
or replaced by the ceiling-cell destination. A fixed one-output-pixel analytic
edge band bounds raster uncertainty; all four safe color interiors and exterior
background are independently testable. The source-derived comparison region is
independently blank and all non-target assertions pass.

The zero-mismatch whole-cell projection describes the current stretch; it does
not earn native success. The native stage remains failed, while the subsequent
clear remains inconclusive because its prior show witness did not qualify. Its
actual disappearance and cleanup assertions are still retained. This is a valid
negative in the tested stable-metric creation-cell projection, not a claim about
physical monitor pixels or every font/DPI/resize scenario. The separate production
sizing/lifecycle correction is [issue #851](https://github.com/benyblack/ntilde/issues/851).
Animation, Unicode placeholders and the rest of [#770](https://github.com/benyblack/ntilde/issues/770)
remain open. See the [bounded fixture/oracle contract](static-image-model.md).

## Complete bound outcomes

| Suite | Passed | Failed | Inconclusive |
|---|---:|---:|---:|
| ASCII cells | 8 | 0 | 0 |
| SGR indices | 17 | 0 | 0 |
| RGB / underline | 20 | 0 | 0 |
| Rendition / charsets | 19 | 0 | 0 |
| Cursor modes | 9 | 0 | 0 |
| Text pixels | 25 | 0 | 0 |
| Runtime colors | 10 | 0 | 0 |
| Erase | 13 | 0 | 0 |
| Cell editing | 22 | 0 | 0 |
| History / editing | 30 | 0 | 0 |
| Resize | 3 | 0 | 0 |
| Saved state / reset | 9 | 0 | 0 |
| Metadata / private UI | 10 | 1 | 2 |
| Generated input | 39 | 0 | 0 |
| Static images | 21 | 1 | 1 |

The input result qualifies 39 declared Avalonia events routed into the owned
local child, including its independent raw-byte controls; it does not qualify
hardware input, native NumLock, SSH/mux or other-platform runtime. The 25 passing
text-pixel cases are separate from image-protocol pixels and desktop cadence.

Metadata is **10 passed / 2 inconclusive / 1 negative** in this run. The private
OSC9 toast was observed and auto-hidden with `windowActive=false`; this is not
an operating-system notification or broad focus-delivery claim. Both shell-P
variants remain inconclusive. Raw OSC title clearing succeeded, while the
intentional profile/cwd display/tab fallback stayed nonempty, so the existing
literal-empty-label expectation remains negative. It is not retention of the
old raw title. No expectation was softened to make the collection pass.

Stock output is **84 supported / 0 unsupported / 171 inconclusive** with
`suiteComplete=false`. Relative to the [published v9 run](2026-11-04-input-observations.md),
only `modes.reverse-video` and `modes.alt-scroll-1007` query-recognition grades
change. The ungraded reverse-wrap geometry diagnostic remains. These raw grades
are separate from the owned image results; the frozen 78/0/177 baseline and every
earlier artifact remain unchanged. The older private v1 preparation had 83/0/172,
not 82/0/173.

## Earlier preparation attempts retained

The [753f0dd9 failed-restoration archive](2026-11-04-image-preparation-1-failed-restoration-evidence.zip)
contains all 241 original files: 827,901 bytes, SHA-256
`0a4ea679d91d2c29de92140e857fb7c155c550f761cf3e3dda34195e7768358c`.
Owned run `96fadcffad5d0956076a72711e592f74` failed at `images-finish`:
original visible cursor became hidden after bare RIS. Both the exact failed
pair and the healthy 30-attempt raw capture remain; there is no completed receipt
or published image grade. The fixture now explicitly restores the original
observed cursor boolean. [RIS visibility](ris-cursor-visibility.md) is a separate
core contract; no visibility field or PNG was ignored to obtain completion.

The [4dbe4857 uncalibrated v1 diagnostic archive](2026-11-04-image-preparation-2-uncalibrated-evidence.zip)
contains all 255 original files: 1,004,416 bytes, SHA-256
`8342a25beca8297675d9b0e32412f9fcbb8a19ef4ddebc8bf67f59087fd8e5c5`.
Owned run `01acdd7e7087bb16a2e3946b83f29f79` completed structurally under image
policy v1. Its original 21/1/1 model counts and schema 10 receipt remain unchanged,
but creation-time calibration was absent. Its 9×17 bounds cannot establish a
native-pixel defect, and the current denominators are not retrofitted onto it.
It is a preparation diagnostic, not a second calibrated terminal grade.

The current archive, both preparation archives and all original directories are
byte-preserved. No older model was rerun to rewrite its outcomes. Their image
experiment versions and qualification limits remain distinct.

The public context omits only its top-level executable path, following the prior
publication convention. The complete original context, proofs and all recorded
binary/source hashes remain in the archive. A build commit, supplied version or
matching binary hash is not binary-to-source attestation. Matching theme/cell
backgrounds only are qualified; different-background compositing, live desktop
cadence, exhaustive raster fidelity and Windows/Linux owned runtime are separate.

Portable guards pin the JSON, every ZIP/member and the fixed 67-file source map.
For the current calibrated archive, they
rederive all 15 reports and the complete finalizer in private copies, compare every
receipt field except completion time, and reject coherent native-result promotion
or metadata stripping. Frozen v8/v9 publications and their guards remain unchanged.
