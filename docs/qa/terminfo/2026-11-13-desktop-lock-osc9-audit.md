# Desktop collection lock enforcement and the OSC 9 collection audit

Date: 2026-11-13. Scope: enforce the shared desktop collection lock in the supported terminfo desktop entrypoints,
and reconcile what the retained records say about desktop exclusivity during the OSC 9 qualification (PR 960 / merge
`b0999910`, reviewed head `dcd59e0c`; `extensions.osc9-progress` / `extensions.notifications`). This page makes **no
benchmark, conformance or compatibility-score claim**: holding a lock is a precondition for a measurement, never evidence
about one, and nothing here re-grades any case.

## Correction of the "unlocked OSC 9 run" allegation

This batch was started on a report that an OSC 9 progress measurement had run **without** the shared desktop lock. That
report was a discovery lead, not a finding, and the evidence does not support it:

* PR 960's final report (merged as `b0999910`) states that every one of its GUI collections used the shared desktop lock and
  that none ran unlocked. The retained records agree: all fourteen OSC 9 GUI attempts, including the published run, ran
  through one shell wrapper that took the shared `mkdir` lock first (zero `waiting for lock` lines, one `lock released` line in
  each wrapper log; the table below).
* What *was* true is narrower: the lock was a **wrapper convention**, not something the collector enforced, and the
  records cannot show who else honoured it. That gap is what this batch's enforcement closes.
* One real overlap with a *different* session's collection exists (attempt 8, below). The merged OSC 9 page describes that
  other collection as "unlocked"; this audit can support only the weaker statement that it did not hold the shared lock path
  during the overlap (an inference, see finding 2), so the word should be read that way.

Provenance of this correction: the batch brief (the allegation), PR 960's final report and merged page
`docs/qa/terminfo/2026-11-13-osc9-qualification.md` (the correction), and the retained run directories and wrapper logs listed
below (the reconciliation). The earlier drafts of this audit are in PR 961's history; nothing in PR 960's publication was edited.

Three classes are kept apart throughout:

