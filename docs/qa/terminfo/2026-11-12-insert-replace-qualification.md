# Insert/replace mode qualification

This batch measures only `modes.insert-replace` from `terminfo.dev@4.0.0` (upstream bundle SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`, probe hash `16a690f34c0e`). It adds evidence for
existing behavior; no production code changed. The [retained run](2026-11-12-insert-replace/) and its publication test rederive
the full cleanup receipt, all required predecessor suites, the journal and both pinned probe paths.

The pinned termless feeds, in order, are `ESC[4l ESC[1;1H ESC[2K ABC`, `ESC[1;1H`, `ESC[4h`, `X`, and `ESC[4l` in `finally`.
The pinned stored-cell criterion is **supported**. The separate terminal path writes `ESC[1;1H ESC[2K`, `ABCD`, `ESC[1;2H`,
`ESC[4h`, `X`, `ESC[4l`, then queries the cursor; it remains **inconclusive**. Its replay here uses the parent-observed cursor
projection, not a PTY cursor reply. The separately run stock collector also reports **inconclusive** for this id. The bounded
Ntilde effect contract is **supported**; these four results remain distinct.

The owned sidecar used a 40-row by 140-column viewport, full vertical and horizontal margins, origin mode off, and auto-wrap
disabled during the experiments. Stored mode readback corroborated the enable and disable transitions. Coordinates are zero-based:

| Control | Before | After | Cursor after |
|---|---|---|---|
| Pinned insertion | `ABC` | `XABC` | `(0,1)` |
| Disable and print `Y` | `XABC` | `XYBC` | `(0,2)` |
| Two inserted characters | `ABCDEFGHIJ` | `ABxyCDEFGHIJ` | `(1,4)` |
| Then replace with `Z` | `ABxyCDEFGHIJ` | `ABxyZDEFGHIJ` | `(1,5)` |
| Right edge, insert `QR` | `WXY` | `QRW` | `(0,139)` |

The effect grader compares the full screen, not just these displayed excerpts. The rows above and below the multi-character
control and all unrelated cells remained unchanged. Shifted D and E retained their complete bold stored tuples; the printed x and y
matched separately measured cells under the current palette-4 background pen. The initial IRM state was replacement and wrap was
enabled; both and the original captured main screen/state were restored. The completed cleanup receipt records the disposable
fixture's removal. A non-default original horizontal-margin state is refused before the fixture writes anything.

The run used scripts commit `3b99470bfd5426b3a72fb969e7ae666a8e3cddcf` and an uninstalled Release sidecar built from App source
revision `2d8433d9bee2dc7fa15895935e08eefe7f21354b`; the intervening merged title/icon batch changed no `src/` files. The App
executable SHA-256 was `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8` and the mirrored assembly SHA-256
was `62f27bb8f9edfc7b865e676ce23acb94ce97c701812577be7251588651b5b4fa`. The runner records the App version `0.13.0` as
operator-supplied, not independently verified. Source policy v48 binds the executed script graph and selected production reference
files; it is not a complete binary-to-source attestation. Receipt schema 54 binds owned run `91a97a7cf97b3d6e8c6d0f805fe26228`.
The [first completed run](2026-11-12-insert-replace/superseded/attempt-1.zip) is retained as superseded: Sonar findings changed
measured collector, replay, receipt and source-checking scripts, requiring the second run. It also graded the pinned and effect paths supported.

Recheck with `node --test scripts/terminfo/insert-replace-publication.test.mjs`. The pinned stock collector remained incomplete due
to its separately recorded reverse-wrap geometry diagnostic. No stock-score, common-harness-score or Core-percentage change follows
from this batch. This is local macOS stored ASCII state, not Unicode editing, wide or combining cells, rendered pixels, or a
cross-platform claim.
