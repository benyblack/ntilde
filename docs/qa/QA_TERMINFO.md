# Optional terminfo.dev desktop baseline

[terminfo.dev](https://github.com/beorn/terminfo.dev) collects external terminal
observations. Use it to find compatibility gaps and small reproductions. This is
a manual QA tool, outside the CI lanes. Ntilde's capability contract, parser
tests, replay tests and rendering tests remain the release evidence.

## Run

Requirements: Node 24+, npm, and a direct local Ntilde tab. Avoid tmux, Screen,
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
when available. On macOS/Linux the runner installs `terminfo.dev@4.0.0` into the
ignored `artifacts/terminfo/tool` directory on first use, with install scripts
disabled. On Windows, prepare that cache first in PowerShell:

```powershell
npm install --prefix artifacts/terminfo/tool --ignore-scripts --no-audit --no-fund --save-exact terminfo.dev@4.0.0
```

The runner checks the package version and bundled suite receipt/digest, then
executes the bundle with the same Node runtime as the runner. It removes the
inherited `__CFBundleIdentifier` and sets the collector's version hint to the
explicit argument, recording the original hints separately. This affects only
the child collector; the raw run retains upstream's `identity: unverified`.
Version hints and `TERM_PROGRAM` do not independently prove app identity.

Each invocation creates a private `artifacts/terminfo/run-*` directory containing:

- `raw.json`: the exact upstream run, including replies and inconclusive outcomes.
- `context.json`: runtime, supplied app version, original identity hints, geometry
  and checkout revision. The checkout revision does not identify the running app.
- `detect.json`, `suite-receipt.json`, and `package-lock.json`: collector identity,
  suite identity and resolved dependencies.
- `summary.json`: outcome/reason counts and the raw file's SHA256.

Keep the whole directory when comparing runs; do not edit raw JSON to correct
identity or support labels. A failed invocation retains available context and
originals. Results remain local; the runner does not submit anything upstream.

## Owned macOS adapter

To permit behavioral probes, launch a separate disposable desktop instance:

```sh
node scripts/terminfo-owned.mjs --executable /absolute/path/to/Ntilde --app-version 0.12.0
```

This runner requires macOS, Node 24+, and the pinned collector prepared above.
It runs from an ordinary shell; it does not need an existing Ntilde tab or agent
access enabled. Use a sidecar copy of the app to keep builds separate from the
running binary. Do not interact with the test window while it runs.

The adapter creates fresh settings and a worker profile in a private temporary
app-data root. Persistence, updates, hotkeys, clipboard writes and agent access
are disabled for that instance. It launches the exact executable with a unique
run marker. A private socket capability binds the worker to that launch; both
ends verify its live app parent, executable, user, controlling TTY and output
device. Geometry reads repeat the ownership/device check. Cleanup terminates
only the launched process and its verified worker and removes the private root.
Early exit and interruption retain failure evidence and still clean up.

The pinned CLI has no public Ntilde ownership hook. This is a **local collector
extension**, not an upstream-supported plugin. A digest-checked transformation
adds a distinct Ntilde ownership grant and disables CLI dispatch in a derived
module; it preserves the probe definitions, evidence grading and existing
policy gates. The downloaded original is unchanged. Unexpected bundle versions
are rejected. Raw results carry `collectorAdapter` with the extension ID and
original/derived bundle digests; do not submit them as stock CLI measurements.

Results go to `artifacts/terminfo/owned-*`, with initial settings, app/binary and
adapter hashes, the upstream receipt/lock, raw replies, summary, and measured
ownership/cleanup evidence. There is no screenshot or clipboard fixture yet.
Keep their checks inconclusive where evidence is unavailable. A collector
diagnostic or `suiteComplete: false` must remain visible; it is not a terminal
failure. See the [first owned run](terminfo/2026-10-30-macos-owned.md).

Adapter tests use Node's built-in test runner:

```sh
node --test scripts/terminfo/*.test.mjs
```

The real-app interruption test is opt-in. On macOS, select the disposable app
executable to exercise it as well:

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
