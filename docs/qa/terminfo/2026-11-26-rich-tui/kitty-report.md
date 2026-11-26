# Rich TUI Kitty keyboard input

XTEST delivered X11 key presses and releases to a disposable Ntilde App on an isolated Linux X11 display. A raw PTY child captured the input produced by the App.

| Flag | Mask | X11 action | Child bytes | Owned verdict |
| --- | ---: | --- | --- | --- |
| `disambiguate` | 1 | ctrl+i | `CSI 105;5u` | supported |
| `report-events` | 3 | escape-hold | `CSI 27uCSI 27;1:2uCSI 27;1:2uCSI 27;1:2uCSI 27;1:2uCSI 27;1:2uCSI 27;1:2uCSI 27;1:3u` | supported |
| `report-alternate` | 5 | ctrl+shift+a | `CSI 97:65;6u` | supported |
| `report-all-keys` | 9 | a | `CSI 97u` | supported |
| `report-text` | 25 | a | `CSI 97;1;97u` | supported |

The flag-off controls delivered plain a as `61` and Ctrl+I as `09`. Holding Escape produced one press, 6 X11 autorepeats, and one release. The alternate-key chord used an XKB mapping with a/A on the same physical key. The associated-text sample committed a single ASCII scalar. An Escape hold without an observed repeat leaves the event-reporting result inconclusive.

Each flag was pushed, queried, exercised, popped, and queried again. The report JSON retains the exact child bytes in hex and the mode replies. The archive also retains the raw chunks and mode-specific keyboard delimiter, native key transitions and ownership proof. The five pinned 4.0.0 probes test flag negotiation. The routed-input observations grade these separate App effects.

Run `280c46035958c01f0e6f1317b2733c6c` used Ntilde `0.14.0` on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop), XTEST 2.2, and Node v24.22.2. The App source is `caea956de1f8b391892f61f89ba6fbf6c462cc77` and the measured collector is `fc73fe7ae8ab701f36aff06a8f1fdd9a8907eec5`. The Xvfb and App processes, private socket, termios state, and lease cleanup were verified. The fixture covers an ordinary local tab and the tested keys on its Linux X11 layout.

Rederive the report with `node scripts/terminfo/rich-x11-kitty-results.mjs --check`. [Owned archive](kitty-owned-run.zip) · [Manifest](kitty-manifest.json) · [Case list and evidence map](report.md)

For a fresh Linux arm64 run, install Node 24, Python 3, Xvfb, libXtst, libX11 and libXRes. Build the App at the recorded source revision.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde 0.14.0 --rich-kitty-keyboard
~~~
