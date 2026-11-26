# Rich TUI Kitty graphics transmit and display

The two cases already show as supported in the [frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json). This batch adds exact-fixture observations from one accepted run in a disposable ordinary local Ntilde tab.

## Pinned reply evidence

| Pinned terminfo.dev 4.0.0 case | Terminal callback | Termless callback replay |
| --- | --- | --- |
| `extensions.kitty-graphics.transmit` | supported | supported |
| `extensions.kitty-graphics.display` | supported | supported |

Each path sent its own direct `a=t,f=24,s=1,v=1,t=d,I=…;/wAA` upload of one red RGB pixel. The child received `i=1,I=394287356;OK` for transmission and `i=2,I=1801578268;OK` for the display path. Display then sent `a=p,i=2,c=2,r=1,C=1` and received `i=2;OK`. Each path sent its pinned silent deletion. Both unchanged callbacks were replayed against the retained child PTY bytes. The termless callback used the same retained PTY trace.

## Owned pixel evidence

| Fixture | Offscreen App render observations |
| --- | --- |
| `extensions.kitty-graphics.transmit` | Upload left the captured pane identical to the bound blank-image frame. Deletion left it unchanged. |
| `extensions.kitty-graphics.display` | Upload left the pane unchanged. Placement painted an 18 × 18 red square (324 changed pixels) inside the requested 2 × 1 cell envelope. No pixel outside that envelope changed. Deletion returned the pane to the bound frame. |

Each capture retained two identical PNGs with matching screen and render observations. The placed bitmap was 1 × 1 and the cursor stayed at the anchor. These PNGs show the App’s controlled offscreen renderer. Native desktop appearance is outside this measurement.

## Retained lifecycle rederivation

Before collecting the exact fixture, the [static-image publication](../2026-11-16-static-images-qualification.md) rederived from its original archive. Both Kitty carrier cases passed. The 16 × 16 RGBA numbered lifecycle covered upload, placement, soft deletion, redisplay, hard deletion and a correlated missing-image reply. Number reuse covered newest-number resolution, deletion and placement by the older allocated ID. Those are separate fixtures and historical observations.

## Reproduce

The [owned archive](owned-run.zip) contains the original PTY captures, command journal, PNG pairs, profile, source snapshot and cleanup proof. [Machine-readable results](report.json) list pinned replies separately from pixel measurements. The accepted run was `a83ff5f0a70531e98b66522d26e2e72e` on macOS 27.0.1 (26A434), arm64 with a 41 × 140 grid. App revision `eb0abcdae2da73b55a6a2905be11acd316d66121`, collector revision `dae4f1f1763a7a324eac00f8d891cdd4d2b6c199`. Cleanup restored the pane and PTY mode, removed the fixture and released the desktop lease.

An initial attempt (`cf9c716879092a56b3e2b040a486a285`) captured every stage and cleaned up. Its receipt failed because the verifier passed the focused scope’s stage name to the shared PNG verifier. That attempt is retained locally. The accepted archive comes from one run after that correction.

Build the App with `scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`. From the measured collector revision, run `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+eb0abcdae2da73b55a6a2905be11acd316d66121 --scope kitty-graphics`.

Run `node scripts/terminfo/kitty-graphics-publication.mjs --check` to rederive the accepted result and replay the digest-pinned callbacks. This publication adds no inventory claims or stock score changes.
