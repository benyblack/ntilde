# Contour-style OSC 133 prompt parts

The owned prompt-part effect for `extensions.osc133-p` is **supported** on one direct-local macOS pane. Both terminfo.dev 4.0.0 paths returned **inconclusive · insufficient-evidence · consumed**. The termless path fed `OSC 133;P;Cwd=/tmp BEL X` and read `X` from cell (0,0). The terminal path homed the cursor, erased row 1, sent the same bytes, and received `CSI 1;2R` for its cursor query. The original probe functions and verdict helper were replayed on those reads. The historical stock grade is inconclusive. [Exact bytes and results](report.json) · [#985 policy audit](../2026-11-17-osc133-p-policy.md)

## Boundary and lifecycle

Contour specifies `P` for another part of the current prompt and `k=r` for the start of a right prompt. Ntilde records bare P as kind `i` and accepts the explicit kinds `i`, `c`, `s`, and `r`. Pane metadata reports zero-based viewport row and column. Ntilde keeps one start per kind per visible row. Emitting that kind again moves it, and a marker at an occupied cell replaces the previous marker there. A live right boundary also limits grid command reads. The ordinary command and output anchors retain their existing lifecycle. [Contour specification](https://contour-terminal.org/vt-extensions/osc-133-shell-integration/#p-prompt-part-start)

| Fenced fixture stage | Independent App readback |
| --- | --- |
| Primary prompt and input | Bare P at (0,0), B at (0,2), accepting input, no output anchor, no current command |
| Right marker, before painting | `r` at (0,21) alongside the original `i`. Both screen reads match the pre-marker screen, including cursor and cells |
| Insert and delete | Inserting two cells before `RHS` moves both its text and `r` from column 21 to 23. Deleting those cells returns both to 21. The `i` boundary and B anchor stay fixed |
| Repaint | `RHS` starts at (0,21) when its first paint uses insert mode. After a width-three `O` is drawn at (0,16), a fresh `r` stays at (0,16) as `new` replaces that sized block. The old `RHS` text remains at column 21 |
| Continuation and Kitty control | `P;k=c` adds (1,3). Kitty’s `A;k=s` on the next row leaves the three P boundaries intact |
| Erase, re-mark, overwrite | Erasing the marked cell drops `r`. A fresh `r` reappears at (0,16). Typing across that cell drops it again |
| Empty right part | An unpainted `r` at (0,7) disappears when the cursor returns to input and types `abcd` across it. The primary and continuation remain, with the same B anchor |
| Submission | C closes input, sets the output anchor and reports the command `cmd`. Both remaining boundaries disappear. A subsequent orphan P produces none |
| New prompt and reset | A new A discards the preceding right boundary. A fresh `P;k=s` reads back at (2,10). A partial-width scroll overwrites that cell and clears its boundary. Re-marking restores it, then RIS and an orphan right marker yield an empty part list |

Selective display erase (`CSI ? 2 J`) spared the protected `R` and its right boundary at (0,13), while the unprotected primary part and neighboring `X` disappeared. The B anchor and input window survived. The VT regression sends ordinary ED 2 directly after selective ED and verifies that the remaining boundary disappears. In the pane, DECALN replaced the protected cell with `E` and removed its boundary. The subsequent finish stage verified the restored baseline.

Through the right, redraw and continuation stages, the pane stayed in its original input block with the same full B anchor, no output anchor, and no command in flight. Parser regressions count A/B/C/D callbacks independently. OSC 7 prepared a run-specific directory and the pinned fixture preserved it. A distinct OSC 633 P/Cwd write changed it through the existing directory path. [All paired observations, PTY fences and receipt](owned-run.zip)

The part belongs to the live main viewport. A primary A, C or D, main-screen clear, alternate-screen entry or hard reset retires the cycle. A size change or soft reset discards the recorded coordinates until fresh markers arrive. Scrollback and exported state carry no parts. Unit tests also exercise grid reads and width changes. The live experiment held the grid at 41 rows by 140 columns. The separate Kitty click measurement covers pointer delivery. [Kitty prompt clicks](../2026-11-27-rich-kitty-clicks/report.md)

## Reproduction

Run `004e30031135ab3e6c19d1515512e718` used App `55392bbf718d9c1f9526e1a52f0396ecb9b9000d`, collector `55392bbf718d9c1f9526e1a52f0396ecb9b9000d`, and Node 24.22.3. The archive retains the fixture writes, replies, paired reads, captured source graph, initial settings, process and TTY ownership, cleanup proof and four desktop-lease events. The child restored its kernel TTY mode. Final reads verified the initial blank grid, empty parts and anchors, and prepared cwd. The App build label was operator-supplied. Binary digests are recorded in the report. A source-graph selection error on the initial launch command was fixed before the first desktop lease or pane. [Manifest](manifest.json) · [Measurement and binary digests](report.json)

The first measured build also passed. After review of the grid reader and effect grader, the rebuilt App and updated collector repeated the full fixture under a new lease. Codex subsequently identified a column-edit defect. The next run exercised the ICH and DCH fix. A later run covered the unpainted right marker and the partial-width scroll. The following run added selective display erase. The final run also exercised initial paint in insert mode, sized-text replacement, and DECALN. [Selective erase measurement](https://github.com/benyblack/ntilde/tree/ad80d0bd012000672031d67dead2736ac5342498/docs/qa/terminfo/2026-11-27-contour-prompt-parts) · [Scroll and unpainted-marker measurement](https://github.com/benyblack/ntilde/tree/a2b646a645535cd82ea3a472312913d93ae5c1c2/docs/qa/terminfo/2026-11-27-contour-prompt-parts) · [Column-edit measurement](https://github.com/benyblack/ntilde/tree/40778d85f8fb43bbdcc33e59269aaa47146f9abc/docs/qa/terminfo/2026-11-27-contour-prompt-parts) · [First run](https://github.com/benyblack/ntilde/tree/c749c6c6617d0057d4d1ab6f16a6f3028300baf7/docs/qa/terminfo/2026-11-27-contour-prompt-parts) · [Reader and grader remeasurement](https://github.com/benyblack/ntilde/tree/a6c4b11dfbb5e008f5a10b81a390afd225810197/docs/qa/terminfo/2026-11-27-contour-prompt-parts)

Two initial reads on the selective-erase build failed the paired-state check before the first fixture stage. Each lease recorded verified cleanup. Neither attempt produced a verdict. The next collector retained rejected frames for diagnosis and repeated the same check. The paired reads agreed on each completed run. [Rejected starts and lease records](rejected-starts.json)

Reissue the receipt, execute both pinned callbacks offline and check the inventory with Node 24 or later.

~~~sh
node --test scripts/terminfo/prompt-parts-publication.test.mjs
node scripts/terminfo-inventory.mjs --check --rederive
~~~

For a fresh measurement, publish the recorded App revision to a private sidecar and run the collector at its recorded revision. The runner acquires the shared desktop lock.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false \
  -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true \
  -p:SourceRevisionId=55392bbf718d9c1f9526e1a52f0396ecb9b9000d -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde \
  --app-version 0.14.0+55392bbf718d9c1f9526e1a52f0396ecb9b9000d --scope prompt-parts
~~~
