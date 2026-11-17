# Kitty animation restored after the App is killed and started again, measured on the desktop (2026-11-17)

Issue [#1030](https://github.com/benyblack/ntilde/issues/1030), follow-up to [#1025](2026-11-17-kitty-animation-persistent-qualification.md) (a live persistent tab) and [#1017](kitty-image-lifecycle.md) (animation in persistent
sessions), parent [#770](https://github.com/benyblack/ntilde/issues/770). #1017 restores a persistent tab's animation from the daemon's snapshot; until now that was covered by deterministic tests only. This note measures it
on a real desktop with the owned animation machinery: the App is killed while the multiplexer daemon and the shell keep running, started again on the same private root, and the reattached pane is read against the
App's wall clock. The case is `extensions.kitty-graphics.animation` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`). No production code changed, no Core percentage changes.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract after a relaunch | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.animation` | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-animation-persistent-restore-effect-v1`) | none (no stock collector ran in this run) |

All 22 stages of the three restore cases passed (run `f8ee33cb`, App and scripts commit `601cd051`, source policy v68), with seventeen validity controls held: the nine of the direct-tab publication, the four persistent-lineage controls of #1025 and
four restore controls (three relaunches in one chain of App processes, the same worker under the same daemon throughout, each reattached pane stable, each relaunch followed by a read of the reattached pane). The pinned callbacks
are replayed from the retained bundle and stay inconclusive; the divergence is recorded as `pinned-inconclusive-by-design`. This run has no stock collector, so it reports no stock grade.

## What the restored panes showed

- **A running animation continues from its original start** (`restore-playing`). The App was killed about 4.0 s into the playback and its new pane showed the restored screen by 5.7 s (the reattach read at 5.8-6.2 s: second frame, window 5-10 s). The later reads landed at 12.17-12.29 s
  (third frame, window 10-15 s) and 17.41-17.57 s (root again, 15-20 s). A playhead the new App had restarted when it came back (somewhere in the 4.0-5.7 s window) would have shown the second frame at 12.2 s and the third at 17.5 s, so these two reads separate a
  restore from a restart; the oracle makes a stage **inconclusive**, not passed, when the two readings predict the same frame.
- **A stopped animation stays stopped** (`restore-stopped`). Stopped on the second frame at 6.2 s, killed right after, the restored pane showed the second frame at 8.2 s and still at 14.7 s, past a hold that would have advanced a running animation.
- **Edited frames survive** (`restore-frames`). After the root was edited with `a=f,r=1` and shown, the restored pane still showed the edit, and a half-transparent PNG frame built over the root (`c=1`) afterwards showed the blend of the *edited* pixels,
  so the edited root's pixels came back from the snapshot.

The restored screen also had to equal the bound screen exactly (cells, cursor, modes) at every reattach read, and the pane kept its id across all three relaunches (the persisted session names it).

## How the App is relaunched

The owned runner now holds ownership per App process. The App saves its session file after each persistent attach, so killing it leaves what the next launch restores (the crash path the App is built for); its pane then reattaches `IfUnattached` and the daemon sends the snapshot.
The parent kills the App, checks the shell and the daemon are still alive, starts a new App on the same root, and waits until the new pane shows the restored screen before admitting the next stage. The rules (`ownership.mjs`, `static-images.mjs`, `docs/MODULE_OWNERSHIP.md`):

- only a stage the suite declares `relaunch` may change the App process, and it **must** (an uninterrupted run can never stand for a restore),
- the worker, the daemon (whose parent legitimately changes from the first App to init when that App goes), the PTY, the user and the executable must be exactly the same, and the parent's own relaunch record must link the previous and the new App,
- the daemon is held to the App that first started it, not the latest,
- a relaunch in flight is finished before cleanup, and the replacement App exiting during its start fails the run at once.

## Limits

One desktop (macOS), one App build, one image size and gap length, three relaunches. The App is **killed** (SIGTERM), not quit through its window: the session is the attach-time save, not the shutdown path's. Not measured: a graceful quit, a second GUI client, remote persistent sessions,
other platforms, a non-PNG root, `a=c`, frame-rate or display-refresh cadence. The full collection in a persistent tab is still not claimed. No stock grade, Core score or common-harness comparison changes.

## Retained evidence and superseded attempts

`2026-11-17-kitty-animation-restore/` holds the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy, the generated `summary.json` and `report.json`, and `superseded-attempts/` with `attempts.json` (scripts commit,
outcome, reason and the SHA-256 and size of each retained file). The published run is `f8ee33cbd3592763820728adae67934f`.

Five earlier attempts are retained. Three produced no evidence: the first looked up the new App's endpoint while the descriptor still named the old one, and two were refused by the ownership check because the daemon's parent legitimately changes when its first App goes away. Two are complete 22/22 runs: one on the first
working scripts, superseded because review then changed measured files (a relaunch that changes no process, a first read that could precede the snapshot, an interrupted relaunch outliving cleanup, an early relaunch that left restore and restart indistinguishable), and one (`8fa2d38f`, source policy v67) superseded when merging main (the OSC 22 collection: source policy v68 and changes to the shared image-carrier seal checks) changed the measured graph. Only each attempt's failure, worker error and ownership proof (or report and receipt) are kept.

`kitty-animation-restore-publication.test.mjs` rederives the receipt binding, settings, measured sources (against the retained manifest), the chain of App processes, the daemon lineage and the whole carrier report from its journals, refuses a downgraded, stripped, unchained or foreign run, and re-checks every retained attempt file.
The suites' unit tests (`kitty-animation.test.mjs`) cover the epochs, the timeline stage and the restart/restore separation on synthetic journals.

```sh
node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope persistent-restore
node scripts/terminfo/kitty-animation-persistent-assemble.mjs restore artifacts/terminfo/owned-<run> docs/qa/terminfo/2026-11-17-kitty-animation-restore <pinned bundle .gz> <app commit> <scripts commit>
node --test scripts/terminfo/kitty-animation-restore-publication.test.mjs
```
