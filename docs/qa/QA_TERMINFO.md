# Optional terminfo.dev desktop baseline

[terminfo.dev](https://github.com/beorn/terminfo.dev) collects external terminal
observations. Use it to find compatibility gaps and small reproductions. This is
a manual QA tool, outside the CI lanes. Ntilde's capability contract, parser
tests, replay tests and rendering tests remain the release evidence.

## Run

Requirements: Node 24+, an npm installation (or a prepared collector cache),
and a direct local Ntilde tab. Avoid tmux, Screen,
SSH and the agent's own terminal: those measure a different path. Use a separate
tab, wait for shell startup prompts to finish, and do not type during collection.

Find the **running app's** version in About or its build metadata. A sidecar can
be older than the checkout; do not substitute the current Git revision or an
inherited `TERM_PROGRAM_VERSION`.

From the repository root, inside Ntilde:

```sh
node scripts/terminfo-baseline.mjs --app-version 0.12.0
```

Replace the example version with the actual version, including build metadata
when available. On macOS/Linux the runner prepares `terminfo.dev@4.0.0` in the
ignored `artifacts/terminfo/tool` directory on first use. It uses the checked-in
`collector-package.json` and `collector-package-lock.json` under
`scripts/terminfo`, with npm scripts disabled. Both inputs have pinned SHA-256
digests; a changed manifest, package version, archive URL or integrity digest
is refused before npm or the collector can run. Refresh these files and their
pins together when intentionally upgrading the collector.

Preparation executes `npm-cli.js` from the selected Node installation directly,
without searching `PATH`: `../lib/node_modules/npm/bin` from the Node executable
on Unix, or `node_modules/npm/bin` beside `node.exe` on Windows. For a different
installation, set `NTILDE_TERMINFO_NPM_CLI` to its absolute `npm-cli.js` path.
Both runners need that CLI for each run, including with a manually prepared
cache. To prepare or replace the cache manually on Unix:

```sh
mkdir -p artifacts/terminfo/tool
cp scripts/terminfo/collector-package.json artifacts/terminfo/tool/package.json
cp scripts/terminfo/collector-package-lock.json artifacts/terminfo/tool/package-lock.json
npm ci --prefix artifacts/terminfo/tool --ignore-scripts --no-audit --no-fund --include=prod --include=dev --include=optional
```

On Windows, prepare that cache first in PowerShell:

```powershell
New-Item -ItemType Directory -Force artifacts/terminfo/tool | Out-Null
Copy-Item scripts/terminfo/collector-package.json artifacts/terminfo/tool/package.json
Copy-Item scripts/terminfo/collector-package-lock.json artifacts/terminfo/tool/package-lock.json
npm ci --prefix artifacts/terminfo/tool --ignore-scripts --no-audit --no-fund --include=prod --include=dev --include=optional
```

Both runners check the package version and the full pinned suite receipt
(schema, probe hash, collector revision, manifest and bundle digests) before
retaining evidence. Both require the dependency lockfile before creating a
run directory. Each run creates a separate dependency installation using the
retained lockfile and `npm ci --ignore-scripts`; modified or partially upgraded
files in the prepared cache never execute. Only HTTPS package archives with
integrity digests are accepted, and installation failure or a changed lockfile
stops collection. The freshly installed bundle and receipt are checked again.
This may need registry access if npm's archive cache is cold. Runs do not change
one another's dependencies. Both retain the exact installed receipt and
lockfile bytes they validated. The inline runner executes the bundle with the
same Node runtime as the runner. It removes the inherited `__CFBundleIdentifier` and sets the collector's version hint to the
explicit argument, recording the original hints separately. This affects only
the child collector; the raw run retains upstream's `identity: unverified`.
Version hints and `TERM_PROGRAM` do not independently prove app identity.

Each invocation creates a private `artifacts/terminfo/run-*` directory containing:

- `raw.json`: the exact upstream run, including replies and inconclusive outcomes.
- `context.json`: runtime, supplied app version, original identity hints, geometry
  and checkout revision. Checkout metadata uses `/usr/bin/git` on Unix or the
  absolute `NTILDE_TERMINFO_GIT_EXECUTABLE` override; it is null when Git is
  unavailable (including Windows without an override). The checkout revision
  does not identify the running app.
- `detect.json`, `suite-receipt.json`, and `package-lock.json`: collector identity,
  suite identity and resolved dependencies.
- `summary.json`: outcome/reason counts and the raw file's SHA256.
- `collector/`: the run's manifest, lockfile and isolated dependency installation.
  This uses about 128 MB on macOS and stays with the inline evidence directory.

Keep the whole directory when comparing runs; do not edit raw JSON to correct
identity or support labels. A failed invocation retains available context and
originals. Results remain local; the runner does not submit anything upstream.

## Owned macOS adapter

To permit behavioral probes, launch a separate disposable desktop instance:

```sh
node scripts/terminfo-owned.mjs --executable /absolute/path/to/Ntilde --app-version 0.12.0
```

This runner requires macOS, Node 24+, npm, and the pinned collector prepared above.
It runs from an ordinary shell; it does not need an existing Ntilde tab or agent
access enabled. The executable must be an **uninstalled sidecar outside any
`.app` bundle**. Packaged macOS apps and aliases pointing into their bundles
are refused before launch. Do not interact with the test window while it runs.

Velopack can apply a staged update before GUI settings are loaded. On macOS its
restart loses the private `NTILDE_APPDATA_ROOT` and can open the real profile;
disabling automatic update checks in the fresh settings does not stop that
startup path. The adapter therefore accepts only standalone sidecars. This
also protects runs against older app builds without a dedicated QA startup
mode. The gate follows Velopack 1.2.0's
[macOS locator](https://github.com/velopack/velopack/blob/f2edcbcafb81da5b3c884aaea330e225ad91d8b6/src/lib-csharp/Locators/OsxVelopackLocator.cs)
and the [config storage contract](../CONFIG_STORAGE_CONTRACT.md#environment-variables);
revisit it when changing the pinned updater dependency. Installed-app update
behavior is outside this adapter's coverage.

The adapter creates fresh settings and a worker profile in a private fixture
under `~/.ntilde-qa`, including its isolated dependency installation, which is
removed during cleanup. It verifies that the parent is owned by the current user,
is not a symlink, and has no group/other permissions. A long home path that
cannot fit the macOS socket limit is rejected before launching the app.
Persistence, updates, hotkeys, clipboard writes and agent Act access are disabled
for that instance. Observe access is enabled only in this private app-data root
for the supplemental cell suite below. It launches the exact executable with a unique
run marker. A private socket capability binds the worker to that launch; both
ends verify its live app parent, executable, user, controlling TTY and output
device. Geometry and cell reads repeat the ownership/device check. Cleanup terminates
only the launched process and its verified worker and removes the private root.
Worker cleanup checks the recorded process identity and unique run marker even
if an app crash has reparented it. It waits up to two seconds after `SIGTERM`,
rechecks identity before escalating to `SIGKILL`, and waits another two seconds
for exit. Cleanup is recorded and the fixture removed only after the worker
has exited. If identity cannot be verified or termination fails, the run fails
and retains the fixture with cleanup-failure evidence; app and socket shutdown
are still attempted. Ownership replies share a four-second budget
for their process queries and observer RPCs within the existing five-second response timeout.
Early exit and interruption retain failure evidence and still clean up. Signal
handlers cover fixture creation through app exit, socket closure and fixture
removal, and remain installed for repeated signals during teardown.

The pinned CLI has no public Ntilde ownership hook. This is a **local collector
extension**, not an upstream-supported plugin. A digest-checked transformation
adds a distinct Ntilde ownership grant and disables CLI dispatch in a derived
module; it preserves the probe definitions, evidence grading and existing
policy gates. The downloaded original is unchanged. Unexpected bundle versions
are rejected. Raw results carry `collectorAdapter` with the extension ID and
original/derived bundle digests; do not submit them as stock CLI measurements.

Results go to `artifacts/terminfo/owned-*`, with initial settings, app/binary and
adapter hashes, the upstream receipt/lock, raw replies, summary, and measured
ownership/cleanup evidence. `collector-adapter.mjs` retains the exact derived
bundle after its private installation is deleted. `sources/` retains the runner,
collector transformation, ownership, worker, runtime, cell observer, suite and
ordering-barrier source files, plus the
pinned dependency inputs; context
hashes bind each snapshot to the run. `adapterSourceSha256` identifies the
collector transformation, while `runnerSourceSha256` identifies the launcher.
These copies support auditing archived runs after the checkout changes.
There is no screenshot or clipboard fixture yet.
Keep their checks inconclusive where evidence is unavailable. A collector
diagnostic or `suiteComplete: false` must remain visible; it is not a terminal
failure. See the [first owned run](terminfo/2026-10-30-macos-owned.md) and the
[refreshed before/after run](terminfo/2026-10-31-macos-owned.md).

### Supplemental ASCII cell observations

The owned runner also measures eight local text/cursor experiments through the
App's existing observe-only `readScreen` buffer projection. These are a separate
suite, `ntilde-owned-ascii-cells-v1`. They do not change the original collector's
probes, evidence requirements, grades or raw JSON. The inline runner does not
run this suite.

Discovery is restricted to the private fixture's descriptor and socket, with
the launched PID, current user and private filesystem permissions checked. The
fixture must have exactly one local, non-windowless pane with its unique worker
profile. The worker enters the alternate screen and writes a fresh run nonce;
the parent binds that nonce and cursor position to the observed pane before
accepting experiment snapshots. Every read checks pane identity and dimensions
before and after the snapshot. No normal app endpoint is consulted.

Each write ends with a fresh DA query/reply as an ordering barrier. This reply
does not establish text or cursor behavior: the parent's actual before/after
snapshots supply those assertions. Each case has a known control, and expected
unaffected rows must remain intact. A bad control is inconclusive; a qualified
mismatch is a failed local assertion, not a stock `unsupported` grade. The suite
covers ASCII writes, overwrite, absolute cursor movement, CUP with origin mode
off/on and a scroll region, EL 0/1/2, and insert followed by replace mode.

Only viewports of 3–128 rows and 48–256 columns are accepted, with at most 64
snapshot attempts and 512 KiB per response. Wide/combining characters, colors,
attributes, glyph pixels and scrollback are outside this suite. `readScreen`
trims trailing whitespace; blank cells are asserted through that projection.
The worker restores its input mode and the main-screen text, cursor and cursor
visibility must match the initial snapshot before collection can complete.

Additional files in each owned evidence directory:

- `cell-snapshots.jsonl`: the parent's incremental snapshot journal.
- `cell-observations.json`: the complete snapshots, controls and derived local
  assertions. It stays provisional until cleanup completes.
- `cell-receipt.json`: written only after worker/app shutdown and private fixture
  deletion. It binds the report, context, raw collector run and ownership proof
  with SHA-256 hashes. Failed or interrupted runs have no completion receipt.

Keep these with the other run evidence. The
[2026-11-02 cell measurement](terminfo/2026-11-02-ascii-cells.md) records the first
successful run and its limits.

Adapter tests use Node's built-in test runner:

```sh
node --test scripts/terminfo/*.test.mjs
```

The portable cell, observer, ordering-barrier and runtime helper tests also run
in CI's script self-tests. CI does not launch a desktop or install the collector;
Unix filesystem checks are explicitly skipped on Windows.

The changed-dependency regression runs when the cache is prepared and
`NTILDE_TERMINFO_NPM_CLI` explicitly selects a usable npm CLI. The real-app
interruption test is opt-in. On macOS, select the disposable app
executable to exercise it as well, with a connected, awake desktop display:

```sh
NTILDE_TERMINFO_TEST_EXECUTABLE=/absolute/path/to/Ntilde node --test scripts/terminfo/*.test.mjs
```

## Interpret

The ordinary inline CLI refuses most state-changing checks without its own
verified disposable-terminal ownership. Opening a fresh Ntilde tab does not
satisfy that upstream requirement. Keep policy refusals inconclusive; do not
forge an ownership receipt or count them as failures.

Likewise, a missing reply does not establish lack of support. A mode report or
graphics-query reply establishes only that response, not rendering, input, or
the mode's complete behavior. `suiteComplete` means the suite recorded its
scheduled outcomes; it does not mean all features were tested successfully.
See the [upstream measurement guide](https://github.com/beorn/terminfo.dev/blob/main/docs/contribute.md).

Investigate discrepancies against the exact running version first. Promote a
confirmed problem into a minimal deterministic test in its owning assembly;
only then change a capability claim. Do not automatically import these results
into `vt-capabilities.json` or `vt_coverage_matrix.md`.

The [first macOS baseline](terminfo/2026-10-30-macos.md) records the pilot and its
limitations. Changing the pinned collector requires a fresh run and review of
its suite receipt and schema; results from different suites are not directly
comparable as scores.

Latest runtime-color follow-up: [2026-10-31 owned color-stack measurement](terminfo/2026-10-31-color-stack.md)
and [derived digest-bound comparison](terminfo/2026-10-31-color-stack-comparison.json).
It measured 73 supported / 5 unsupported / 177 inconclusive observations; the
report separates query evidence from state, replay and pixel verification.

Latest alternate-key follow-up: [2026-11-01 owned measurement](terminfo/2026-11-01-alternate-keys.md)
and [derived comparison](terminfo/2026-11-01-alternate-keys-comparison.json).
It measured 74 supported / 4 unsupported / 177 inconclusive observations. The
changed collector result proves negotiation; supplied-layout input and managed
transport tests provide separate delivery evidence with explicit layout limits.

Latest all-key follow-up: [2026-11-01 owned measurement](terminfo/2026-11-01-all-keys.md)
and [derived comparison](terminfo/2026-11-01-all-keys-comparison.json).
It measured 75 supported / 3 unsupported / 177 inconclusive observations.
The changed query establishes flag-8 negotiation; routed-event and GUI/mux tests
separately cover emitted frames and old-peer text fallback. Flag 16 remains masked.

The [2026-11-01 associated-text follow-up](terminfo/2026-11-01-associated-text.md)
measured 76 supported / 2 unsupported / 177 inconclusive observations. Flag 16
negotiation is separate from routed actual-text, Unicode, GUI/mux and SSH input
boundary tests; the report records their scope and remaining limits.

The [2026-11-01 text-sizing follow-up](terminfo/2026-11-01-text-sizing.md)
measured 77 supported / 1 unsupported / 177 inconclusive observations. The owned
probe verifies OSC 66 cursor advance; separate domain, state/replay, resize and
shared-renderer pixel tests cover the implemented geometry and appearance.

The [2026-11-01 clipboard follow-up](terminfo/2026-11-01-clipboard.md) measured
78 supported / 0 unsupported / 177 inconclusive observations. The changed probe
establishes mode-5522 recognition only; binary transfers, permission boundaries
and paste grants have separate test-owned backend evidence. The collector
measurement does not qualify native cross-app clipboard interoperability. The
separate [native clipboard qualification](clipboard-native.md) exercises real
AppKit/Win32/GTK interchange on disposable CI desktops; [session-only read
approvals](clipboard-permissions.md) are documented separately. Neither regrades
these collector observations.

The [2026-11-02 inconclusive-observation triage](terminfo/2026-11-02-inconclusive-triage.md)
and [machine-readable inventory](terminfo/2026-11-02-inconclusive-triage.json) cover
all 177 unchanged observations, with a next measurement and source context for
each: 152 evidence-limited, 22 missing matching replies, and three deliberately
unsent clipboard probes. Source limitations and measurement gaps are kept
separate from original grades. Validate the inventory offline with
`python3 scripts/terminfo-triage.py --check`; no private artifact or live terminal
is needed for that check.
