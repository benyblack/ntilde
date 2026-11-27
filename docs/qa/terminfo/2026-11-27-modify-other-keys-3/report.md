# modifyOtherKeys level 3

**The owned input effect is supported.** The child read back level 3 and received exactly one report for each paired ordinary key. Both callbacks of the pinned `terminfo.dev@4.0.0` probe for `input.modify-other-keys-3` returned **inconclusive**. Its termless callback reads only the shared capability declaration, which the child answered negatively via XTGETTCAP. Its terminal callback performs no I/O. The historical stock grade is inconclusive. This adds one owned inventory ID and claim. Frozen Core, Modern and public comparison scores are unchanged. [Complete results](report.json)

## Child PTY bytes

Each row used the same logical key, physical position, modifiers and correlated backend text at both levels. The child verified the selected level through XTQMODKEYS before dispatch. Every sample ends at two fresh DECRPM ordering replies, which the table omits.

| Key and paired text | Level 2, hex | Level 3, hex | Level 3, decoded |
| --- | --- | --- | --- |
| A → `a` | `61` | `1b 5b 32 37 3b 31 3b 39 37 7e` | `CSI 27;1;97~` |
| 2 → `2` | `32` | `1b 5b 32 37 3b 31 3b 35 30 7e` | `CSI 27;1;50~` |
| Semicolon → `;` | `3b` | `1b 5b 32 37 3b 31 3b 35 39 7e` | `CSI 27;1;59~` |
| Space → space | `20` | `1b 5b 32 37 3b 31 3b 33 32 7e` | `CSI 27;1;32~` |

At level 2, the App left the keydown unhandled and delivered the text callback. At level 3, the App handled both events and the child captured a single frame. A keydown without backend text also delivered the full frame. Shift+A, Ctrl+A and Alt+A produced `CSI 27;2;65~`, `CSI 27;5;97~` and `CSI 27;3;97~`. With Kitty disambiguation active, Ctrl+A produced `CSI 97;5u`. Popping Kitty restored level-3 routing.

The 22 observations include the declaration, levels 0 and 1 delivering Ctrl+A as `01`, a standalone text commit delivering `37`, an empty fenced key-up, Return delivering `0d`, and transitions back to level 2 and reset to 0 delivering plain `61`. The runner verified the bound alternate screen throughout dispatch, the restored main screen and default input modes, the original kernel terminal attributes, process cleanup, and lease release. [Fixed-run archive and receipt](owned-run.zip)

## Contract and baseline

Xterm patch 412 selects eligible ordinary keys at level 3 in `ModifyOtherKeys`. Its default `modifyOtherKey` framing emits the function-key number 27, the modifier parameter, and the character code. An unmodified printable key uses modifier parameter 1. The expected byte strings were committed before the production change. [Literal oracle](../../../../scripts/terminfo/modify-other-keys-3-contracts.mjs) · [xterm manual](https://invisible-island.net/xterm/manpage/xterm.html#VT100-Widget-Resources:modifyOtherKeys) · [control sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html) · [xterm input.c](https://github.com/ThomasDickey/xterm-snapshots/blob/5c75ead9a75d4b2e666ca06fa94503b5766ed93f/input.c#L641-L784)

Level 3 adds plain and Shift-only printable keys. It reports the backend’s printable Unicode scalar when supplied. A key without a symbol uses the existing US logical-key table with Shift applied. The generated PTY events exercise that fallback. App routing regressions also cover Caps Lock, Shift with Caps Lock, non-Latin and supplementary characters, and multi-scalar composition. Named controls keep their level-2 rules, and physical keypad, cursor and function keys keep their own routes. Ctrl+Alt and Meta keep the existing layout and shortcut handling. Mux's existing protocol bit advertises levels 1/2, so shared sessions retain that ceiling. Standalone text, IME-processed keys and distinct IME commits are covered by App routing tests. The live evidence covers generated Avalonia input on macOS arm64 with one direct-local PTY. Physical layout translation, a native IME session and other transports require their own observations.

The #1002 policy regression passed on main for starting levels 0, 1 and 2 on both screens. In the separate four-row baseline run, requesting level 3 over level 2 still returned `CSI >4;2m`. Paired A delivered `61` and Ctrl+A delivered `CSI 27;5;97~`. [Baseline archive and receipt](baseline/owned-run.zip) · [Original policy audit](../2026-11-17-modify-other-keys-3-policy.md) · [Frozen levels 1/2 qualification](../2026-11-12-modify-other-keys-qualification.md)

| Measurement | App source | Collector source | Run |
| --- | --- | --- | --- |
| Ignored request | `6d16bd84bdac25cd8b51a0fa7a4554d74702401d` | `d33840549b1684e1d9278f60df670f9daf67de6d` | `f3106ab6a428a316b68f8ce0b0f38cd7` |
| Level 3 | `d569b5180b86ec8369b6668f4d8d7b3cadc68c45` | `d569b5180b86ec8369b6668f4d8d7b3cadc68c45` | `78b3dbd7ec1be90575890efad4f1fb07` |

All three earlier implementation runs also passed the 22 observations. The table uses the complete repeat after separating printable and named key handling. [First implementation receipt](https://github.com/benyblack/ntilde/blob/95e23798a9f36e455c67fd512d0de1aa7888a1d0/docs/qa/terminfo/2026-11-27-modify-other-keys-3/owned-run.zip) · [Mixed-pane revision receipt](https://github.com/benyblack/ntilde/blob/57d04d2815702bd4270c1e8bf559e6fe327a1a46/docs/qa/terminfo/2026-11-27-modify-other-keys-3/owned-run.zip)

An earlier attempt on the backend-symbol revision `4a39ecaa`, `7d940ab92c5f2beab8e89316c8b5dfe8`, captured `CSI B` on both sides of its third ordering fence. The trailing input invalidated the capture. The runner stopped, verified cleanup and released the lease. A fresh pane then completed the entire plan. [Rejected raw capture](rejected-input.json) · [Backend-symbol revision receipt](https://github.com/benyblack/ntilde/blob/61ba9f3853eabbeb552821ced031c0baa39d7afb/docs/qa/terminfo/2026-11-27-modify-other-keys-3/owned-run.zip)

Both runs used Node 24.22.3, a 140 × 40 pane, source policy `readonly-owned-source-graph-modify-other-keys-3-v1` and receipt schema 1. The App revision is build provenance. The archives bind the executable, managed App assembly and captured collector sources by SHA-256. [Fixed manifest](manifest.json) · [Baseline manifest](baseline/manifest.json)

## Reproduction

Reissue both receipts and replay the original pinned callbacks offline using Node 24 and Python 3.

~~~sh
node scripts/terminfo/modify-other-keys-3-publication.mjs --check
node --test scripts/terminfo/modify-other-keys-3-publication.test.mjs scripts/terminfo/modify-other-keys-publication.test.mjs
~~~

For a live repeat, build from the desired App revision above into a fresh directory under `~/.ntilde-qa-sidecars`. Run the launcher from the corresponding collector revision. Set `APP_REVISION` to the full App SHA and `SIDECAR` to the absolute private directory. Set `SCOPE` to `modify-other-keys-3-baseline` for the ignored-request reproduction or `modify-other-keys-3` for the implementation. The launcher acquires the shared desktop lock.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false \
  -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o "$SIDECAR"
node scripts/terminfo-owned.mjs --executable "$SIDECAR/Ntilde" --app-version "0.14.0+$APP_REVISION" --scope "$SCOPE"
~~~
