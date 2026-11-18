# Persistent Kitty animation limits: cost and benefit of lifting them (#1026)

Issue [#1026](https://github.com/benyblack/ntilde/issues/1026) asks whether either of two accepted limits of Kitty animation in persistent sessions is worth lifting. Persistent animation is [#1017](kitty-image-lifecycle.md); composition is [#1027](2026-11-18-kitty-animation-compose-qualification.md).
The issue's own scope makes the first step a cost/benefit note, not an implementation. This is that note. **Decision: lift neither now; reopen on a concrete report.** The reasons, the numbers behind them and what would change the answer follow.

| Limit | Decision | Why |
|---|---|---|
| 1. Only PNG sources can be a base or a container frame in a persistent session | **Do not lift** | The Kitty protocol defines `f=100` as PNG, so no conformant client sends another container; a decoder-independent JPEG/WebP/GIF reader is a large parser surface and cannot agree pixel-for-pixel with the host decoder that draws the root |
| 2. Large or noisy animations lose the oldest frames' pixels when the shared source budget evicts them | **Do not change the budget** | For frames that do not pack, the source budget and the existing per-screen pixel quota allow the same number of frames (within about one), so a bigger budget would add almost no capacity; the budget binds earlier when other retained images share it, at some sizes by one frame with nothing else retained (512x512), and independently past 512 sources, and it cannot grow much without breaking the rule that a snapshot fits one mux frame |

## Limit 1: non-PNG roots and containers

**Facts.** In a persistent session the root's pixels come from its retained bytes: raw `f=24`/`f=32` directly, a PNG through `PngPixelReader` (every standard colour type, bit depth and interlace method), and a container frame (`f=100`) is PNG only. A JPEG, WebP or GIF root, or such a
container frame, answers `ENOTSUP` for `c=1`/`r=1`/`a=c` and for the frame itself, on the daemon and every client alike. An ordinary tab with a decoder accepts them.

**Benefit of lifting: close to none.** The Kitty graphics protocol defines `f=100` as PNG data (the spec's key table: `f=100` "indicates PNG data"), and its other formats are raw RGB and RGBA. Ntilde's acceptance of JPEG, WebP or GIF
in a Kitty `f=100` transfer ([`ImageHeaderProbe`](../../../src/Ntilde.VT/ImageHeaderProbe.cs)) is a tolerance beyond the protocol, so a client written for the protocol never reaches this limit. The formats Ntilde draws in other protocols (iTerm2 inline images) are not Kitty animation roots.

**Cost of lifting: high.**

- A decoder-independent reader per format. Baseline and progressive JPEG alone is a Huffman decoder, dequantisation, an inverse DCT, chroma upsampling and colour conversion; WebP (lossy and lossless) and animated GIF are each another. All of it is untrusted input parsed on the headless daemon, which today
  reads only PNG with a reader bounded by the image header's own size.
- The result cannot be exact. A lossy decode is not bit-for-bit reproducible across decoders (IDCT precision, upsampling and rounding choices). The daemon and every client must compose the same bytes, so the VT-owned reader would define the pixels, while the host decoder draws the root itself:
  a frame built on that root would differ from it by a few levels. PNG has no such gap because it is lossless.
- A new persistent image policy (the set of formats a session reads decides what every peer accepts), so an older peer is refused rather than downgraded.

**Recommendation.** Keep `ENOTSUP`. Reopen only if a real client is shown sending a non-PNG container in a Kitty animation transfer; the cheaper answer then is to ask it to send PNG or raw pixels, as the protocol says.

## Limit 2: frames lost to the shared source budget

**Facts.** Frames live in the session's `ImageSourceBudget` (32 MiB, 512 sources, shared with the root's bytes and every other retained Kitty and iTerm2 image) as `KittyFrameCodec` packings: runs of whole pixels, at most 4.0078 bytes per pixel (one control byte per 128 pixels), a few bytes for flat frames.
Separately, every Kitty image is charged against a 64 MiB per-screen pixel quota, 4 bytes per pixel for the root and 8 per later frame (decoded pixels plus the RGBA kept for composition), with 64 frames per image; this limit applies in ordinary tabs too and refuses with `ENOSPC`.
When the budget is full it evicts retired sources, then the oldest live ones. An evicted frame can no longer be a base (`ENOTSUP`) and is carried in snapshots without pixels, identically on the daemon and every client.

**How the two limits compare.** For frames that do not pack, the budget holds `32 MiB / 4.0078 = 8.37 M` frame-pixels and the quota `64 MiB / 8 = 8.39 M`: the same to within a quarter of a percent, the budget being the slightly tighter (it also holds the root's own bytes and one control byte per 128 pixels).
Frames each side allows beside a raw RGBA root, for a noisy animation of one image whose source budget is otherwise empty, `floor((32 MiB - 4P) / (4P + ceil(P/128)))` against `floor((64 MiB - 4P) / 8P)` (computed from the constants above):

| Image | Pixels | Quota (the cap, 63) | Source budget, nothing else retained | Source budget, 8 MiB of other images |
|---|---:|---:|---:|---:|
| 128x128 | 16,384 | 63 | 510 (past the 63-frame cap) | 382 (past the cap) |
| 320x240 | 76,800 | 63 | 108 (past the cap) | 80 |
| 480x270 | 129,600 | 63 | 63 | 47 |
| 640x480 | 307,200 | 26 | 26 | 19 |
| 800x600 | 480,000 | 16 | 16 | 12 |
| 1280x720 | 921,600 | 8 | 8 | 5 |
| 1920x1080 | 2,073,600 | 3 | 3 | 2 |

This is executable, not only arithmetic. `PersistentKittyAnimationTests.Noise_frames_fill_the_pixel_quota_exactly_when_they_fill_the_source_budget_so_nothing_is_evicted_first` sends noise frames of 640x480 into a persistent session; 26 are accepted, the 27th is refused by the quota
with `ENOSPC`, every one of the 26 keeps its pixels, the root's bytes are still retained and the budget is all but full. The boundary where the two do not coincide is pinned too: at 512x512 the quota allows 31 frames and the budget 30, so `At_512_by_512_the_budget_binds_one_frame_before_the_quota_and_evicts_the_root_source`
shows all 31 accepted while the budget evicts the oldest source, the root's, with no other image involved. If a constant changes so that the premise no longer holds, those tests fail and this note needs revisiting.

**What this means.**

- Frames that pack (flat colours, sprites on a plain background, most synthetic animations) use far less of the budget than they are charged against the quota, so the budget does not bind.
- For frames that do not pack the budget and the quota bind at the same point or within about a frame of each other (the budget first at some sizes, as at 512x512). The pixel quota applies to ordinary tabs and persistent sessions alike; the source budget applies only to persistent frames (an ordinary tab keeps each frame's RGBA canvas directly and never evicts a frame's pixels). A larger source budget would add at most that frame, since the quota would then refuse the next.
- The budget binds earlier when other images share it (the last column): an animation of full-size noisy frames displayed beside several large images can lose its oldest frames' pixels. That is the case the issue names.
- **The source count is a second, independent way to lose frames.** The budget keeps at most 512 sources, one per image and per frame, whatever their size: nine 16x16 animations of 64 frames each ask for 576 and lose the oldest 64 while using about a hundredth of the bytes and of the quota
  (`Many_small_animations_lose_their_oldest_sources_to_the_source_count_long_before_any_byte_or_quota_limit`). No pixel budget, dedicated or larger, changes that; only the count does.

**Cost of lifting.**

- **A dedicated animation budget** removes the sharing, at a price. It changes what a session accounts and evicts, so it needs a new persistent image policy (every peer must account the same bytes). It adds up to the new budget's size in daemon memory per session. And a snapshot must fit one mux frame
  (`MuxProtocol.MaxFrameBytes`, 64 MiB), with the retained sources a large part of it: the budget cannot grow much beyond today's 32 MiB without a chunked snapshot, which is a mux protocol change of its own.
- **A better codec** (a real compressor) would shrink noisy frames only by what they truly compress, which for noise is nothing, and the codec is deliberately not a runtime compressor so that every peer charges the same bytes for the same frame.

**Benefit of lifting: small and unobserved.** The failure needs one of the combinations above (a noisy near-quota animation beside other retained images, a noisy animation at a size where the budget binds a frame before the quota, such as 512x512, or more than 512 sources in the session) and degrades gracefully: the image stays on screen, the animation plays, and only the oldest frames come back blank after a reattach. No issue in the repository reports an animation that hits it (the limit was accepted to keep every peer in agreement, not in response to a failure), and no measured session did.

**Recommendation.** Leave the budget, the count and the policy as they are. If a report appears, prefer the smallest change that fits it: for many small frames, raise the source count; for large noisy frames beside other images, protect the animation's frames from eviction by other images' bytes, or refuse the frame that would not fit (`ENOSPC`, as the quota does) instead of evicting an older one. Each changes what a session accounts or evicts, so each is a persistent policy-version change.
Measure the animation's real size first; an animation near the pixel quota is refused with `ENOSPC` in an ordinary tab too, and at most sizes the quota is the limit users would meet first (the budget, by at most a frame, at some).

## What would change this

- A named client that sends JPEG, WebP or GIF as a Kitty animation root or frame (limit 1).
- A reproduced case where a protocol-conformant animation, within the quota, loses frames in a persistent session while other images are shown (limit 2), with its frame size and the other images' bytes.
- A change to the constants this note's table is computed from (`MaxKittyImagePixelBytes`, `ImageSourceBudget.DefaultMaxBytes`, `ImageSourceBudget.DefaultMaxSources`, `KittyAnimation.MaxFrames`, `MuxProtocol.MaxFrameBytes`).
