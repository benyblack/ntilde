# Kitty and iTerm2 images in persistent sessions — design (#838)

Decision (2026-11-06): **support** Kitty graphics and iTerm2 inline images in persistent
(multiplexer-backed) panes, rather than documenting them as Sixel-only.

## Status (2026-11-06)

Delivered: PR 2 (#881), PR 3 (#882), PR 4 (#884) and PR 5, the coverage update and the manual check
(`docs/qa/terminfo/2026-11-06-persistent-images-check.md`). PR 6 (`t=f`) is not done. Where the
delivered work differs from the design below:

- **`readScreen` carries no Kitty or iTerm2 images**, rather than the newest that fit. Its readers
  read text, and the export skips the image bytes instead of copying them on the parse thread.
- **The window's `AllowNativeKittyGraphics` is not carried.** Every side of a session must decide
  every image alike, so no one window's setting can apply. Image sessions trust native APC, the
  setting's default.
- **Evicted sources keep their images in snapshots**, with no bytes: an evicted Kitty asset keeps its
  id and size, so a later placement finds it on every side.
- **Snapshots stay within the restore's inflation limit.** The source budget also bounds what
  validating the retained compressed sources inflates, so every export restores.

## How images reach a persistent pane today

- **Every side parses the same bytes.** The daemon (`HeadlessTerminalSession`) parses the
  child's output and then broadcasts the same bytes in `Output` frames. Each window
  (`MuxClientSession` → `TerminalPane`) and the text client re-parse them with their own
  `AnsiParser`. Only the daemon's replies reach the child.
- **So placement must be identical everywhere.** Cursor movement, reserved cells and
  scrolling stay in step only if the daemon and every client make the same place/skip and
  cell-span decision for every image.
- **Sixel already meets that.** Both sides measure with the decoder-free
  `SixelGeometry.Measure`. The DCS text is retained as a bounded `SixelSource` (256
  sources, 2 Mi chars) and carried in snapshots (`TerminalStateSnapshot.Sixels`), so
  attach, reattach, `readScreen` and replay restore Sixel images. The window decodes
  lazily at draw time.
- **Kitty and iTerm2 decide with a full decoder.** Decode success, the pixel size and
  Skia's codec support set place/skip, the default cell span and the Kitty reply. The
  daemon has no decoder, and can't have one: `Ntilde.Mux` may not reference SkiaSharp
  or Rendering (layering tests), and the remote `ntilde-mux` has no package references
  at all. So today the daemon refuses (`ERR`, `ENOTSUP:Graphics disabled by host`),
  windows skip identically, and snapshots and replay exclude both protocols (`ImportState`
  clears Kitty assets and placements).

## Design

### 1. Placement from image headers, not the decoder (every pane)

A decoder-free **`ImageHeaderProbe`** in `Ntilde.VT` reads the pixel size from the
image header:

| Format | Where the size comes from |
|---|---|
| PNG | `IHDR` |
| JPEG | the first `SOFn` |
| GIF | the logical screen descriptor |
| WebP | `VP8 `, `VP8L` or `VP8X` |
| Kitty raw `f=24`/`f=32` | `s` and `v` |

- **The probe is the only input to geometry.** It decides accept or refuse, the pixel
  size and therefore the default cell span, the 2000-pixel limit, and the Kitty reply.
  Kitty `o=z` is inflated in VT with `ZLibStream`. A raw image needs the exact
  `s * v * bpp` length; a container needs only its header.
- **The decoder only supplies pixels.** A window whose decoder later fails to decode
  bytes with a valid header draws nothing in cells that are already reserved, as an
  undefined Sixel register does. Geometry never changes after the probe.
- **The same rule applies in direct panes**, so there is one behaviour to test and replay
  matches live output.
  - Visible change: an image with a valid header and a corrupt body now reserves its
    cells and gets `OK`, where today it is skipped with `EINVAL:Invalid image data`.
  - A format the probe can't size (for example AVIF, or SVG via iTerm2) is refused
    everywhere. Today Skia might accept it in a direct pane.

### 2. Retained image sources and snapshots (every pane)

- **Keep the encoded bytes.** Like `SixelSource`, Kitty assets and iTerm2 images keep
  their encoded bytes, as transmitted or after `o=z` inflation, in a bounded
  **`ImageSourceBudget`**. Proposed limits per pane or session, subject to the size of
  the existing 256 MiB snapshot queue and 64 MiB frame limits:
  - 32 MiB of encoded bytes
  - 128 Kitty assets
  - 256 iTerm2 images
- **Eviction order:** retired and unplaced first, then the oldest. An evicted source
  leaves its placement as reserved blank cells, as `SixelSource.Data == null` does.
- **Snapshots carry them.** `TerminalStateSnapshot` gains a bounded image section: Kitty
  assets (id, number, alt screen, format, source bytes) and placements (image id,
  placement id, cell rectangle, clip), plus iTerm2 images (source bytes, cell rectangle,
  clip, `preserveAspectRatio`).
  - Export and import mirror `ExportSixelState`/`ImportSixelState`: import keeps the
    newest that fit, and never replays placement or moves the cursor.
  - Clients decode on restore, through the parser's decoder (deferred, as for Sixel).
- **Replay** gets this through the same snapshot, so a recording that starts mid-session
  restores its images.
- **`readScreen`** (4 MiB cap) keeps images only if they fit, newest first.

### 3. Persistent sessions: negotiation, daemon and clients

- **Negotiate a capability.** Add an image policy to Hello/Welcome and the
  spawn/attach/snapshot fields, following `SixelRasterPolicy` and `SixelPolicyAdmission`:
  - A session's policy is fixed when it is spawned.
  - A peer that doesn't support the session's policy is refused (`version_mismatch`),
    never silently downgraded.
  - An old daemon gives new windows today's behaviour, and old windows can't attach to
    an image-enabled session.
- **Daemon.** With the policy on, the headless parser allows non-Sixel images using the
  probe; it still has no decoder.
  - It stores sources and placements and answers Kitty queries and commands
    authoritatively (`OK`, `EINVAL`, `E2BIG`, `ENOENT`, as in a direct pane).
  - On Windows the daemon's ConPTY gate (`IsNativeKittyBlocked`) keeps today's semantics:
    tunnelled Kitty always works, and native APC follows the window's
    `AllowNativeKittyGraphics` setting carried in the presentation.
- **GUI window.** With the policy on, the pane sets `AllowNonSixelImages` and gets a
  decoder for Kitty and iTerm2 too, and decodes for display only. It never decides
  geometry from the decoder (section 1).
- **Text client** (`ntilde mux attach` in another terminal): it gets the probe, so it
  reserves identical cells, and shows no pixels, as today for Sixel.
- **Remote `ntilde-mux`.** The probe is in VT and the inflater is in the BCL, so the
  remote daemon supports the policy with no new dependencies.

### 4. Kitty `t=f` (file transport) in persistent panes

`t=f` paths name files on the daemon's host. In a direct pane the App reads them
(temp-dir confinement and a size cap); in a persistent pane the window can't.

- **First pass:** in persistent panes, `t=f` stays unavailable and the Kitty query
  answers `ERR` for it, as today, so programs fall back to `t=d`, which every one we know
  of supports.
- **Follow-up, if wanted:** the daemon reads the file under the same confinement and
  forwards the bytes to clients in a new ordered frame, keyed to the stream position.

### Out of scope

These remain as today:
- Kitty animation, frames, composition and Unicode placeholders (`U=1`, tracked in #770)
- shared memory (`t=s`)
- the `w`/`h` crop semantics

## Delivery (each a separate PR, in order)

1. **Docs fix (small).** The user guide and the persistent-sessions announcement say no
   images, Sixel included, show in persistent panes. Correct that to "Sixel only, Kitty and
   iTerm2 coming", and record this design.
2. **Header probe and placement rule** (VT, every pane). Tests:
   - each format's header, plus truncated, corrupt and oversized inputs
   - with and without a decoder, Kitty and iTerm2 give identical cursor, cells and
     replies
   - the visible changes above, pinned
3. **Retained sources and the snapshot image section** (VT, Replay, every pane). Tests:
   - export and import round trips, with budgets and eviction
   - snapshot size limits
   - restore draws in the App
   - replay from a mid-session snapshot
   - schema compatibility: old snapshots still import
4. **Mux policy and enablement** (Mux.Contracts, Mux, App, text client). Tests:
   - Hello/Welcome and spawn/attach negotiation, refusal of mixed versions
   - headless daemon replies for query, transmit, place and delete, including chunked
     transfers that span an attach
   - reattach and reconnect restore images within the budgets
   - the text client reserves identical cells
   - Windows, macOS and Linux persistent tabs answer `OK` to #838's probe (headless
     tests on all three CI platforms)
5. **Coverage and qualification.**
   - coverage matrix, conformance report, `ARCHITECTURE.md`, `MODULE_OWNERSHIP.md` and
     the user docs
   - a manual check with real programs in a persistent tab, versions recorded: for
     example `kitty +kitten icat` or `timg` for Kitty, and `imgcat` for iTerm2
   - historical terminfo raw runs and grades stay unchanged
6. **Optional:** `t=f` in persistent panes (section 4).

## Risks

- **Divergence** between the daemon and a client would desynchronise the cursor. PR 2's
  property tests (same bytes, decoder or none → identical buffer state) are the guard,
  and snapshot restore resynchronises on attach.
- **Memory:** encoded sources are bounded per session (section 2), and the daemon never
  holds decoded pixels.
- **Behaviour change in direct panes** (section 1) is visible only for images whose
  header is valid but whose body is corrupt, or whose format the probe can't size. The
  PR description and the matrix call it out.