| Class | Meaning here | OSC 9 attempts |
| --- | --- | --- |
| **Shared-lock convention** | The run's wrapper took the shared `mkdir` lock (its own records show it); nothing in the collector enforced it | all 14 GUI attempts |
| **Enforced lease** (this batch) | The launcher itself took and released a lease with verified cleanup, recorded in the audit log | none: it did not exist yet (only this batch's two validation runs) |
| **Ownership cannot be established** | No retained record shows how, or whether, the run used the shared lock (or it used a different lock path, which the shared lease cannot see) | the overlapping reflow collection `FoQxOl`; every other session's collection outside the OSC 9 worktree (one live example, below, used `/tmp/ntilde-qa-desktop-collection.lock`) |

## Enforcement (what is code, what is documentation)

| Behavior | Status |
| --- | --- |
| `scripts/terminfo-owned.mjs` (owned App collections, including their preflight, retry and smoke launches) takes the lease before any output, App, window or focus action; refused contenders launch nothing | **Enforced** by the launcher/runner |
| `scripts/terminfo/common-core-driver.ts` takes the same lease before creating output; the old regular-file lock at the same path is gone | **Enforced** |
| Launch intent is persisted before spawn; release requires verified cleanup (worker and App exited, no process carrying the lease's run markers); anything else retains a `cleanup-failed` lease | **Enforced** |
| Recovery of a dead holder | **Explicit command only** (`scripts/terminfo-desktop-lock.mjs recover`), never automatic |
| Other tools (`terminfo-baseline.mjs`, preflight, Linux self-tests, publication/receipt/inventory checks, `--help`, deterministic tests) | Unlocked by design; stay usable while another session owns the desktop |
| Sessions running their own `mkdir` wrapper | **Documentation/convention only**: still honored, because the lease uses the same atomic `mkdir` at the same path |
| Hostile or careless same-user processes that ignore the lock | **Not protected** (cooperative boundary) |

The full protocol, lifecycle and recovery rules are in [QA_TERMINFO.md](../QA_TERMINFO.md#desktop-collection-lock).
The helper `scripts/terminfo/desktop-lock.mjs` is loaded by the launcher through a pinned SHA-256 and sits outside the
measured source graph, so no source policy or receipt schema changed and every earlier receipt and publication keeps
verifying unchanged.

## Audit of the OSC 9 collections

### Sources and what they can establish

* **Run directories** (`artifacts/terminfo/owned-*` in the OSC 9 worktree, not committed): `context.json` (start),
  `ownership-proof.json` (`cleanedUpAt`), `failure.json`, `osn-focus.json`, `osn-snapshots.jsonl` (per-frame
  `windowActive`). These are the runs' own records.
* **Wrapper logs** (`attempt1.log`–`attempt14.log`, in the OSC 9 session's scratchpad, not in the repository): each
  collection ran through one shell wrapper that took the *manual* `mkdir` lock, wrote `owner=<label> pid=<wrapper> since=<time>`,
  and released only a lock it created. Every log shows `waiting for lock` zero times and `lock released` once.
* **The OSC 9 owner's own account** (session reply, quoted as data), and the retained transcripts' command timeline.
* **An overlap scan** across every worktree's retained runs (`node scripts/terminfo-collection-overlap.mjs`, 1159 run
  contexts examined) plus a file-activity sweep (`app-output.log`, `cleanup.json`, `ownership-proof.json`,
  `launch-request.json`) for each final-run window.

Current lock state cannot prove historical exclusivity: no lock directory existed before or after any of these runs, and
no audit log existed before this change, so **no historical lease facts exist and none are fabricated here**. What follows is
direct observation unless marked *inference*.

### Findings

1. **All fourteen GUI collections ran under the shared-lock convention (the manual `mkdir` wrapper); none ran under a code-enforced lock**: the lock was
   a wrapper convention, not enforced by the collector (this change fixes that). The three `owned-*` directories
   with executable `/usr/bin/true` (`2aKkGh` 13:50:29Z, `0tN5Rt` 13:57:17Z, `QAPHaS` 14:47:42Z) are the launcher's lifecycle
   smoke tests ("Owned app exited before collection (0)", under 1 s each): no GUI App was opened, so they did not need the lock.
2. **One real overlap with the OSC 9 runs exists (verified intervals; reported cause; unresolved lock practice).** Attempt 8 (`3b6a9e45…`, directory `NvA4Ce`, App pid 48172) ran 14:15:29.143Z–14:17:34.830Z. A
   successful collection from another worktree (`terminfo-ascii-reflow`, directory `FoQxOl`, run `4b6916b3…`, owner pid
   49487, a different sidecar) ran 14:15:36.872Z–14:16:43.724Z: both recorded intervals intersect for about 67 s.
   * *Verified:* the two recorded intervals intersect (each run's own `context.json` and `ownership-proof.json`), and attempt
     8's wrapper released a lock that still carried its own owner record at exit, so attempt 8 held the shared lock path for
     its whole window.
   * *Reported:* PR 960 describes the other collection as unlocked; that is a characterisation, not a retained record.
   * *Unresolved:* which lock implementation or path the reflow collection used. It did not hold the shared path while
     attempt 8 did (inference: `mkdir` at that path would have failed), but it may have used no lock, a lock elsewhere (its Nov 12 session also worked in a separate `termcheck` checkout), or a manual removal; no retained record of that session's
     14:15Z activity exists locally (its Nov 11-12 Codex rollouts only record the instruction to honour the lock and a
     preflight that tests the lock path's existence). Not every reflow attempt is thereby unlocked: the reflow runs
     `iUlKFK` and `1hRI4f` (14:09Z, 14:11Z) overlap nothing.
   * The reflow owner could not be reached through the session tools (no live session for it), so nothing about its work,
     lock or processes was changed or asked of it.
3. **Interference was observed in attempt 8.** The run's per-frame `windowActive` rose from `false` to `true` at frame 45,
   after the parent's `defocus` (14:16:20Z) and before its third action (`refocus`, 14:17:01Z; ordered by frame and action
   records, not by per-frame time) with no parent focus action performed. The retained report grades the notification case
   `inconclusive` (`inactive-window-precondition-unmet`) for that reason; its progress case is `supported` with its controls
   held, which does not establish desktop exclusivity. Frame times are not recorded, so
   tying the activation to the other App's window opening or closing (14:15:36Z / 14:16:43Z) is *inference*, not observation: it is this audit's inference, and neither this audit nor the OSC 9 owner has direct evidence that the overlap caused the focus change (a user or another process could equally have activated the window).
4. **No other run shows an unexplained transition.** In every other GUI run the `windowActive` transitions are exactly
   those of the parent's `refocus` / `defocus` / `refocus` actions; terminal geometry is 40 rows throughout; no wrapper log
   contains an "unexpected input" line. No other recorded GUI collection in any worktree overlaps any other OSC 9 run.
5. Native window bounds are not recorded in the OSC 9 observation files; that required-condition input is **not
   available** for any attempt and no disposition below depends on it.

### Per-attempt dispositions (separate from, and not altering, the retained evidence or grades)

| # | Run (dir) | Recorded interval (UTC) | Wrapper lock | Overlap | Outcome as retained | Disposition |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `83474894…` (`bABVkX`) | 13:15:12.464–13:16:57.096 | held | none | aborted (own request kind unrouted); cleanup completed | Unusable as measurement (aborted); lock not the reason; keep as superseded |
| 2 | `393946b3…` (`cFBL9G`) | 13:18:12.684–13:19:36.782 | held | none | failed (`EEXIST` rewriting a result file); cleanup completed | Unusable (failed); keep as superseded |
| 3 | `73698eb5…` (`LFSJHu`) | 13:21:53.409–13:24:23.535 | held | none | refused by the owner's post-run check | Unusable (refused); keep as superseded |
| 4 | `fc1c394e…` (`TVxJZA`) | 13:27:39.857–13:30:14.388 | held | none | complete | Observations usable as retained; superseded because measured scripts changed afterwards |
| 5 | `f55d10a4…` (`Vjz8WQ`) | 13:32:50.117–13:35:24.068 | held | none | complete | Same as 4 |
| 6 | `af475cd2…` (`FbhzFq`) | 13:38:42.210–13:40:54.611 | held | none | complete | Same as 4 |
| 7 | `129e1504…` (`DnUsZm`) | 13:57:27.335–14:00:22.631 | held | none | complete | Same as 4 |
| 8 | `3b6a9e45…` (`NvA4Ce`) | 14:15:29.143–14:17:34.830 | held (manual) | **yes, ~67 s with `FoQxOl`** | complete; notification case `inconclusive` on an unexplained activation, progress case `supported` | **Interference observed: the notification observation is not usable; the progress observation is uncertain** (controls held, exclusivity unestablished). Superseded; its real reason includes the overlap, not only the script changes |
| 9 | `ec590d43…` (`rt5pO5`) | 14:18:18.840–14:21:14.930 | held | none | complete | Usable as retained; superseded by script changes |
| 10 | `297a178e…` (`ac9Orw`) | 14:23:45.451–14:25:53.541 | held | none | complete | Same as 9 |
| 11 | `e6d1207f…` (`eEBjR9`) | 14:27:29.891–14:29:32.391 | held | none | complete | Same as 9 |
| 12 | `ce49a60a…` (`7qTdRj`, policy v51) | 14:38:30.146–14:40:39.294 | held | none | complete | Usable as retained; superseded by the restack to the final scripts |
| 13 | `b9369a7b…` (`7YNOki`, policy v51) | 14:54:15.635–14:56:27.897 | held | none | complete | Usable as retained; superseded because a last SonarCloud fix touched a measured file |
| 14 | `77fa834f…` (`vjFwK5`, policy v51, receipt 57, scripts `7a580871`, App from `1ac4476c`, stock run `26c93f5a…`, **published**) | 15:16:39.118–15:18:52.330 | held | none | complete; both effect contracts `supported`, pinned verdicts unchanged | **Usable.** Required conditions are directly recorded (window inactive at launch, each parent focus action verified by the frontmost PID, every `windowActive` transition (frames 38, 40, 57) explained by the parent's `refocus`/`defocus`/`refocus`, geometry stable); exclusivity beyond the shared-lock convention is supported by the absence of any other recorded collection in the window and by the in-run checks, but cannot be proven |

### Replacement measurement decision

No fresh OSC 9 measurement is required by this change, and none was made for the corrected allegation. The only interfered run (8)
is already superseded and not published; the published run (14) has no recorded interference and its measured plan, observer and App are unchanged by this PR, which adds
lease acquisition and cleanup verification around the existing collection without changing what is measured. A replacement
run through the enforced path would be warranted if any OSC 9 measured input changes, if a later audit finds a recorded overlap
with run 14, or if the owner wants exclusivity to rest on the enforced lease rather than the wrapper convention; it must be
arranged with the OSC 9 owning session and would retain run 14 as superseded with that real reason.

### Evidence retained and unavailable

* Retained in the repository (PR 960, on `main`): the published run and thirteen superseded attempt archives with their SHA-256 manifest
  (`docs/qa/terminfo/2026-11-13-osc9/`); `node --test scripts/terminfo/osc9-publication.test.mjs` re-derives them. They are archives of the runs' own files and are not rewritten here.
* Local only, **not** in the repository: the wrapper logs (their SHA-256 prefixes are recorded below), the OSC 9
  worktree's `artifacts/terminfo/owned-*` directories (all retained, none deleted, per the owner), and the three smoke runs.
* **Unavailable:** any lock-state record from the time (none existed), the overlapping session's transcript and lock practice,
  per-frame timestamps for `windowActive`, native window bounds, and the follow-up lead "hmk5" (it matches no session, run, task or
  commit; it appears only inside unrelated encoded text).

| Wrapper log | Bytes | SHA-256 (first 12) |
| --- | ---: | --- |
| `attempt1.log` | 328 | `55479bc8b6ae` |
| `attempt2.log` | 424 | `cda4f87a32a1` |
| `attempt3.log` | 359 | `9a105a9b6250` |
| `attempt4.log` | 31931 | `ecfa61695411` |
| `attempt5.log` | 31893 | `5e2cae9a7a60` |
| `attempt6.log` | 31893 | `c975a2c81994` |
| `attempt7.log` | 31892 | `a9411d6c7786` |
| `attempt8.log` | 31931 | `f4dfc38e4445` |
| `attempt9.log` | 31893 | `0f4865fdee34` |
| `attempt10.log` | 31930 | `cbe07847f556` |
| `attempt11.log` | 31893 | `f442d071ea3d` |
| `attempt12.log` | 32505 | `a1b6f94253df` |
| `attempt13.log` | 32563 | `ef804c96c3b7` |
| `attempt14.log` | 32505 | `eb43d2b361ca` |

### Other recorded overlaps (outside the OSC 9 runs; recorded, not remediated)

Scanning the five worktrees that ran collections (OSC 9, ascii-reflow, XTGETTCAP, modifyOtherKeys and this batch) finds four
more recorded GUI interval overlaps, all among other sessions and none involving the OSC 9 runs. The scan cannot say which side
lacked the lock, or whether either did:

| First (worktree, UTC) | Second | Overlap |
| --- | --- | ---: |
| `lzjMtN` XTGETTCAP, 11-12 13:14:43–13:16:06 | `hnT45w` modifyOtherKeys, 13:14:56–13:15:42 | 46.5 s |
| `ksRijX` ascii-reflow, 11-12 14:18:27–14:20:13 | `Y7cH9t` XTGETTCAP, 14:19:42–14:20:53 | 30.9 s |
| `sWZqnR` ascii-reflow, 11-12 15:38:04–15:41:18 | `5LFHJB` XTGETTCAP, 15:41:11–15:42:29 | 7.3 s |
| `r76NTC` XTGETTCAP, 11-13 09:54:02–09:55:07 | `4VAYK1` ascii-reflow, 09:54:42–09:55:47 | 24.9 s |

They concern the completed XTGETTCAP and modifyOtherKeys publications, which this task was told not to rewrite, and the open
reflow work, which belongs to its owner; they are reported only so those owners can decide. They do not make any reflow
attempt, and in particular not the final OSC 9 run, unlocked or invalid by association.

### Reproduce the overlap scan

```sh
node scripts/terminfo-collection-overlap.mjs <worktree>/artifacts/terminfo [<worktree>/artifacts/terminfo ...]
```

It reads only each run's `context.json` and `ownership-proof.json`, treats an interval with no recorded cleanup as still
running, ignores `/usr/bin/true` smoke runs, and reports facts about two recorded intervals, never which side broke exclusion.

## Live validation of the enforced lease

Coordinated with the OSC 9 owning session (the only active collection session; it confirmed an absent lock and no planned
run, and waited for the release message). The App was a Release build of `origin/main` 1ac4476c; the scripts were this branch's
launcher at commit 9662d4a6. These are lock-lifecycle validations, **not** published measurements: no grade, receipt claim or
inventory change comes from them. Both runs used `node scripts/terminfo-owned.mjs ...` unchanged, from a worktree of their own.

| | Run 1 (`yQr36J`) | Run 2 (`aEi9Sg`) |
| --- | --- | --- |
| Lease | `2e706233…` | `b2e48bf6…` |
| Acquired / launch persisted | 15:12:06.686Z / 15:12:08.410Z | 15:14:23.103Z / 15:14:25.938Z |
| Observed while held | owner record `status=held launch=attempted` with the three run markers; `status` reported `holder: alive, pid-and-start-match`; the App (child of the launcher) and its worker carried the lease's markers | not sampled |
| Second contender (real launcher, 15:12:21Z) | refused with exit 1, "Desktop collection lock is unavailable (owned, holder alive). No App was launched."; no second App, no new run directory, lease unchanged; `--help`, `status` and the inventory check all worked | not repeated |
| Collection | failed in the colour-reset suite 80 s in ("crst collection and restoration failed"; the underlying worker error is not retained in the run's files); no receipt | completed, receipt written, exit 0 |
| Cleanup verified (marker scan) / lease released | 15:13:26.782Z / 15:13:27.823Z | 15:15:51.062Z / 15:16:13.339Z (after the receipt was completed) |
| Afterwards | lock absent; no process carrying a marker; no sidecar process | same |

Run 3 (`316ed89a…`, final PR head `3e78d835`, same sidecar): acquired 15:52:16.718Z, launch persisted 15:52:18.293Z, cleanup verified
15:54:45.273Z, released 15:55:08.899Z after the receipt was completed; collector exit 0; lock absent and no owned process of this
batch afterwards.

**A divergent lock path was observed live.** At 15:54:54Z, 14 s before run 3 released, another session's wrapper
(`/tmp/selc-measure.sh`, the selection-colors measurement) launched an owned collection. Its header documents an advisory `mkdir`
lock at `/tmp/ntilde-qa-desktop-collection.lock`, **not** the shared `~/.ntilde-qa-sidecars/.desktop-collection.lock`, so it neither
waited for nor excluded the shared lease. Run 3's App had already exited and its cleanup was already verified (15:54:45Z), so
nothing was disturbed; that session's work, lock and processes were not touched. It is the second class of "ownership cannot be
established by the shared lock": a collection protected only by a different lock path. The enforced launcher cannot see such a
path, which is why the shared path is the one documented convention; the owner of that wrapper should adopt it.

The failed first run is retained as it happened. Its cause is **not established**: the same scripts, App and desktop
completed in run 2 about two minutes later, and nothing in the lock path touches the colour-reset suite, but no evidence
excludes a transient desktop condition. It does show the failure path too releases only after verified cleanup. The run
directories are local, git-ignored artifacts; the committed record of both is the audit log lines above
(`~/.ntilde-qa-sidecars/.desktop-collection.log`, local to this machine).

Deterministic coverage lives in `scripts/terminfo/desktop-lock.test.mjs` (independent-process races with file barriers,
refusal of every ambiguous state, recovery rules including a swapped-in lease), `desktop-lock-launcher.test.mjs`
(fixture launchers: clean release, preparation failure, spawn failure, surviving process, offline help while held, tampered
helper, repeated signals, source-order invariants for the runner and driver) and `collection-intervals.test.mjs`.
