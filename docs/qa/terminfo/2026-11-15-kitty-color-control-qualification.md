# Kitty color-control qualification: OSC 21 (2026-11-15)

Qualifies one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `extensions.osc21-kitty-color`, whose exact request is `OSC 21;foreground=? ST`. Like the
[SGR-stack batch](2026-11-15-xterm-sgr-stack-qualification.md), **this batch adds production code**: main did not answer the request (the [exact query audit](exact-query-audit.md) recorded matched silence),
so a qualification had nothing to measure. The VT now implements a documented subset of kitty's color-control protocol, and the batch measures it. The private-mode save/restore batch ran in parallel, owns different cases and merged first; this batch was restacked on it (source policy v60, receipt
schema 66).

## What the pinned probe can say

The pinned probe is a **query-only** probe, unlike the always-inconclusive stack probes. It sends the exact request and is the one case of this batch whose verdict the unchanged pinned code can decide:

- termless path: `feedCapture(OSC 21;foreground=? ST)` over `kittyForegroundResult`; terminal path: `queryWithSentinelOutcome` with the pattern `ESC ] 21 ; … (BEL | ST)`, and `unansweredQuery` when nothing matches.
- **supported** when the reply carries exactly one `foreground=` field whose value is empty (an explicitly undefined color) or `rgb:h/h/h` with one to four hex digits per channel; **inconclusive**
  (`no-response`) when no OSC 21 frame arrives; **inconclusive** with the note "replied without one verifiable foreground value" for any other value. It can never return unsupported, and it does not
  test setting a color or any rendering.

Both pinned paths are replayed byte-exactly over the journaled raw reply of the case's own fenced exchange. The pinned verdict is also the stock collector's grade in the same owned run and is
corroborated by the exact-query witness `q07.extensions.osc21-kitty-color` under query policy v5.

## Result

| Case | Pinned termless / terminal paths | Stock grade (this run) | Exact-query witness | Effect contract (`ntilde-kitty-color-control-effect-v1`) |
|---|---|---|---|---|
| `extensions.osc21-kitty-color` | **supported** / **supported** | **supported** (was inconclusive: no reply) | matched a framed `rgb:rr/gg/bb` reply | **supported** (6 controls and 31 protocol groups matched; 12 policy groups, 10 matched Ntilde's choices and 2 record divergences from kitty's text) |

The stock collector in the same owned run (`9a77d0d0193af108f347421092087cbf`) grades 93 supported / 1 unsupported / 161 inconclusive (`suiteComplete=false`). On main (after the saved-mode batch) the
counts are 92 / 1 / 162, so exactly one stock grade moves: `extensions.osc21-kitty-color`. The frozen stock baseline keeps its original grades. No Core point is added. Inventory: one more ID with both a
supported pinned verdict and a supported effect contract.

## Production

`OSC 21 ; key=value ; … ST` queries, sets and resets colors by key name over the runtime colors OSC 4, 10, 11, 12, 17 and 19 already keep ([parser](../../../src/Ntilde.VT/AnsiParser.KittyColors.cs), [unit
tests](../../../tests/Ntilde.VT.Tests/KittyColorTests.cs)). It follows kitty's [protocol text](https://sw.kovidgoyal.net/kitty/color-stack/):

- keys: `foreground`, `background`, `selection_background`, `selection_foreground`, `cursor` and the decimal palette indices 0-255; a value `?` queries, a bare key resets the override to the host default,
  a numeric color sets it (the encodings the OSC 10 parser accepts);
- the reply is one OSC 21, always ended by ST (even for a BEL request), with a field per query in request order: `key=rgb:rr/gg/bb` with eight-bit channels, an empty value for a color with no concrete value
  (an unset derived selection color), and `unknown=<Base64 of the key, no padding>` for any key outside the modeled set (the key is encoded so the reply cannot be made to carry arbitrary text);
- the state is shared with the legacy sequences and the kitty color stack (OSC 30001/30101).

Choices the text does not make, or where Ntilde differs, recorded as non-deciding policy: color names, `@alpha` suffixes and the empty "dynamic" value are not modeled (the color in force stays); `cursor_text`,
`visual_bell` and `transparent_background_color1..7` are kitty keys this terminal has no color for and answer `unknown`; keys are case-sensitive; a sequence over 4096 characters is dropped whole and only the first 256
fields are processed; a sequence with no query is silent. Two spellings **differ from kitty's text** and are recorded as policy divergences: `#RGB` is scaled like the OSC 10 parser (`#3a7` is `33/aa/77`;
kitty's text keeps the most significant bits, `30/a0/70`), and an out-of-range `rgbi` intensity is refused rather than clipped (`rgbi:2/0/0` keeps the color in force). The text does not say how a scaled channel that
is not a whole byte is rounded (Ntilde truncates, like OSC 10); every deciding group therefore uses values that scale exactly, and a test pins that. A local Codex review of the production commit found nothing; a
second review of the layer found this rounding mismatch between the oracle and the shared parser.

## What the effect contract decides

