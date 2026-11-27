# Rich TUI OSC 5522 clipboard qualification

| Measurement | Verdict |
| --- | --- |
| Pinned terminfo.dev 4.0.0 terminal callback | **supported** |
| Owned OSC 5522 paste on the default X11 clipboard | **supported** |
| Historical stock run `02be5c606e0e7d79ab8e11f142d21ae0` | **unsupported** |

The direct local pane received the exact `CSI ? 5522 $ p` query through its child PTY. The DA1 sentinel followed it. Request `1b 5b 3f 35 35 32 32 24 70 1b 5b 63` returned `1b 5b 3f 35 35 32 32 3b 32 24 79 1b 5b 3f 36 32 3b 34 3b 32 32 63`. The unchanged terminal callback graded the complete DECRPM as recognized with mode reset. The pinned parser-context callback also replayed that same DECRPM. The two callbacks grade mode recognition.

The independent mode writes and queries recorded these replies.

| Phase | Child write | Child reply |
| --- | --- | --- |
| off-set | `1b 5b 3f 35 35 32 32 6c` | empty |
| off-query | `1b 5b 3f 35 35 32 32 24 70 1b 5b 63` | `1b 5b 3f 35 35 32 32 3b 32 24 79 1b 5b 3f 36 32 3b 34 3b 32 32 63` |
| on-set | `1b 5b 3f 35 35 32 32 68` | empty |
| on-query | `1b 5b 3f 35 35 32 32 24 70 1b 5b 63` | `1b 5b 3f 35 35 32 32 3b 31 24 79 1b 5b 3f 36 32 3b 34 3b 32 32 63` |
| off-reset | `1b 5b 3f 35 35 32 32 6c` | empty |
| off-again | `1b 5b 3f 35 35 32 32 24 70 1b 5b 63` | `1b 5b 3f 35 35 32 32 3b 32 24 79 1b 5b 3f 36 32 3b 34 3b 32 32 63` |

The prior macOS comparison replayed `1b 5b 3f 35 35 32 32 3b 30 24 79 1b 5b 3f 36 32 3b 34 3b 32 32 63` from `04019dbacf1c02aa7ccd85c39fcd2670ec2905eb` as unsupported and `1b 5b 3f 35 35 32 32 3b 32 24 79 1b 5b 3f 36 32 3b 34 3b 32 32 63` from `5aa7481b2d3187325378168f68c0548111322ac3` as supported. The current direct App pane reproduced the latter reply with native clipboard wiring and OSC 52 clipboard writes disabled. The OSC 5522 implementation opts in for App-owned panes with desktop clipboard service. The later historical stock run recorded an unverified Ntilde 0.12.0 identity and null configuration, so the comparison establishes the implementation transition while the exact configuration behind the stock result remains unknown. [Prior macOS comparison](../2026-11-01-clipboard.md) · [Historical stock baseline](../2026-11-09-stock-baseline.json).

A fresh GTK peer independently read the synthetic UTF-8 and HTML values from the default X11 CLIPBOARD. Ctrl+V delivered the raw text with mode 5522 off. With mode 5522 enabled, the entire fenced PTY window held one MIME notification for `text/plain` and `text/html`. The window contained zero ordinary paste bytes. The GTK fixture replaced and verified both live values before the token read, which still returned the two values captured at the gesture.

Reusing that credential within 15 seconds opened the App-owned Clipboard access dialog. The collector closed the dialog through its normal X11 window-close path. The child received `EPERM` with zero data frames. OSC 52 read returned an empty response. Existing App clipboard tests cover explicit allow-once and session-only grants, credential scope, and reset revocation. [Clipboard permission policy](../../clipboard-permissions.md) · [Kitty clipboard protocol](https://sw.kovidgoyal.net/kitty/clipboard/).

## Retained evidence and reproduction

App `0.14.0` came from `db411becbe33922691e29d78b0ab7dc443d26b14` with .NET SDK `10.0.403`. Collector `9670c3fad322c0f90d65217540ecebcb737d8146` ran with Node `v24.22.2` on Ubuntu 24.04 LTS arm64 (Docker Desktop). App PID 9951 owned local pane `e534f05e-7b86-4553-9c40-afd3bcc54a61` and child PID 10006 on `/dev/pts/0`. XRes verified the App and independent GTK clients on Xvfb PID 9948.

The archive retains the raw mode and OSC exchanges, XTEST events, both independent MIME readbacks, consent window ownership and focus restoration, App binary hashes and measured collector sources. The prepared two-MIME clipboard baseline and incoming modes were read back before the worker restored termios and exited. The App, eight GTK peers and Xvfb exited. The X11 socket and fixture tree were gone before the lease released at 2026-11-27T08:33:54.029Z. [Owned run](owned-run.zip) · [Manifest](manifest.json) · [Derived report](report.json).

[Earlier attempt `796553d53da0d7c5c79ff39dda75389b`](attempt-1.zip). The first verifier assumed the seal repeated the arm timestamp. The worker records that timestamp on the arm receipt. The initial query returned reset and cleanup completed.

[Earlier attempt `7a013ea3deec6477b1812c4c7a5b45da`](attempt-2.zip). Token reuse opened the real consent dialog and returned EPERM. The collector had incorrectly expected EACCES.

[Earlier attempt `1744c1f400db01d224fc9451b9de2fb5`](attempt-3.zip). The collector overwrote the retained mode-restoration exchange with the synthetic baseline values before replay.

[Earlier attempt `10b9dc2c0417978e0382d6c94c681a34`](attempt-4.zip). This completed run verified both effects and restored the fixture. The final collector tightened frame termination to reject any trailing ordinary paste bytes.

[Earlier attempt `415a1d98e8bb910104d7133427793f86`](attempt-5.zip). This completed run verified the verdicts and cleanup before the SonarCloud dispatch and X11 library cleanup changed the measured collector source.

Rederive offline with Node 24 and Python 3.

~~~sh
node scripts/terminfo/osc5522-clipboard-results.mjs --check
~~~

For a fresh Linux run, install Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes.

~~~sh
# Build from App revision db411becbe33922691e29d78b0ab7dc443d26b14.
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# Launch from collector revision 9670c3fad322c0f90d65217540ecebcb737d8146.
node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde 0.14.0 --osc5522-clipboard
~~~
