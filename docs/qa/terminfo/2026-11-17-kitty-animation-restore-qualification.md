# Kitty animation restored after the App is terminated and started again, measured on the desktop (2026-11-17)

Issue [#1030](https://github.com/benyblack/ntilde/issues/1030), follow-up to [#1025](2026-11-17-kitty-animation-persistent-qualification.md) (a live persistent tab) and [#1017](kitty-image-lifecycle.md) (animation in persistent
sessions), parent [#770](https://github.com/benyblack/ntilde/issues/770). #1017 restores a persistent tab's animation from the daemon's snapshot; until now that was covered by deterministic tests only. This note measures it
on a real desktop with the owned animation machinery: the App is terminated with SIGTERM while the multiplexer daemon and the shell keep running, started again on the same private root, and the reattached pane is read against the
App's wall clock. The case is `extensions.kitty-graphics.animation` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`). No production code changed, no Core percentage changes.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract after a relaunch | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.animation` | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-animation-persistent-restore-effect-v1`) | none (no stock collector ran in this run) |

All 23 stages of the three restore cases passed (run `b7b803c3`, App commit `601cd051`, scripts commit `c0d1c2f9`, source policy v68; the App sources are identical at both commits), with seventeen validity controls held: the nine of the direct-tab publication, the four persistent-lineage controls of #1025 and
four restore controls (three relaunches in one chain of App processes, the same worker under the same daemon throughout, each reattached pane stable, each relaunch followed by a read of the reattached pane). The pinned callbacks
are replayed from the retained bundle and stay inconclusive; the divergence is recorded as `pinned-inconclusive-by-design`. This run has no stock collector, so it reports no stock grade.

## What the restored panes showed

- **A running animation continues from its original start** (`restore-playing`). The App was terminated about 4.0 s into the playback and its new pane showed the restored screen by 5.1 s (the reattach read at 5.31-5.41 s: second frame, window 5-10 s). The later reads landed at 12.36-12.42 s
  (third frame, window 10-15 s) and 17.46-17.53 s (root again, 15-20 s). A playhead the new App had restarted when it came back (somewhere in the 4.0-5.1 s window) would have shown the root at 5.3 s, the second frame at 12.4 s and the third at 17.5 s, so all three reads separate a
  restore from a restart; the oracle makes a stage **inconclusive**, not passed, when the two readings predict the same frame (the reattach read included).
- **A stopped animation stays stopped** (`restore-stopped`). Stopped on the second frame at 6.3 s, terminated right after, the restored pane showed the second frame at 7.6 s and still at 13.8 s, past a hold that would have advanced a running animation. A playhead that had instead resumed
  from the frozen frame when the App came back (resumed anywhere in the 6.3-7.4 s window, and anywhere in its frame's hold) could not show the second frame at 13.8 s, so the hold separates a stopped animation from a resumed one; the oracle makes the stage inconclusive when it could.
- **Edited and PNG-derived frames survive** (`restore-frames`). After the root was edited with `a=f,r=1` and shown, the restored pane still showed the edit; frame 2, the half-transparent PNG frame built over the root before the edit, was then selected again (`a=a,c=2`, no upload) and showed its original
  blend (`overRoot`), so its pixels, not only its slot, came back; and a second half-transparent PNG frame built over the root (`c=1`) afterwards showed the blend of the *edited* pixels, so the edited root's pixels came back from the snapshot.

The restored screen also had to equal the bound screen exactly (cells, cursor, modes) at every reattach read, and the pane kept its id across all three relaunches (the persisted session names it).

## How the App is relaunched

The owned runner now holds ownership per App process. The App saves its session file after each persistent attach, so killing it leaves what the next launch restores (the crash path the App is built for); its pane then reattaches `IfUnattached` and the daemon sends the snapshot.
The parent kills the App, checks the shell and the daemon are still alive, starts a new App on the same root, and waits until the new pane shows the restored screen before admitting the next stage. The rules (`ownership.mjs`, `static-images.mjs`, `docs/MODULE_OWNERSHIP.md`):

- only a stage the suite declares `relaunch` may change the App process, and it **must** (an uninterrupted run can never stand for a restore),
- the worker, the daemon (whose parent legitimately changes from the first App to init when that App goes), the PTY, the user and the executable must be exactly the same, and the parent's own relaunch record must link the previous and the new App,
- the daemon is held to the App that first started it, not the latest,
- a relaunch in flight is finished before cleanup, and the replacement App exiting during its start fails the run at once.

## Limits

One desktop (macOS), one App build, one image size and gap length, three relaunches. The App is **terminated with SIGTERM**, not quit through its window: the session is the attach-time save, not the shutdown path's. Not measured: a graceful quit, a second GUI client, remote persistent sessions,
other platforms, a non-PNG root, `a=c`, frame-rate or display-refresh cadence. The full collection in a persistent tab is still not claimed. No stock grade, Core score or common-harness comparison changes.

## Retained evidence and superseded attempts

`2026-11-17-kitty-animation-restore/` holds the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy, the generated `summary.json` and `report.json`, and `superseded-attempts/` with `attempts.json` (scripts commit,
outcome, reason and the SHA-256 and size of each retained file). The published run is `b7b803c301961b814367ff6c20be45c7`.

Eight earlier attempts are retained. Three produced no evidence: the first looked up the new App's endpoint while the descriptor still named the old one, and two were refused by the ownership check because the daemon's parent legitimately changes when its first App goes away. Three are complete runs of the earlier 22-stage plan and two of the 23-stage plan: one on the first
working scripts, superseded because review then changed measured files (a relaunch that changes no process, a first read that could precede the snapshot, an interrupted relaunch outliving cleanup, an early relaunch that left restore and restart indistinguishable), and one (`8fa2d38f`, source policy v67) superseded when merging main (the OSC 22 collection: source policy v68 and changes to the shared image-carrier seal checks) changed the measured graph, and one (`601cd051`) superseded when review found that the PNG-derived frame 2 was never read back after a relaunch (the plan gained the `again` stage), that a timeline read and the stopped hold did not rule out a restarted or resumed playhead, that the relaunch timing used a record the validated frame did not carry, and that a relaunch request was not tied to the next declared stage; the next (`41dfe4c1`, 23 stages) was superseded when a further review found that the relaunch admission counted relaunches only and ignored the observer's own state, so a request arriving before the setup stages or during a capture could still have terminated the App; the last (`251e0320`) was superseded by a behavior-preserving split of the runner's relaunch answer that edits a measured file. Only each attempt's failure, worker error and ownership proof (or report and receipt) are kept.

`kitty-animation-restore-publication.test.mjs` rederives the receipt binding, settings, measured sources (against the retained manifest), the chain of App processes, the daemon lineage and the whole carrier report from its journals, refuses a downgraded, stripped, unchained or foreign run, and re-checks every retained attempt file.
The suites' unit tests (`kitty-animation.test.mjs`) cover the epochs, the timeline stage and the restart/restore separation on synthetic journals.

Fresh measurement and assembly, into a new publication directory of your own (the assembler refuses one that already holds a publication, and the revisions must be the measured constants of `kitty-animation-restore-qualification.mjs`):

```sh
node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope persistent-restore
node scripts/terminfo/kitty-animation-persistent-assemble.mjs restore artifacts/terminfo/owned-<run> <new publication directory> <pinned bundle .gz> <app commit> <scripts commit>
```

Replay of the checked-in publication (it reads that fixed directory and the fixed measured revisions, not a fresh run):

```sh
node --test scripts/terminfo/kitty-animation-restore-publication.test.mjs
```
