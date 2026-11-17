# rxvt special-color qualification: OSC 5/105 (2026-11-17)

Qualifies one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `extensions.osc5-special-color`, whose exact request is `OSC 5;0;? BEL`. Like the
[kitty color-control batch](2026-11-15-kitty-color-control-qualification.md), **this batch adds production code**: main did not answer the request (the [exact query audit](exact-query-audit.md) recorded
matched silence), so a qualification had nothing to measure. The VT now implements rxvt's special-color protocol, and the batch measures it. The batch is restacked on the merged static-image and
aggregate batches (live source policy v64, receipt schema 71; the first run remains retained at v63/schema 70).

## What the pinned probe can say

The pinned probe is a **query-only** probe registered through terminfo.dev's `oscSimpleQueryProbe` factory. It sends `OSC 5;0;? BEL` — selector **0** of rxvt's special-color table (colorBD, the bold
color) — and is the one case of this batch whose verdict the unchanged pinned code can decide:

- termless path: `feedCapture(OSC 5;0;? BEL)` over `oscReplyResult`; terminal path: `queryWithSentinelOutcome` with the pattern `ESC ] 5 ; 0 ; rgb : h{1,4} / h{1,4} / h{1,4} (BEL | ST)`, case-insensitive.
- **supported** when the reply is one complete `OSC 5;0;rgb:…` frame ended by BEL or ST; **inconclusive** (`invalid-reply`) when the capture holds the `OSC 5;0;` prefix without a complete frame;
  **inconclusive** (`no-response`) when nothing arrives. It can never return unsupported, and it does not test setting a color, the other selectors or any rendering.

Both pinned paths are replayed byte-exactly over the journaled raw reply of the case's own fenced exchange. The pinned verdict is also the stock collector's grade in the same owned run and is
corroborated by the exact-query witness `q07.extensions.osc5-special-color` under query policy v6.

## Result

| Case | Pinned termless / terminal paths | Stock grade (this run) | Exact-query witness | Effect contract (`ntilde-osc5-special-colors-effect-v1`) |
|---|---|---|---|---|
| `extensions.osc5-special-color` | **supported** / **supported** | **supported** (was inconclusive: no reply) | matched a complete `rgb:` frame | **supported** (3 controls and 15 protocol groups matched; 5 policy groups, all matched Ntilde's choices) |

The stock collector (`7321591b0e1b9235abc4db60badcf86f`) in owned run `5f5a5946b82d34456e8167ec967d1a70` grades 94 supported / 1 unsupported / 160 inconclusive (`suiteComplete=false`), 255 graded cases.
The frozen November 9 baseline is 86 / 1 / 168, also 255 cases. Eight cases have since moved from inconclusive to supported: OSC 5 plus the separately implemented OSC 99 notification, Kitty OSC 21,
DECRQCRA, UTF-8 mouse, window title/icon reports and XTGETXRES cases. This batch newly implements OSC 5 only; those seven other changes are not attributed to it. The frozen baseline stays unchanged.
No Core point is added.

## Production

`OSC 5 ; c ; spec ; c ; spec ; … ST` sets, queries and resets rxvt's five special colors ([parser](../../../src/Ntilde.VT/AnsiParser.Colors.cs), [unit
tests](../../../tests/Ntilde.VT.Tests/Osc5SpecialColorTests.cs)). It follows rxvt's special-color text:

- selectors: the numeric special-color indices 0 (colorBD/bold), 1 (colorUL/underline), 2 (colorBL/blink), 3 (colorRV/reverse), 4 (colorIT/italic); the values are the numeric encodings the OSC 10 parser
  accepts; a value `?` reports that slot as one complete `OSC 5;c;rgb:rrrr/gggg/bbbb` frame (sixteen-bit channels, always ST, echoing the selector);
- `OSC 105 [; c …]` resets the listed special colors to their defaults; bare `OSC 105` resets all five;
- the five slots live in their own resource set: nothing here touches the OSC 4 palette, the dynamic colors OSC 10/11/12/17/19 or the kitty OSC 21 keys, and neither do those sequences touch the special
  colors (each direction is a deciding isolation group of its own);
- an unset slot reports the default foreground, this App's analog of rxvt's per-color X resource default;
- every read-direction group pins the resolved-color binding (palette, theme and render state): a collateral mutation of any unrelated color inside the exchange is rejected by the grader, and the set-direction groups pin everything except the declared setup fields;