The oracle is written from kitty's text, sharing no code with the parser. Every exchange is a fenced request list of at most 64 bytes whose raw replies (or their explicit absence) are retained byte for byte
before the primary-DA barrier. Colors are first set through **other** sequences (OSC 10, 11, 12, 17, 19 and 4: distinguishable known values, so a reply cannot be satisfied by a default), then read back through
OSC 21; colors OSC 21 sets are read back through the legacy OSC replies too (sixteen-bit, one shared state). `protocol` groups decide a mismatch (unsupported), `control` groups prove each setup took effect
(a failed control is inconclusive, never a verdict) and `policy` groups record a choice above (never deciding). Demands for slots left untouched are baseline-relative: the baseline read records the host
default replies for the foreground, background, cursor, both selection colors and palette entries 5 and 200 (the selection colors may legitimately be empty), and later reads must equal them. Every exchange
must leave the screen, cursor position and visibility, margins, wrap/insert modes and active screen unchanged.

- **Controls (6).** the three baseline reads; color set A written through the legacy sequences and read back through OSC 10/11/12 (and the selection colors and palette entries through OSC 17/19 and 4); the selection
  colors set through OSC 21 and read back.
- **Protocol (31).** the exact pinned request; each key alone; five keys in one request answered in request order; a BEL-terminated request answered with ST; two sequences answered by two replies; set-then-query in
  one sequence; the OSC 21 value read back through OSC 10; six numeric encodings (`#RRGGBB`, `rgb:h/h/h`, `rgb:hh/hh/hh`, `rgb:hhhh/hhhh/hhhh`, `rgbi`, `#RRRGGGBBB`); palette set and read-back through OSC 4;
  selection read-back through OSC 17/19; bare-key reset of the foreground, background and a palette entry; the legacy resets (OSC 104/110/111/112/117/119) restoring the baseline; and four unknown-key groups
  (`nonsense`, index 256, padding, order).
- **Policy (12).** interleaved unknown key; `cursor_text`/`visual_bell`; `transparent_background_color1`; key case; `#RGB` and out-of-range `rgbi` (the two divergences); empty value; color name; alpha suffix;
  a set-only sequence; the 4096-character cap; the 256-field cap.

All control and protocol groups matched and every exchange left the state unchanged. The exact bytes of every group were replayed through the C# VT before any desktop run, the contract's own tests include
hand-written demands as a second oracle, and tampered, stale, cross-case and corrupted retained data are refused by the receipt tests.

## Exact-query witness (query policy v5)

`q07.extensions.osc21-kitty-color` expected silence under query policies v1-v4 and keeps that expectation for every archived run. The new policy v5 (marker `colorReport`, which requires the v4 device marker)
expects one complete `OSC 21;foreground=rgb:rr/gg/bb ST` reply with eight-bit lowercase channels. The value is the host theme's foreground (the suite begins with RIS, which clears every override), which the
witness model cannot name, so the witness decides the exact framing and shape through a pattern entry and records the pattern; the exact values are the effect contract's job. The run's other nine
witness mismatches are not this case's: `xtwinops-20/21` (the run turns the title reports on), the Sixel geometry report, the DECRQSS `m` and ` q` reports and four XTGETTCAP answers, all recorded by earlier
batches' audits.

## Not claimed

Raw replies and stored state only: no rendering, painted-pixel or host-presentation claim, no setting of dynamic or named colors, no alpha or opacity (`transparent_background_color*`, `visual_bell`,
`cursor_text`), no color-name database, and no claim about the kitty `OSC 30001/30101` stack (a different, previously supported spelling). Remote/mux delivery, Windows/Linux and common-harness runs are not covered.

## Identities

App 0.14.0 (operator supplied) built from `d82374aa8e22e7b4e1115344908e3c2bb975c7c6` (arm64 Release; the last commit that changed the App sources; executable SHA-256
`f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256 `a6a1db21fd76a0cde3d320534fe09a260d743320fefe7e8bc9ad9fdf1a8371c3`); measured scripts
`ec6c5b99431af6d8fcee5083f196a585e66f2d13`; source policy `readonly-owned-source-graph-v60` (401 files; manifest SHA-256 `677391b6832d0d77fff8126908c326700d27af225078a7584e60056047e9de11`); query policy
`owned-exact-query-paired-raw-state-v5`; initial settings SHA-256 `08d42282e15a297de1e484b6b92658a98046a61333a1ca3ebbbb9ae025019091`; receipt schema 66; Node v24.22.2; owned run
`55f0ba462cd3cc6fbc315a0e5fad5126`; collected 2026-11-16T09:11Z on macOS. The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the
retained bytes. The measured scripts are the restacked branch head (after the saved-mode batch), so the run postdates review-driven fixes to the contract module and the production handler.

## Retained evidence

[2026-11-15-kitty-color-control/](2026-11-15-kitty-color-control/): `owned-run.zip` (the complete owned run), `report.json` (the projected result), `summary.json` (identities, stock counts, receipt) and
`manifest.json`. `node --test scripts/terminfo/kcl-publication.test.mjs` re-derives the receipt and the report from the retained bytes. No completed run was superseded: six earlier collections on this head aborted before finishing a run (stray keyboard input reaching the disposable App, a 5-minute limit under heavy machine load, and a defect in an intermediate fix of the pinned frame pattern that the collector itself refused) and none is published; the first complete run is the published one.
