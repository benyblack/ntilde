# Common upstream application comparison

This experiment runs the same current upstream terminfo.dev application callbacks on
Ntilde, iTerm2 and Ghostty. It uses the 80 frozen Core feature IDs as its target;
79 currently have terminal callbacks. The remaining manual item stays untested.
It does not import results from Ntilde's independent 80/80 qualification.

## Method

All subjects run serially on this macOS desktop under the shared collection lock.
Each launch creates a disposable window and runs the identical worker through its
actual PTY. The parent records executable digest, version, upstream source identity,
worker/driver digests, native-window ownership and actual `stty` geometry after
launch configuration. The comparison refuses different grids, shared workers or upstream collector identities.
Application launch adapters may differ; each exact driver and worker snapshot must
match its recorded digest. The final driver requires an explicit Ntilde build version;
the earlier Ghostty launcher is retained unchanged and obtains its version from the
installed application plist. That version-argument fix changes no upstream test.

The upstream collector owns the fixture bytes, controls, assertions, result statuses
and finalization. Our launch adapter adds Ntilde identity and native iTerm window
custody. It admits complete native 1x or 2x screenshots without resampling; cropping,
anisotropic scaling and other backing scales are refused. Definitions and graders
under `packages/probe-defs` and `packages/probes` remain unchanged. The patch,
source snapshot and dependency lock are retained, including their original license.

The display font, theme and application defaults can differ. Ghostty uses its
upstream isolated default configuration, Ntilde uses a private settings directory,
and iTerm uses its installed default profile. Only bounded display fields are
exported from iTerm preferences; no saved commands or environment are published.
Raw protocol replies and screenshots retain what was actually measured.

A matched suite does not guarantee matched observer availability. The upstream
calibration can refuse an application's reported geometry, and its ownership policy
can withhold state-changing probes. Each affected row retains the exact reason.
Counts must not be presented as a capability ranking when those limits differ.

Collector drafts deliberately retain `identity: unverified`. After collection we
apply the upstream app launcher's own `verifyTerminalIdentity` guard to the raw
replies, retaining a separate digest-bound result. This does not rewrite the raw
capture or change any feature verdict. A failed or missing guard blocks publication.

## Results and evidence

The dated publication contains exact raw schema-4 runs, parent/worker records,
cleanup outcomes, digest-addressed native screenshots, a complete source snapshot
and a history of pilot, failed and superseded attempts. The website projection is
derived from those retained bytes. It cannot replace inconclusive, invalid or
untestable outcomes with Ntilde's deeper qualification conclusions.

Every screenshot reference must resolve to its exact PNG digest inside the subject's
archive. The raw JSON remains immutable inside the run archive and must match its separately recorded digest. Readable per-case records and their upstream assertions derive from it. Successful
cleanup requires both application/window absence and recorded worker exit.

## Reproduction

Use Bun 1.4.4, Node 24+, Python 3, ImageMagick and the recorded macOS applications.
Extract the source snapshot, install its lock with `bun install --frozen-lockfile
--ignore-scripts`, and retain an unmodified source-suite identity. Collect serially:

```sh
bun scripts/terminfo/common-core-driver.ts <upstream> iterm2 <new-output>
bun scripts/terminfo/common-core-driver.ts <upstream> ghostty <new-output>
bun scripts/terminfo/common-core-driver.ts <upstream> ntilde <new-output> <sidecar> <build-version>
```

`COMMON_CORE_PREFLIGHT=1` selects a two-probe diagnostic run. Preflights are refused
by the publication reader and cannot enter the final comparison.

After each native run, retain the separate upstream identity guard result:

```sh
bun scripts/terminfo/common-core-identity.ts <upstream> <completed-run>
```

Verify an existing publication without desktop access:

```sh
node scripts/terminfo-common-core-publication.mjs --check
node scripts/terminfo-common-core-publication.mjs --check --historical
node --test scripts/terminfo/common-core-*.test.mjs
```

The adapter is a local commit, not an upstream release. To reproduce its exact Git
identity, fetch the recorded upstream base and then the retained Git bundle:

```sh
git fetch origin b0c8c62500199b381478a02b1f144a4de60693e5
git fetch <publication>/native-adapter.bundle HEAD
git checkout --detach FETCH_HEAD
```

The bundle records the actual local commits and requires that upstream base. The
source zip and readable patch allow independent inspection without executing the
bundle. A different source identity constitutes a new measurement.

## Focused movement retests

