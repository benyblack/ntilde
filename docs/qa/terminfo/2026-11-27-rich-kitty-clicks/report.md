# Kitty shell prompt click events

The owned App effect for `input.kitty-click-events` is **supported**. The pinned `terminfo.dev@4.0.0` bundle has no runnable probe for this case. Its stock grade is absent.

Kitty’s shell integration specification chooses the primary anchor before the secondary prompt and the secondary anchor from its marked row onward. In Kitty’s mouse encoder, the zero-based relative row is adjusted before the one-based SGR encoding. A primary-button click produces a single left press with a final uppercase M. [Shell integration specification](https://sw.kovidgoyal.net/kitty/shell-integration/#notes-for-shell-developers) · [Kitty encoder](https://github.com/kovidgoyal/kitty/blob/a82dac6a03aae6c48fc84d7c759caf12c0c3dc25/kitty/mouse.c#L82-L112) · [Kitty prompt click dispatch](https://github.com/kovidgoyal/kitty/blob/a82dac6a03aae6c48fc84d7c759caf12c0c3dc25/kitty/mouse.c#L1176-L1192)

The fixture wrote OSC 133 A (primary), A;k=s (secondary), and C (submission) from a raw child connected to the App through a direct PTY. It verified marker text and cursor coordinates with the private read-only pane connection and cursor-position replies. XTEST delivered real X11 pointer moves, button presses, and button releases on the isolated display. Each target pixel was first calibrated with paired ordinary mode 1000/1006 press and release reports. After disabling application mouse tracking and verifying mode readback, a correlated keyboard delimiter followed every pointer action and closed its child input capture.

The one-based viewport anchors were primary row 13 and secondary row 20. The live editor cursor was on row 26. The click coordinates below use one-based viewport cells and X11 screen pixels.

| Fixture state | Position | Cell (column, row) | Screen pixel (x, y) | Child frame |
| --- | --- | --- | --- | --- |
| disabled | secondary | 52, 20 | 418, 418 | silent |
| absolute | primary | 52, 14 | 418, 306 | `CSI <0;52;14M` |
| absolute | secondary | 52, 20 | 418, 418 | `CSI <0;52;20M` |
| absolute | continuation | 52, 26 | 418, 522 | `CSI <0;52;26M` |
| relative | above | 52, 9 | 418, 202 | silent |
| relative | primary | 52, 14 | 418, 306 | `CSI <0;52;2M` |
| relative | secondary | 52, 20 | 418, 418 | `CSI <0;52;1M` |
| relative | continuation | 52, 26 | 418, 522 | `CSI <0;52;7M` |
| relative | below | 52, 31 | 418, 634 | silent |
| new-prompt | secondary | 52, 20 | 418, 418 | silent |
| after-submit | secondary | 52, 20 | 418, 418 | silent |
| reset | secondary | 52, 20 | 418, 418 | silent |

The target click frames arrived only after pointer release. The capture retains the three action windows, exact child chunks and delimiters, test marker output bytes, CPR replies, pane snapshots, click coordinates, and process identities. A plain primary A marker cleared the old opt-in. RIS repainted the same fixture text and cursor without prompt markers. The child restored and read back its termios attributes, exited, and the collector reaped the App and Xvfb and verified the socket and fixture were gone. Both the Linux collection lease and its enclosing host desktop lease were released.

Run `6e9be450e8dd7df0354909f3f43e14de` used Ntilde `0.14.0+94ed40c8ad167b03dc705b9a84ef05911c3c5f12` in Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop) with XTEST 2.2. The App was published from the source archive of `94ed40c8ad167b03dc705b9a84ef05911c3c5f12`. The measured collector revision was `669a96426e55653bdce753655a46e14f09963442`. The fixture exercised one direct-local App pane with a fake shell prompt. Scope ends at Linux X11 child PTY input.

Re-derive with `node scripts/terminfo/rich-x11-kitty-clicks-results.mjs --check`. [Raw owned run](owned-run.zip) · [Measurement manifest](manifest.json) · [Exact child bytes and controls](report.json)

To collect on Linux arm64, use Node 24, Python 3, Xvfb, libX11, libXtst, and libXRes. Build the App at the recorded App revision and collect at the recorded collector revision.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -p:SourceRevisionId=94ed40c8ad167b03dc705b9a84ef05911c3c5f12 -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde 0.14.0+94ed40c8ad167b03dc705b9a84ef05911c3c5f12 --rich-kitty-clicks
node scripts/terminfo/rich-x11-kitty-clicks-results.mjs --verify-run /absolute/path/to/modern-x11-run
~~~
