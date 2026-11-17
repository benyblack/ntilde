# Kitty animation in a live persistent tab, measured on the desktop (2026-11-17)

Issue [#1025](https://github.com/benyblack/ntilde/issues/1025), follow-up to [#1017](kitty-image-lifecycle.md) (animation in persistent sessions) and [#1015](2026-11-16-kitty-animation-qualification.md),
parent [#770](https://github.com/benyblack/ntilde/issues/770). #1017 shipped animation in persistent (mux-backed) sessions with deterministic tests only. This note measures the same owned
animation suite (`ntilde-owned-kitty-animation-v2`) in a **live persistent tab** on a real desktop, for the same case `extensions.kitty-graphics.animation` (bundle SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`). No production code changed. The case stays outside the frozen Core catalogue, so no Core percentage changes.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract in a persistent tab | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.animation` | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-animation-persistent-playback-effect-v1`) | none (no stock collector ran in this run) |

The results stay separate. Both pinned callbacks are replayed from the retained pinned bundle and stay inconclusive whatever the App declares; the divergence is recorded as
`pinned-inconclusive-by-design`. This run has no stock collector, so it reports no stock grade and does not touch the direct-tab publication's stock result (161 inconclusive / 93 supported / 1 unsupported).

All 24 stages passed, the same four cases as the direct-tab run (stationary control, playback, stop-freeze, PNG and root-based frames), with every write acknowledged `OK` through the
daemon and the client, source-derived frame colors at the controlled anchor, unchanged surroundings and a blank target after each clear. Thirteen validity controls held: the nine of the
direct-tab publication (receipt binds the report, raw reader healthy, state restored, matching controlled background, fresh paired fences, blank-before controls, acknowledgement separate
from pixels, every timed read inside its window, carrier policy) and four persistent ones (below).

## Wall-clock evidence

Elapsed time is the App's own monotonic clock, as in the direct tab. Measured against the bracket in which `a=a,s=3` could have taken effect (15 ms wide), the playback
captures covered 7.46-7.52 s (second frame window 5-10 s), 12.46-12.52 s (third, 10-15 s) and 17.48-17.54 s (root again, 15-20 s). In `stop-freeze` the stop took effect at
6.24-6.25 s, inside the second frame, and a capture 12.35 s after the start still showed the second frame, where a running animation would have shown the third.

## What makes it a persistent tab

The owned runner measured direct tabs only: it proves the worker is a direct child of the App. With `SessionPersistence` `KeepOnClose` the App starts the multiplexer daemon detached
(`Ntilde mux serve`, parent PID 1) and the daemon hosts the shell, so the worker's parent is the daemon. A first attempt confirmed it: the App exited at once with "Worker is not a direct
child of the owned app". The ownership proof was extended, not relaxed (`scripts/terminfo/ownership.mjs`):

- the daemon is the launched executable running `mux serve` (the path is matched whole, so spaces are safe),
- its environment carries this run's private app-data root and run identity (`ps eww`; a value is matched as a whole, never split),
- it advertises itself through a descriptor inside that private root, and started no earlier than the owned App,
- the worker is its direct child, and every observed ownership fact in the retained journal carries the daemon record
  (`validateImageOwnership` refuses a frame under any other parent, and a direct run cannot gain or drop the record alone).

Cleanup stops the daemon: `mux kill-server` first, then SIGTERM/SIGKILL only while a re-read of its command line and environment still proves it is this run's. Candidates come from the recorded
identity and from a scan of this user's processes (not the descriptor, so a daemon interrupted before publishing one is found too), and a scan that still finds one afterwards, or fails,
fails the cleanup and keeps the desktop lease. The four persistent validity controls are: tab configured persistent, worker hosted by the daemon (never a direct child), daemon is the launched
executable serving the private root, daemon stopped before the receipt.

## Why a separate collection, not the full one

The full owned collection runs about 60 suites in one worker and produces one receipt. Running it in a persistent tab was tried and is **not** supported: once the ownership proof passed,
the first suite reported unexpected input once, and in another run the Resize suite failed because the agent observer refused the read after the first resize. Both are properties of the
full collection in a persistent tab ([QA_TERMINFO.md](../QA_TERMINFO.md) already says persistent panes answer some probes differently by design), outside this issue. The persistent run is therefore its own
collection (`--scope persistent-animation`): only the animation carrier runs, with its own receipt (`persistent-animation-receipt.json`) binding the one retained report, the ownership proof
and the context. It reuses the carrier, observer, cases, oracle and report builder unchanged, and the launcher's captured source graph (source policy v67, no new measured file).

## Limits

One live persistent tab, one desktop (macOS), one App build (App and scripts revision `33394ffa`), one image size, one gap length and one PNG encoding. Not measured: a **reattach or restart**
(restoring a tab from a snapshot is still covered by deterministic tests only), a second client, remote persistent sessions, other platforms, a non-PNG root, `a=c`, frame-rate or
display-refresh cadence. The full collection in a persistent tab is not claimed. No stock grade, Core score or common-harness comparison changes.

## Retained evidence and superseded attempts

`2026-11-17-kitty-animation-persistent/` holds the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy, the generated `summary.json` and
`report.json`, and `superseded-attempts/` with `attempts.json` (each attempt, its scripts commit, outcome and reason, and the SHA-256 of each retained file). The published run is
`7462f86c6e54ea7671b53dd349286de2`, measured on the scripts and App of commit `33394ffa`: the assembler checks that commit against the run's recorded digests for the scripts, while the App revision is operator-supplied (a built binary's digest cannot name a commit).

Ten earlier attempts are retained with their real outcomes. Six produced no evidence: a temporary persistence switch on the full collection (the direct-child ownership proof cannot hold),
the daemon descriptor read with the wrong field casing, unexpected input in the first suite, Resize failing after the first resize, a missing proof slot in the first dedicated runner, and the
shared image-ownership check still requiring a direct parent. Four are complete 24/24 runs superseded because later changes touched measured files: one before the first Codex round's
fixes (`a9081b65`), one after it (`b4bf3c83`, superseded after Sonar and CodeRabbit comments led to edits of measured scripts) and one after those fixes (`39fac4de`, source policy v66), superseded
when merging main (the OSC 720 batch: source policy v67, receipt 74, changes to the runner, worker and launcher) changed the measured graph, and the last (`a844dbce`, merged tree, source policy v67) after a second review round (a cleanup error that must not hide the original, absolute executables in the assembler) edited the measured runner. The raw directories of the six no-evidence attempts (about 30 MB each for the full-collection ones) are not kept; their
failure, worker error and ownership proof are.

`kitty-animation-persistent-publication.test.mjs` rederives the receipt binding, settings, measured sources (against the retained manifest), daemon lineage and the whole carrier report from its
journals, refuses a downgraded, stripped or foreign run, and re-checks every retained attempt file against `attempts.json`. The assembler stages and verifies everything first, refuses an occupied destination
and requires the scripts revision to be the commit whose files the run's digests name. Collect with the persistent scope under the shared desktop lock, then assemble:

```sh
node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope persistent-animation
node scripts/terminfo/kitty-animation-persistent-assemble.mjs artifacts/terminfo/owned-<run> docs/qa/terminfo/2026-11-17-kitty-animation-persistent <pinned bundle .gz> <app commit> <scripts commit>
node --test scripts/terminfo/kitty-animation-persistent-publication.test.mjs
```
