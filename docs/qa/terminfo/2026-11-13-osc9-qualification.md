# OSC 9 progress and notifications qualification (2026-11-13)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `extensions.osc9-progress` and
`extensions.notifications`. **No production code changed**: this measures existing behavior (`HandleOscNotificationOrProgress`, `OnProgressReported`,
`OnDesktopNotification`) through the existing pane-metadata observer.

## What the pinned probes can say

Read from the pinned bundle, both can only ever return **inconclusive** and neither writes, feeds or reads a byte on either path:

| Case | Pinned termless | Pinned terminal path |
|---|---|---|
| `extensions.osc9-progress` | **no function at all** (the registration passes `null`) | `unmeasuredStateEffect("OSC 9;4 progress display")`: `insufficient-evidence`, evidence `none` |
| `extensions.notifications` | declared-capability report `{"declared": …has("osc9")}`: `insufficient-evidence`, evidence `legacy` | "No desktop notification delivery readback for OSC 9": evidence `none` |

Those verdicts are preserved unchanged (the unchanged pinned functions run in the layered derivation, and an independent static replay must reproduce them). The
termless capability is an adapter-side declaration the pinned code is served; it never decides anything. No observation can upgrade either verdict.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.osc9-progress` | none (no termless function) | inconclusive by design | **supported** (`ntilde-osc9-progress-state-effect-v1`) | inconclusive |
| `extensions.notifications` | inconclusive by design | inconclusive by design | **supported** (`ntilde-osc9-notification-toast-effect-v1`) | inconclusive |

The effect contracts are separately identified, with their own bytes and oracle; the divergence (`pinned-inconclusive-by-design`) is recorded in
[report.json](2026-11-13-osc9/report.json). The stock collector, re-run in the same owned run (`32751fafa46975094d07cdffa41fe454`), still grades both inconclusive
(87 supported / 1 unsupported / 167 inconclusive, `suiteComplete=false`): **no stock score and no Core point is added.**

## What the App did (disposable direct-local pane, 40×140)

The oracle is written from the OSC 9;4 protocol (ConEmu / Windows Terminal: state 0 remove, 1 normal, 2 error, 3 indeterminate, 4 paused, progress 0–100) and the
App's documented tab-title suffix; the production parser is never called for an expected value. Each experiment starts from a prepared baseline (progress withdrawn,
toast hidden, run-bound title, cwd and nonce row), reads a distinguishable setup, feeds one payload, reads the effect, and compares everything else (complete screen,
cursor, cwd, links, rendition, status) with the bound state. 55 paired reads in all, each after a completed DA fence barrier.

**Progress (18 experiments, 36 reads).** Graded protocol experiments, all matched: normal 42, error 37, indeterminate, paused 61, error replacing a live normal
report, withdrawal of a live report, percentage clamped at 100 (`150`), percentage 0, percentage ignored for the indeterminate state, ST terminator. Controls: an
unmarked feed (SGR reset) leaves a live report untouched, and withdrawal with nothing set changes nothing. For every report the stored kind and percent, the owning
pane and the **actual tab suffix** (` 42%`, ` ✖ 37%`, ` ⏸ 61%`, ` ⋯`) matched; OS taskbar presentation is not claimed.

**Policy, recorded and never graded** (each matched the documented policy): an absent percentage is stored as no percentage (normal, error, paused), an unknown state
(`7`) leaves the live report untouched and is *not* a withdrawal, a missing state (`4;`) is dropped, and a bare `4` is notification text.

**Notification (9 experiments, 19 reads).** The private in-window toast is read from the pane metadata: visibility, title (the pane's base tab title), message,
auto-hide and restoration, with the window-focus and opt-in preconditions recorded at control time and at delivery. The parent performs and retains three focus
actions (`refocus`, `defocus`, `refocus`), each verified by asking System Events which process is frontmost (recorded in `osn-focus.json`; the observed
`windowActive` is what is graded). Positive delivery (`NT-notify-…`, BEL and ST) shows the exact title and message while the window is inactive, the toast is gone
after the wait (auto-hide verified, restored), the unmarked control and an `OSC 9;4;1;50` progress payload (stored as progress) produce no toast. Policy records
(never graded): with the window active and the pane current the toast is suppressed (applicable in this run), a TAB is flattened to a space, a 250-character text is
capped at 200 characters ending in an ellipsis, a whitespace-only text is dropped, and a bare `4` is shown as text `4`.

**Decision rule.** Had the inactive-window delivery control not held at both control time and delivery, the notification case would have stayed inconclusive with the
concrete reason retained; attempts 4, 8 and 10 below show exactly that outcome (a refocus that did not take, a window that became active again, and a focus action that was never
admitted, respectively); each was kept as a superseded attempt, not graded.

## Identities

App 0.13.0 (operator supplied) built from `b2121100df7c76773e1a15ba569cb3f5526209dc` (arm64 Release, executable SHA-256 `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly
SHA-256 `ce358b1b093775e507df6ac50b0d9b2f3a2674b54c675654c510a33a042c773a`; the binary is bit-identical to the title/icon-stack and XTGETTCAP runs, since no production
source changed); measured scripts `c98482ee2aa9c7ca7ee3ee760774a2d082a2c5b3`; source policy `readonly-owned-source-graph-v50` (336 files; manifest SHA-256
`94bcd9440b4b53738750757be89eab4486eb2a9a92226280b29cf63c5d61e4a6`); initial settings SHA-256 `e907bb0eb015d37d1ffe53af385bf1553e880b4efcdd0bd56076be9cf0956d60`; receipt schema 56; Node
v24.22.2; owned run `e6d1207f698b7f9ef4b12a6ae245cbb0`; collected 2026-11-13T14:27Z on macOS. The App label is operator supplied and the build is not binary-to-source
attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the retained bytes.