Choices recorded as non-deciding policy: selector names (rxvt also accepts names such as `bold`) are not modeled — an unmodeled selector changes nothing and stays silent, while valid pairs in the same
sequence still apply; malformed pairs are ignored without disturbing the reply framing; a sequence over 32768 characters is dropped whole; a sequence with no query is silent; and the App has no
per-attribute color resources, so a stored special color is stored, queried and reset but **never render-applied** — rxvt's OSC 106 color-mode toggles are not modeled either. A render-snapshot test
pins the non-application rather than assuming it.

## What the effect contract decides

The oracle is written from rxvt's special-color text, sharing no code with the parser. Every exchange is a fenced request of at most 128 bytes whose raw replies (or their explicit absence) are retained
byte for byte, and every group must leave the complete screen, cursor, rendition, margins, modes and active screen unchanged. The measured groups: the baseline readback of all five slots (the
restoration oracle), the exact pinned request, a five-slot set read back per slot, set-then-query, the numeric encodings, BEL-terminated requests, two sequences in one request, per-selector and bare
OSC 105 resets and restoration to the measured defaults; the cross-service set/read pairs that pin the resource separation; and the policy groups above.

## Not claimed

No rendering, painted-pixel or theme-application claim for the stored special colors; no rxvt color-mode (OSC 106) support; no selector names; no OSC 105 with selectors outside 0-4; no remote/mux,
Windows/Linux or common-harness claim; no stock score beyond this run's recorded counts, and the frozen stock baseline keeps its original grades.

## Identities

App version 0.14.0, Release sidecar from `dc930315fa4bc7133d676ddc577b609d9c4f528e` (including the xterm palette-stack isolation fix; the selected source snapshot pins the measured script bytes independently). Owned run `5f5a5946b82d34456e8167ec967d1a70`,
measured 2026-11-17, macOS, Node v24.22.2. Source policy `readonly-owned-source-graph-v64` (427 files), receipt schema 71, query policy `owned-exact-query-paired-raw-state-v6`. The build is not
binary-to-source attestation.

## Evidence and rederivation

The retained [archive](2026-11-17-osc5-special-colors-stack-isolation/owned-run.zip) ([manifest](2026-11-17-osc5-special-colors-stack-isolation/manifest.json)), [summary](2026-11-17-osc5-special-colors-stack-isolation/summary.json) and
[report](2026-11-17-osc5-special-colors-stack-isolation/report.json) rederive through:

```sh
node --test scripts/terminfo/osc5-publication.test.mjs
node scripts/terminfo-inventory.mjs --check
```

The [first run](2026-11-17-osc5-special-colors/owned-run.zip), its summary, report and manifest remain unchanged: owned ID `1eda9658848990fce76b888d41b7bb4f`, stock ID
`fd5b6501307a54ce3dddd81b1bbab3c6`, App revision `9c922a4a3857fb109ba1ef7978a35ae013a8e63f`, source policy v63 and receipt schema 70. The [first reviewed run](2026-11-17-osc5-special-colors-reviewed/owned-run.zip)
remains unchanged too: owned ID `76c5a6a5341157e4f7683102cd91b9b1`, stock ID `e9a88a4341e1596cd2641c459575ed9e`, revision `e52b99c9ac2624bb071d8079ef2195a183ce4fa4`, policy v64/schema 71.
All three full reports and receipts independently rederive through the current trusted validators; only the newest run is registered in the live inventory. No run claims binary-to-source attestation.

The current App includes all review fixes: special-color-only updates preserve row revisions; set-direction isolation permits only the exact setup mutation; finish clears OSC 5 overrides; and an earlier
special-color baseline survives a later legacy-baseline failure. Xterm palette pops now retain live special colors while restoring dynamic colors and the palette. Separate owning VT regressions cover
stack-top and numbered slots, state transfer, reset/null slots and unchanged render revisions; kitty full-frame save/restore remains independent. The fresh run reconfirms this batch's existing
OSC 5 effect groups on that corrected App; it adds no new desktop xterm-stack criterion.

Pinned replay also preserves the upstream helper's property order for inconclusive observations (`outcome`, `reason`, `evidence`). Real-bundle regressions use the production serialized comparator
on both paths and derive complete reports for silent and malformed replies. The reply-framing cleanup and removal of an unused synthetic seed change no fixture bytes or oracle. The recorded App/scripts
revision identifies the measured inputs, rather than the later publication commit.

One [refused startup](2026-11-17-osc5-special-colors-stack-isolation/refused-render-timer.zip) is retained and digest-bound in the manifest. The App aborted before collection when Avalonia's native
render timer returned `-6661`; its journal is empty and it carries no grade or receipt. After verified cleanup and a display-wake preflight under the desktop lease, the retry completed. The publication
test verifies both the refusal and the successful run; no failure is promoted to an unavailable or supported outcome.