`COMMON_CORE_RETEST_FROM=<original-owned-run-directory>` selects every Core result
whose original status was invalid and whose recorded cause was changed native
window bounds. It refuses a different upstream identity, executable/payload or
shared worker. Missing controls, unsupported cases and unrelated IDs are never
selected. The retest has its own raw run, identity and cleanup records. Publication
includes every selected outcome, including any unsupported or inconclusive result,
and retains the original full-run results. Latest combined counts are labeled as
such, with original and retest evidence links on each affected row.

The measured driver/worker snapshots are retained inside each archive. Subsequent
launcher hardening (dirty-source refusal, atomic handoffs, startup cleanup and
absolute Git dispatch) is not retroactively attributed to those measurements. It
changes future orchestration, not the retained upstream fixtures, controls or
grades. A changed shared worker requires a new compatible measurement baseline.

## November 25 screenshot retests

The focused iTerm2 run selects all seven scrollback rows invalidated by the
static-frame check. Both native captures stay part of that check. The retained
adapter waits 250 ms before it samples a feature so the native renderer can
finish painting the cursor hide. The retest retains the original feature bytes
and verifies its source as a one-file extension to the full-run adapter.

The Ghostty retest selects the five November 25 timeouts that had supported
verdicts on November 11. The first retry used the full-run source and timed
out on the same five cases. Its archive and case records remain under
`ghostty/focused-retest/`.

The second retry uses a one-file derivative under `ghostty/timeout-retest/adapter/`.
The native capture adapter converts each CoreGraphics window snapshot to
JavaScript in bulk, then projects the same census fields. Both the pre-capture
and post-capture ownership and bounds checks still run for every screenshot.
It uses the same window selection, native PNGs, probe definitions, controls,
and 30-second deadline. Other timeouts remain in the full-run record.

The earlier Ghostty retry retained 21 to 23 screenshots per case before the
deadline fired. The four SGR cases had already received their DECRQSS replies.
On the same Mac, both census implementations produced identical output from
one 1,492-window snapshot. In seven alternating runs of each census command,
the median fell from 592 ms to 116 ms, including process startup. Each screenshot
still takes two fresh censuses. The five-case collection dropped from 165.6 to
68.8 seconds, including calibration. The alternate-screen case completed with
22 frames and verified the restored primary buffer and cursor. Each SGR case
completed with 27 frames and the upstream visual controls.

Use the extracted full run as COMMON_CORE_RETEST_FROM. The driver verifies the
original executable, worker and upstream source before launch. Set
COMMON_CORE_RETEST_KIND to capture-stability for iTerm2 or probe-timeout for
Ghostty's original retry. Use capture-throughput with the retained Ghostty
adapter snapshot to reproduce the faster census run. With the variable unset,
the driver uses the original movement retest.

## iTerm2 screenshot session custody

The iTerm2 custody retest selects the 44 Core cases that retained at least one
native screenshot in the original full run. This includes the seven previously
retested scrollback cases and any query-result case with a retained screenshot.
The earlier seven-case retry and the original full run remain archived.

Each screenshot acquisition in the local adapter checks that the named iTerm2
window contains one tab and one session, with the original worker's TTY. The
adapter checks again after a successful screenshot, before admitting the PNG
to calibration or a probe. Existing process, window ID and bounds checks
still apply. The same 250 ms feature-capture wait and original per-probe
deadlines apply. The bulk window census from the Ghostty work offsets the
extra window checks. Publication verifies the frame receipts against the
launch-recorded PID, window and worker TTY.

To reproduce the selection, extract the original iTerm2 full-run archive and
set `COMMON_CORE_RETEST_FROM` to that directory. Set
`COMMON_CORE_RETEST_KIND=capture-custody` and use the adapter snapshot in
`iterm2/custody-retest/adapter/`. The combined comparison includes all outcomes
from this retest.

Run `e66265a92483b4e9e9b34c9d6c684012` retained 895 custody-checked calibration
and probe frames. Of the 44 retests, 33 were supported and 11 inconclusive,
including nine timeouts. In particular, `reset.sgr` reached its 120-second
deadline after 102 captures. The original supported result and the new timeout
remain visible. Both application identity and owned-window cleanup verified.
The measured selector is retained in the run archive. Its later refactor
produces the same ordered case list when replayed over the original full run.

## iTerm2 SGR deadline follow-up