## Retained attempts ([superseded/](2026-11-13-osc9/superseded/))

1. Aborted: the owner refused the parent focus-action request kind (`Unknown owner request`); cleanup completed.
2. Completed every suite, then failed writing the focus record a second time (exclusive-create). It also showed the App window was already inactive at launch and that a
   bare-`4` toast from the progress phase leaked into the next setup read.
3. Completed; the parent's own restoration check refused the run because the tab's focus-dependent attention marker cleared when the window was refocused.
4. Completed with progress supported but notifications **inconclusive** (the refocus did not take in that run; validity control failed). Kept as measured.
5. Completed with both effect contracts supported; superseded by a local Codex finding (focus retries could outlast the owner socket's five-second limit).
6. Completed with both effect contracts supported on the bounded focus action; superseded because the full script suite requires the runner's dispatch to stay three anchored
   expressions and one prefix set, so the focus actions moved to an exact-kind set (the runner is a measured file).
7. Completed with both supported; superseded by the SonarCloud findings (nested ternaries, an unused prerequisite value, a stringification and a one-line conditional in
   measured files).
8. Completed, but the notification case came out **inconclusive** (`inactive-window-precondition-unmet`): the App window became active again after the second delivery
   experiment although the parent took no action. Kept as measured, ungraded.
9. The same scripts rerun: both supported; superseded by CodeRabbit's finding that the focus action was not bounded by the owner request's remaining deadline.
10. Completed with the focus action bounded by that deadline, but the shared deadline reports at most two seconds at a time, which my first attempt-sized bound could never
    fit: no focus action ran and the notification case stayed **inconclusive** with its reason. Kept as measured, ungraded.
11. Final run: a whole focus attempt (command, settle, query: 1.6 s worst case) is admitted only while the reported deadline remainder covers it.

## Not claimed

OS taskbar or desktop notification delivery beyond the in-window toast; OSC 777, OSC 99 and OSC 9 variants outside these two pinned fixtures; real-program emission
(Claude Code and similar); SSH or mux delivery; Windows or Linux; hidden panes; any focus-change behavior that was not observed. The focus actions are performed by
macOS automation on the disposable App and do not establish behavior under real user focus changes.

Re-derive the receipt, every predecessor suffix and this publication from retained bytes with `node --test scripts/terminfo/osc9-publication.test.mjs`; the inventory is
regenerated with `node scripts/terminfo-inventory.mjs --write --rederive`.
