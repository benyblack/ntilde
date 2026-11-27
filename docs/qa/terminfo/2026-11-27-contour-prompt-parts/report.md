# Contour-style OSC 133 prompt parts

The owned prompt-part effect for `extensions.osc133-p` is **supported** on one direct-local macOS pane. Both terminfo.dev 4.0.0 paths returned **inconclusive · insufficient-evidence · consumed**. The termless path fed `OSC 133;P;Cwd=/tmp BEL X` and read `X` from cell (0,0). The terminal path homed the cursor, erased row 1, sent the same bytes, and received `CSI 1;2R` for its cursor query. The original probe functions and verdict helper were replayed on those reads. The historical stock grade is inconclusive. [Exact bytes and results](report.json) · [#985 policy audit](../2026-11-17-osc133-p-policy.md)

## Boundary and lifecycle

Contour specifies `P` for another part of the current prompt and `k=r` for the start of a right prompt. Ntilde records bare P as kind `i` and accepts the explicit kinds `i`, `c`, `s`, and `r`. Pane metadata reports zero-based viewport row and column. Ntilde keeps one start per kind per visible row. Emitting that kind again moves it, and a marker at an occupied cell replaces the previous marker there. A live right boundary also limits grid command reads. The ordinary command and output anchors retain their existing lifecycle. [Contour specification](https://contour-terminal.org/vt-extensions/osc-133-shell-integration/#p-prompt-part-start)

| Fenced fixture stage | Independent App readback |
| --- | --- |
| Primary prompt and input | Bare P at (0,0), B at (0,2), accepting input, no output anchor, no current command |
| Right marker, before painting | `r` at (0,21) alongside the original `i`. Both screen reads match the pre-marker screen, including cursor and cells |
| Repaint | The right marker moves to (0,16). The old `RHS` cells are still visible at column 21 |
| Continuation and Kitty control | `P;k=c` adds (1,3). Kitty’s `A;k=s` on the next row leaves the three P boundaries intact |
| Erase, re-mark, overwrite | Erasing the marked cell drops `r`. A fresh `r` reappears at (0,16). Typing across that cell drops it again |
| Submission | C closes input, sets the output anchor and reports the command `cmd`. Both remaining boundaries disappear. A subsequent orphan P produces none |
| New prompt and reset | A new A discards the preceding right boundary. A fresh `P;k=s` reads back at (2,10). RIS and an orphan right marker yield an empty part list |

Through the right, redraw and continuation stages, the pane stayed in its original input block with the same full B anchor, no output anchor, and no command in flight. Parser regressions count A/B/C/D callbacks independently. OSC 7 prepared a run-specific directory and the pinned fixture preserved it. A distinct OSC 633 P/Cwd write changed it through the existing directory path. [All paired observations, PTY fences and receipt](owned-run.zip)

The part belongs to the live main viewport. A primary A, C or D, main-screen clear, alternate-screen entry or hard reset retires the cycle. A size change or soft reset discards the recorded coordinates until fresh markers arrive. Scrollback and exported state carry no parts. Unit tests also exercise grid reads and width changes. The live experiment held the grid at 41 rows by 140 columns. The separate Kitty click measurement covers pointer delivery. [Kitty prompt clicks](../2026-11-27-rich-kitty-clicks/report.md)

## Reproduction

Run `68c6e323b602b830e5a7461dd7f3146b` used App `c0c1f2ad118ea9b0d3a495760e27c04ff866744d`, collector `c0c1f2ad118ea9b0d3a495760e27c04ff866744d`, and Node 24.22.3. The archive retains the fixture writes, replies, paired reads, captured source graph, initial settings, process and TTY ownership, cleanup proof and four desktop-lease events. The child restored its kernel TTY mode. Final reads verified the initial blank grid, empty parts and anchors, and prepared cwd. The App build label was operator-supplied. Binary digests are recorded in the report. A source-graph selection error on the initial launch command was fixed before the first desktop lease or pane. [Manifest](manifest.json) · [Measurement and binary digests](report.json)

The first measured build also passed. After review of the grid reader and effect grader, the rebuilt App and updated collector repeated the full fixture under a new lease. [First run and receipt](https://github.com/benyblack/ntilde/tree/c749c6c6617d0057d4d1ab6f16a6f3028300baf7/docs/qa/terminfo/2026-11-27-contour-prompt-parts)

Reissue the receipt, execute both pinned callbacks offline and check the inventory with Node 24 or later.

~~~sh
node --test scripts/terminfo/prompt-parts-publication.test.mjs
node scripts/terminfo-inventory.mjs --check --rederive
~~~

For a fresh measurement, publish the recorded App revision to a private sidecar and run the collector at its recorded revision. The runner acquires the shared desktop lock.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false \
  -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true \
  -p:SourceRevisionId=c0c1f2ad118ea9b0d3a495760e27c04ff866744d -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde \
  --app-version 0.14.0+c0c1f2ad118ea9b0d3a495760e27c04ff866744d --scope prompt-parts
~~~