The custody run's nine deadline failures still had live screenshots. The six
single-attribute probes had also received their DECRQSS replies. Each feature
frame incurred the same 250 ms adapter sleep used for the cursor-hide stability
pair, even when the probe already measured paint freshness. The three saved
rendition probes stopped partway through their longer subset matrices.

The derivative adapter keeps the 250 ms sleep on the static pair and for outside
probes. The nine SGR probes use their existing paint-witness checks for every
other feature frame. The per-frame tab, session, TTY, window and bounds checks,
native pixels, probe definitions, controls, and deadlines still apply. Published
frame traces record the sleep policy alongside session custody.

Use the extracted iTerm2 custody-retest archive for `COMMON_CORE_RETEST_FROM`,
select `COMMON_CORE_RETEST_KIND=iterm-sgr-timeout`, and use the adapter snapshot
in `iterm2/sgr-timeout-retest/adapter/`. The offline publication rederives the
exact nine IDs from recorded timeout outcomes, carries forward the other 35
custody retest outcomes and links all three stages for each updated row.

Run `0ec264b9c06429af344d9b44e6e78b2e` graded all nine supported. The median
gap between successive captures within the same probe fell from 1,167 ms in
the custody run to 754 ms. Each of the three reset matrices completed all 132
frames. The run retained 568 probe frames and 11 calibration frames, with
verified application identity and cleanup. The combined iTerm2 count is
69 supported of 80. The earlier nine deadline failures remain in their
original archive.

## Ntilde screenshot follow-up

The original Ntilde run recorded 64 supported of 80. Nine SGR probes exceeded
an existing deadline while collecting frames. The three reset matrices each
stopped after 98 or 99 of 132 frames, and the six single-attribute probes had
already received their DECRQSS replies. In `cursor.hide`, the shown, hidden
and restored frames all had the same blank cursor cell. The original frames
cannot distinguish missed paint from loss of keyboard focus.

The derivative adapter converts the macOS window census in bulk. The existing
window identity and bounds checks still surround each capture. For
`cursor.hide`, the adapter also waits 250 ms and checks that the owned Ntilde
process is foreground before and after capture. Probe bytes, controls,
graders and deadlines are identical to the original comparison.

Run `b0e20a1117652fa6ee959e9765b97923` repeated just those ten cases on the
original Ntilde 0.14.0 payload at 41 × 140. The median gap between successive
frames of the same SGR probe fell from 1,211 ms to 412 ms. All three reset
matrices completed 132 frames. The cursor's shown and restored images matched,
and hiding it changed the same cell. All ten are supported. The combined
comparison is 74 supported of 80, with the original full-run results retained
in their own case records.

The six remaining rows have no decisive common-harness result. The three
history cases (`erase.screen.scrollback`, `scrollback.accumulate` and
`scrollback.total-lines`) need evidence outside the visible viewport.
`extensions.osc1-icon` needs icon-name readback, `reset.method` needs a backend
reset API and `device.env-term` has no upstream terminal callback in the pinned
suite.

To reproduce the selection, extract the original Ntilde full-run archive,
use it as `COMMON_CORE_RETEST_FROM`, set `COMMON_CORE_RETEST_KIND=ntilde-capture`
and use the adapter snapshot in `ntilde/capture-retest/adapter/`. The driver
verifies the original application payload before launching it. Run
`node scripts/terminfo-common-core-publication.mjs --check` with Node 24 to
rederive the combined report from the retained archives.

## Offline source proof and checkout formatting

`source-proof.zip` retains content-addressed Git commit/tree objects and the base
blobs needed to verify the local adapter. Verification reconstructs the bundle in
an isolated bare repository, checks its source ancestry and exact patch, binds each
source-archive member to the reported Git tree, proves that both upstream probe
subtrees are unchanged, and independently recomputes the recorded catalogue and
suite fingerprint. A proof boolean alone grants nothing.

The exact patch bytes are retained in `native-adapter.patch.gz`. The readable patch
and other generated text projections tolerate Windows checkout line endings;
immutable measurement JSON and archived source/capture bytes are still checked by
exact digest. No per-batch `.gitattributes` rule is needed.

Offline verification initializes the Git-verified upstream probe definitions without
executing feature callbacks, derives their terminal/library/mux membership, and
replays the retained upstream identity checker and measured-version resolver over
each raw run's replies. Consistent omissions or fabricated success receipts cannot
replace those executable-source checks. Node 24 supplies the native TypeScript
loader; verification needs no Bun installation, dependencies, desktop or network.
