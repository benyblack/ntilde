# Optional terminfo.dev desktop baseline

The current owned driver adds a sixteenth, bounded exact-query suite under
schema 11/fixed 74-source policy v4. Its packet/state/ownership contract is
[the owned query model](terminfo/owned-query-model.md). The preceding calibrated
static-image suite and frozen v10 publication retain their original 67-source
scope; published v9 input and v8 observations likewise keep their original bytes.


[terminfo.dev](https://github.com/beorn/terminfo.dev) collects external terminal
observations. Use it to find compatibility gaps and small reproductions. This is
a manual QA tool, outside the CI lanes. Ntilde's capability contract, parser
tests, replay tests and rendering tests remain the release evidence.

The [80-row Core TUI evidence map](terminfo/2026-11-04-core-baseline.md)
reconciles the frozen stock results, retained owned variants, source/test references
and remaining acceptance boundaries. Its machine-readable companion preserves
original grades and case pointers; additional evidence is not a combined compliance
score. Reproduce it offline with `python3 scripts/terminfo-core-report.py --check`.

The [collector completeness diagnosis](terminfo/collector-completeness.md)
reproduces the pinned reverse-wrap geometry-declaration error offline and explains
why the retained stock run remains partial even when supplemental suites finish.

## Run

Requirements: Node 24+, an npm installation (or a prepared collector cache),
and a direct local Ntilde tab. Avoid tmux, Screen,
SSH and the agent's own terminal: those measure a different path. Use a separate
tab, wait for shell startup prompts to finish, and do not type during collection.

"Direct" also means **not a persistent (mux-backed) session**. Persistent panes
answer some probes differently by design: they refuse non-Sixel images, so the
Kitty graphics query replies `ERR` (#838), and they report OSC 5522 MIME
clipboard mode as unrecognized. Turn persistence off for the measured tab, or
record that the tab was persistent.

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
Git attributes preserve LF bytes for both pinned inputs on Windows as well;
validation never normalizes a modified cache to make its digest pass.

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

**Native Windows is not supported by the pinned collector.** `terminfo.dev@4.0.0`
(the latest published version as of 2026-11-04) refuses to write results on
`win32`. Its `openControllingTTY` throws "JSON/file collection requires a proved
Windows controlling TTY" whenever `--output` or `--json` is used, and the runner
always passes `--output raw.json`. The runner therefore refuses native Windows
before creating a run directory, preparing a cache or starting the collector,
and points here. Preparing a cache on native Windows does not help. Revisit this
when the pinned collector version changes.

### Windows: collect through WSL

To measure the Windows build of Ntilde, run the inline baseline from WSL inside
an Ntilde tab. The bytes still pass through Ntilde's ConPTY path; inside WSL,
Node runs as Linux and finds a normal controlling TTY.

1. In a direct (non-persistent) PowerShell tab in Ntilde, pass Ntilde's own
   identity hints into WSL. WSL drops Windows environment variables that are
   not listed in `WSLENV`, and the runner refuses to start without
   `TERM_PROGRAM=Ntilde`:

   ```powershell
   $env:WSLENV = "TERM_PROGRAM/u:COLORTERM/u"; wsl
   ```

2. In WSL, use a checkout on the Linux filesystem (for example `~/ntilde`),
   not `/mnt/c` or `/mnt/d`; the runner writes private files with Unix modes.
   Install Node 24+ inside WSL (for example with nvm, so npm sits beside
   `node`), then check that `echo $TERM_PROGRAM` prints `Ntilde`.
3. Run the usual command. On Linux the runner prepares its own cache:

   ```sh
   node scripts/terminfo-baseline.mjs --app-version 0.13.0
   ```

The raw run records `os: linux` and the WSL kernel as `osVersion`, because that
is what Node reports. Do not edit it. Record the Windows build, the WSL
distribution, whether the tab was persistent, and the Ntilde build separately.
The [2026-11-04 Windows and Linux runs](terminfo/2026-11-04-windows-linux-inline.md)
used this path.

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

This runner requires an awake macOS desktop, Node 24+, npm, and the pinned collector prepared above.
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
Persistence, updates, hotkeys and clipboard writes are disabled for that instance.
Observe access is enabled only in this private app-data root. The v6 runner also
enables Act access there for the parent-selected [resize fixture](terminfo/owned-resize.md);
its ordinary cell observers remain read-only. The v1–v5 runner left Act disabled. It launches the exact executable with a unique
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

### Supplemental SGR flags and palette indices

After the ASCII suite restores the main screen, the same owned worker runs
`ntilde-owned-sgr-indices-v1` in a fresh alternate-screen fixture. A separate
`NTILDE_SG_` nonce binds it to the same pane; process/PTY ownership, singleton
profile, geometry, DA ordering and restore checks apply to every snapshot.
Observe access requests the App's existing `readScreen(includeAttributes=true)`.
These SGR requests remain read-only. The v6 runner enables Act only in the private
instance for its later source-selected resize actions.

Seventeen experiments measure bold, italic, underline-enabled and inverse flags;
SGR 22/23/24/27 selective resets; SGR 0 for those flags and indexed colors;
foreground/background standard and bright indices; seven sample indices for
each `38;5`/`48;5` path; and SGR 39/49 default markers. Selective resets must
preserve unrelated styles/colors and previous cells. Every control and target
checks the complete ASCII viewport, including unaffected blank cells.

The App supplies one `FLAGS:FG/BG|` token per ASCII cell. The parent validates
all rows/columns, 16-bit flags and indices from -1 through 255, then records a
lossless row-major run-length encoding: `attributeRuns` entries contain `count`,
`flags`, `fg` and `bg`. Runs can cross row boundaries; their counts must cover
exactly `rows * cols`. This retains every cell without repeating thousands of
identical default tokens. Each suite keeps its own 64-snapshot limit.

`sgr-snapshots.jsonl` journals those parent-owned snapshots;
`sgr-observations.json` retains the complete observations. The final
`cell-receipt.json` additionally binds the SGR report's hash and counts under
`sgrEvidence`, so both suites finalize with one cleanup receipt. Their sources,
including the attribute decoder, are archived and hashed with the run.

Only flags and palette indices are visible in this API projection. RGB values,
underline style/color metadata, appearance, blink timing and font rendering
remain unmeasured. In particular, a default marker is not a measured default
RGB value, and an underline flag is not proof of a particular painted style.
The [2026-11-02 SGR measurement](terminfo/2026-11-02-sgr-indices.md) records the
first run. Neither supplemental suite changes stock grades or the frozen triage.

### Supplemental RGB and underline metadata

After SGR restoration, `ntilde-owned-rgb-underline-v1` binds a fresh `NTILDE_RG_`
nonce to the same pane and requests `readScreen(includeCellDetails=true)`.
The default read remains unchanged. This optional projection is captured with
text, geometry and cursor state under one buffer read lock. It includes every
physical cell, including wide continuations, as canonical row-major runs.
The API refuses viewports exceeding 8,192 cells or 1,024 runs with
`screenDetailsUnavailable`; it never returns a partial projection.

Each run contains `count`, flags without the transient Dirty bit, `fg`/`bg`
indices, optional literal `foregroundArgb`/`backgroundArgb`, `underlineStyle`
(0 off, 1 single, 2 double, 3 curly, 4 dotted, 5 dashed),
`underlineColorIndex` and optional `underlineArgb`. Literal values are unsigned
packed ARGB, alpha in the high byte. Default/palette colors omit ARGB; an
underline with index -1 and no ARGB inherits the text color. These are stored
buffer values. They do not resolve palette/default colors, inverse, faint,
selection, theme changes or rendered pixels.

Twenty experiments measure foreground/background truecolor in semicolon and
both colon forms, black/white/component edge values, selective 39/49 resets,
five underline styles plus SGR 21, SGR 58 indexed and RGB colors, SGR 59,
SGR 24 and SGR 0. Every control and target checks all cells, text and cursor;
selective resets preserve preceding cells and unrelated metadata. Bad controls
remain inconclusive; a qualified mismatch is a failed local assertion.

`rgb-snapshots.jsonl` retains the 48 parent-owned frames and
`rgb-observations.json` retains the derived report. The one final
`cell-receipt.json` binds this report under `rgbEvidence`, alongside ASCII and
SGR evidence, only after verified worker exit and fixture removal. Suite and
decoder sources are archived and hashed. Each suite retains a separate
64-snapshot ceiling. DA replies order operations; they do not prove colors.
Stock collector definitions, grades and the frozen 177-item triage are unchanged.
If the first requested RGB baseline returns `screenDetailsUnavailable`, the
worker has not started that alternate-screen fixture. The parent records a
hashed `rgb-unavailable.json` with no fabricated experiment outcomes, then
continues the stock collection. The final receipt marks `rgbEvidence.status`
as `unavailable` only after normal ownership and cleanup verification. Other
errors, or any refusal after fixture entry, still fail the run and attempt
restoration; a missing/partial projection is never treated as a pass.

The [2026-11-02 RGB/underline measurement](terminfo/2026-11-02-rgb-underline.md)
retains the first qualified run and its limits.

### Supplemental remaining rendition, charsets and cursor modes

After RGB collection restores (or records its initial typed budget refusal),
the worker runs `ntilde-owned-rendition-charsets-v3`: 19 experiments and 50
parent-owned frames. Three additional experiments observe SGR 53/55, independence
from underline style/color and SGR 24, and SGR 0 reset. The explicit
`readScreen(includeCellDetails=true, includeOverline=true)` opt-in includes a
boolean per run. Without it, overline is omitted from both values and run
boundaries, preserving canonical projections for older clients. These observations
prove stored state; focused renderer tests separately exercise the painted top stroke. These measure faint, hidden, strikethrough and blink flags;
their selective resets and SGR 0; BMP, combining, wide and supplementary Unicode;
DEC line drawing/special graphics; and G0/G1 SI/SO switching. Complete physical
cell details include wide-continuation and extended-text flags. Combining and
wide cases use independent cursor-width expectations. Each charset sequence
restores ASCII designation and shift, then writes lowercase probes to verify it.
The first retained v1 mapping measurement predates those post-restoration probes
and remains reproducible with its explicitly selected suite revision. The retained
v2 measurement also remains reproducible, with its original 16 experiments and
40 frames; it contains no overline observations. Hidden text is
recorded buffer content; blink is a stored flag. Neither proves painted output.

The [2026-11-03 overline measurement](terminfo/2026-11-03-overline.md) retains
the qualified v3 run and its limits.

`ntilde-owned-cursor-modes-v1` follows with nine experiments and 22 frames.
`readScreen(includeCursorMode=true)` adds an optional `cursorMode` object with
stored `style` (0 block, 1 bar, 2 underline), `blink` and `isDefault`. It shares
the snapshot read lock and remains omitted by default. `isDefault=true` means
the host may use its configured presentation; the numeric style is not a
resolved UI preference. This readback does not measure blink phase or pixels.

The cursor suite requires an empty default-attribute main viewport at home,
visible cursor, and stored default underline/blinking modes before any writes.
It tests visibility, all DECSCUSR parameters 0..6 and an ignored invalid 7.
Every control normalizes to explicit steady block. Leaving the alternate screen
and sending DECSTR restores the recorded default cursor modes without clearing
main-screen text. Initial/restored snapshots must match completely, including
cursor modes; the reset is used only inside this verified private fixture.
This does not qualify every saved mode, history field or reset protocol.

Both suites preserve the same pane, ownership, nonce, ordering and cleanup
checks. Reports/journals use `rendition-*` and `cursor-*` filenames; the shared
final receipt additionally binds `renditionEvidence` and `cursorEvidence`.
The production receipt finalizer rehashes every required suite report/marker,
binds each to the same run, refuses remaining fixture entries and never overwrites
a final receipt. Its portable tests run in CI without a desktop or collector.

Initial `screenDetailsUnavailable` refusals produce separate hashed unavailable
markers, no invented outcomes, and permit subsequent suites/stock collection.
Unrelated errors and any refusal after fixture entry remain fatal.

Overline now has VT state and an App rendering path, with stored state qualified
by the [v3 measurement](terminfo/2026-11-03-overline.md). No constant or
consumed-input hint substitutes for an observation. Stock grades and frozen
triage stay unchanged. The live frames qualify stored state and decoded cells;
controlled pixel observations are now retained in the [owned pixel suite](terminfo/owned-pixels.md);
synthetic blink phases do not measure desktop cadence. Live
Windows/Linux qualification remains #775. The earlier
[2026-11-02 rendition/cursor measurement](terminfo/2026-11-02-rendition-cursor.md)
retains its historical scope and exact limits.

### Optional editing-state readback

[Editing-state readback](terminfo/buffer-observations.md) adds an explicit raw
AgentHost opt-in for margins, origin/wrap/insert modes, pending wrap, active
screen and exact local scrollback count under the text/cell snapshot lock.
Windowless history count is explicitly unknown, not inferred from its viewport
import. These facts support extended editing fixtures; they do not by themselves
qualify protocol behavior or change stock grades.

### Supplemental runtime color resets and queries

The [owned color suite](terminfo/owned-colors.md) follows the fixed pixel suite.
It adds ten baseline/override/reset experiments, 35 parent-owned PNGs, exact
fenced OSC 4/10/11/12/17/19 replies and independent OSC 104/110/111/112/117/119
reset checks. Selection colors use an isolated synthetic render selection.
An unset selection default is an explicit absent reply, not unsupported.
Runtime colors are cleared explicitly before alternate-screen exit because
DECSTR does not restore them. Receipt v3 preserves the six prior bindings and
adds re-derived color evidence after verified cleanup. The
[2026-11-03 measurement](terminfo/2026-11-03-colors.md) retains all ten passing
comparisons and original PNGs. This is supplemental
owned offscreen evidence; stock collector grades stay frozen.

### Supplemental erasure and editing

The [owned erasure/editing suites](terminfo/owned-editing.md) follow runtime
colors with 35 bounded ASCII experiments and 76 paired logical frames. They
observe every relevant cell, cursor, margins/modes and actual nonempty history;
ED3 requires an independently seeded history control. Receipt v4 re-derives both
reports and preserves all seven preceding bindings after verified cleanup.
The [2026-11-03 before/after measurement](terminfo/2026-11-03-editing.md)
retains three original zero-count failures and all 35 passing comparisons after
the focused parser fix. These are supplemental observations; stock grades remain unchanged.

### Supplemental history and scrolling

The [owned history suite](terminfo/owned-history.md) adds 30 ASCII comparisons
and 63 paired frames after erasure/editing. It measures actual ordered history,
main/alternate scrolling, reverse index and margins, including origin-relative
homing. Receipt v5 retains all nine prior bindings and requires an independently
observed private-PTY output-mode proof. Only the proved disposable PTY has output
translation disabled for owned suites; its previous mode returns before stock
collection. This prevents bare LF from becoming CRLF in transport. Resize/reflow
and history attributes remain separate work; stock grades stay unchanged. The
[2026-11-03 measurement](terminfo/2026-11-03-history.md) retains all 30 passing
comparisons, exact source provenance and the parent-observed output-mode proof.

### ASCII/SGR benchmark qualification grading

The [ASCII/SGR qualification batch](terminfo/2026-11-05-ascii-sgr-qualification.md)
grades exactly four pinned cases — `text.basic`, `text.overwrite`, `sgr.bold`
and `sgr.italic` — through an explicitly versioned collector extension
(`ntilde-owned-ascii-sgr-grading-v1`). The extension is a second digest-checked
derivation on top of the owned adapter: the pinned bundle and the owned adapter
stay byte-identical, and derivation refuses unless the bundle still contains the
exact four probe definitions.

The worker replays each case's exact pinned termless fixture bytes through the
owned fence and reads the resulting cells/flags through the parent-journaled
`readScreen` observer. The extension then executes the pinned probe's own
termless code over those observed screens, so the collector's criteria — not a
re-implementation — produce the outcome. An independent contract grader must
agree before any conclusion is published, and the parent replays the pinned
grading a second time over the retained journal. Graded results carry the
extension identity and are never stock CLI output; the owned-adapter collector run
keeps its own identity, grades and inconclusive four-case outcomes.

`unsupported` requires a measured mismatch behind a valid control (the pinned
notes, such as "Control already contained Hello", keep failures inconclusive).
Missing captures, wrong input bytes, changed observations, mismatched run
identities and incomplete restoration all prevent a conclusive grade. Receipt
v13 re-derives the graded report, run log, adapter bytes and journal after
verified cleanup. The bold/italic conclusions cover the stored cell flags the
pinned scorer demands — the pin's own terminal path states "Cursor advance does
not verify SGR styling" — and explicitly do not claim glyph appearance; that
remains the separate pixel scope in [bold-glyphs.md](terminfo/bold-glyphs.md).

### SGR reset and standard-color qualification grading

The [SGR reset/standard-color batch](terminfo/2026-11-06-sgr-colors-qualification.md)
grades exactly five more pinned cases — `sgr.selective-reset.bold`,
`sgr.selective-reset.italic`, `sgr.reset`, `sgr.fg.standard` and
`sgr.bg.standard` — through a second layered, explicitly versioned collector
extension (`ntilde-owned-sgr-colors-grading-v1`) derived digest-checked over
the first grading extension. The three reset cases replay the pinned
`C${setup}X${reset}Y` fixtures byte-exactly and require the pinned
`measuredReset` code, the independent contract grader and the parent-side
replay to agree on every field; where the stricter contract grader measures a
mismatch the pinned criterion cannot see (for example color drift), both
verdicts are recorded and the conclusion follows the stricter grader. The two standard-color cases verify the exact
stored palette indices and unaffected default markers; the pinned RGB
criterion stays at its own indistinguishable-inconclusive verdict because the
stored projection deliberately exposes no theme-resolved RGB — no index is
inferred from appearance. Receipt schema 14 binds the layered report, run log
and adapter bytes after verified cleanup; source policy v7 carries the
113-file inventory with every earlier policy mapping frozen.

The measured SGR-colors run recorded contracts v1; the current contracts are
v2 (setup-cell color drift invalidates selective-reset controls), and v1
evidence rederives with its own revision. The collector run beside the graded
suites is owned-adapter output with unchanged SGR scoring, not unmodified
stock CLI bytes.

### SGR standard-color RGB-appearance qualification grading

The [SGR-RGB batch](terminfo/2026-11-07-sgr-rgb-qualification.md) finishes the
named-color criterion's remaining scope — exactly `sgr.fg.standard` and
`sgr.bg.standard` — through a third layered, explicitly versioned collector
extension (`ntilde-owned-sgr-rgb-grading-v1`) derived digest-checked over the
SGR-colors extension. The App observer gained an opt-in `readScreen`
projection (`includeResolvedColors`) that resolves each cell's
foreground/background through the draw path's own storage-to-theme precedence
(default → theme default, palette index → ANSI palette entry, literal →
stored ARGB) against one coherent render snapshot, and reports the resolved
runs together with the palette/theme binding (theme name, defaults, 256-entry
palette, override flags, DECSCNM, minimum-contrast setting) they were
resolved against. Resolution stays in the App's rendering layer; the VT core
is unchanged.

The two fixtures replay byte-identically to the stored batch
(`C\x1b[31mX\x1b[34mY` and `C\x1b[41mX\x1b[44mY`), the pinned
`namedColorProbe` code executes over the RGB-carrying projection, and the
conclusion requires the pinned code, the independent contract grader and the
parent-side replay to agree. The pinned distinguishability semantics are
preserved exactly: a theme whose entries alias stays inconclusive ("Baseline,
theme colors, or color readback were indistinguishable"), never unsupported,
and arbitrary resolved values cannot pass — every run must cohere with the
binding's palette lookups and with the stored attribute runs of the same
snapshot, and one binding must cover the whole suite (a palette/theme change
during observation voids the run). The RGB conclusions claim the resolution
stage only: no contrast adjustment, opacity compositing or glyph
rasterisation, and the stored-index conclusions remain the SGR-colors
suite's. Receipt schema 15 binds the layered report, run log and adapter
bytes after verified cleanup; source policy v9 carries the 123-file
inventory (six new scripts plus the render-resolution C# reference inputs)
with every earlier policy mapping frozen.

The batch carries one explicitly attributed cherry-pick (`8ffc6d19`, the
private-clipboard wide-column observer fix from
`codex/terminfo-benchmark-refresh`): current main cannot complete the shared
owned pipeline's cleanup receipt without it, while the four ASCII/SGR cases
themselves do not depend on it. See the
[measurement](terminfo/2026-11-05-ascii-sgr-qualification.md) for the
retained failure evidence and the pristine-main discrimination run.

### SGR attribute qualification

The [SGR attributes batch](terminfo/2026-11-08-sgr-attributes-qualification.md)
qualifies `sgr.underline.single`, `sgr.inverse`, `sgr.faint`, `sgr.hidden` and
`sgr.strikethrough` through the layered `ntilde-owned-sgr-attributes-grading-v1`
extension over the SGR-RGB one. The pinned `sgrProbe` code runs over parent-observed
stored flags (plus the stored underline style) with neutral-baseline,
unrelated-drift, DECSCNM and reset-restoration controls; the contract grader may
grade unsupported only after valid controls and never more permissively than the
pinned probe. Claims are stored state only, not rendered appearance. Receipt
schema 16 and source policy v10 (128 files) bind it; earlier policies stay frozen.

### SGR palette-color qualification

The [SGR palette batch](terminfo/2026-11-08-sgr-palette-qualification.md)
qualifies `sgr.fg.bright`, `sgr.bg.bright`, `sgr.fg.256`, `sgr.bg.256`,
`sgr.fg.default` and `sgr.bg.default` through the layered
`ntilde-owned-sgr-palette-grading-v1` extension over the SGR-attributes one. The
pinned `namedColorProbe`, `indexedColorProbe` and `defaultColorProbe` code runs over
parent-observed cells that carry stored palette indices, default markers and literal
bytes together with App-resolved RGB bound to one recorded palette/theme state.
Stored storage, resolved RGB and rendered appearance stay distinct layers; rendered
appearance is not claimed. Controls cover the unaffected channel, unrelated
attributes, DECSCNM, bold-versus-bright and palette boundary indices; the contract
grader may grade unsupported only after valid controls and never more permissively
than the pinned probe. Receipt schema 17 and source policy v11 (133 files) bind it;
earlier policies stay frozen.

Adapter tests use Node's built-in test runner:

```sh
node --test scripts/terminfo/*.test.mjs
```

The portable cell, SGR, RGB/underline, decoder, observer, ordering-barrier and runtime helper tests also run
in CI's script self-tests. CI does not launch a desktop or install the collector;
Unix filesystem checks are explicitly skipped on Windows.

The changed-dependency regression runs when the cache is prepared and
`NTILDE_TERMINFO_NPM_CLI` explicitly selects a usable npm CLI. The real-app
interruption test is opt-in. On macOS, select the disposable app
executable to exercise it as well, with a connected, awake desktop display:

```sh
NTILDE_TERMINFO_TEST_EXECUTABLE=/absolute/path/to/Ntilde node --test scripts/terminfo/*.test.mjs
```

### SGR truecolor qualification

The [SGR truecolor batch](terminfo/2026-11-08-sgr-truecolor-qualification.md)
qualifies exactly `sgr.fg.truecolor` and `sgr.bg.truecolor` through the layered
`ntilde-owned-sgr-truecolor-grading-v1` extension over the SGR-palette one. The
pinned `truecolorProbe` code runs over parent-observed cells carrying stored literal
ARGB bytes (palette index and default marker proven cleared) together with
App-resolved RGB bound to one recorded palette/theme state. Only the semicolon
`38/48;2;r;g;b` form the pinned fixture exercises is qualified; the related
`extensions.truecolor` case, colon forms and colorspace ids are separate variants and
are not claimed. Controls cover literal-versus-palette storage with equal resolved
appearance, boundary components, the unaffected channel, unrelated attributes
(including overline and underline color) and DECSCNM; rendered appearance is not
claimed. The VT parser snaps five exact truecolor values (e.g. `12,12,12`, `204,204,204`) to palette
indices; they are outside this qualification (see the batch's known-limit section). Receipt schema 18 and source policy v12 (138 files) bind it; earlier
policies stay frozen.

### Truecolor-literal finding (not a terminfo.dev case)

The [truecolor-literal finding](terminfo/2026-11-08-truecolor-literal-finding.md)
covers five exact direct-RGB values the VT parser used to rewrite into palette indices.
It is an Ntilde finding with its own suite (`ntilde-owned-truecolor-literal-finding-v1`,
ids `finding.truecolor-literal.*`): no pinned probe, no collector extension, no stock
grade. It records a before run (main's App: all ten cases unsupported) and an after run
(the corrected parser: all ten supported), each with an explicit indexed positive
control, an OSC 4 mutation of the aliased entry and its restoration. Receipt schema 19
and source policy v13 (144 files); earlier policies stay frozen.

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

The [revision-2 charset restoration measurement](terminfo/2026-11-02-rendition-cursor-restoration.md)
adds lowercase post-reset probes and the portable production receipt finalizer.
The original v1 mapping measurement remains unchanged.

## Controlled capture and owned pixel observations

[Controlled offscreen capture](terminfo/controlled-capture.md) defines the optional synthetic
clock and actual render metadata used by the [owned pixel suite](terminfo/owned-pixels.md).
It follows the five stored-state suites, retains 40 controlled PNG frames and evaluates 25
local comparisons. The new version-2 cleanup receipt re-derives the pixel report and
rechecks every image after verified cleanup; historical version-1 receipts remain unchanged.
Existing stock grades and retained cell/query observations are unchanged.


The controlled-capture API also records cursor/selection/palette override evidence
and distinguishes nullable theme selection defaults from derived selection paint.
Its optional AgentHost `syntheticSelection` paints a validated viewport span in an
offscreen image without changing live selection or terminal state. The
[owned color suite](terminfo/owned-colors.md) now uses these facts for #764; its
[retained measurement](terminfo/2026-11-03-colors.md) records ten passing reset/query/pixel
comparisons. No stock observations are regraded.

### Supplemental owned resize/reflow

The [owned resize suite](terminfo/owned-resize.md) follows history observations.
It retains twelve paired full-state/history frames and six source-selected App
actions with independently measured kernel geometry. It checks width, height and
combined shrink/restore, including Ntilde's documented top-anchor policy and
conservation of every hard line across history plus viewport. V6 receipts retain
all preceding bindings and require the new action/source/settings proof. The
stock collector and frozen grades are unchanged.

The [2026-11-03 committed-source measurement](terminfo/2026-11-03-resize.md)
records three passing comparisons at 40 × 140 cells, with twelve paired frames,
six exact App/kernel action proofs and verified completed cleanup.

### Supplemental owned saved-state and alternate screens

The [saved-state suite](terminfo/owned-saved-state.md) follows restored resize
observations. Nine staged cases retain 57 paired active-state/history frames and
new output witnesses for saved pens and character sets. V7 receipts require all
preceding bindings, the source-defined stage order, archived sources and verified
cleanup. Setup does not depend on the RIS operation under test; the earlier cell-clearing
negative stays frozen for #773. No new stock grade is inferred.

The [2026-11-03 saved-state measurement](terminfo/2026-11-03-saved-state.md)
retains eight passes and one controlled RIS cell-clearing negative for #773,
with all nine controls passing and a completed v7 receipt.

The [2026-11-04 fresh reset measurement](terminfo/2026-11-04-reset-observations.md)
records nine passing saved-state cases and 57 paired frames after the owning
screen-reset fix. Its complete 165-file archive retains all thirteen reports,
75 PNGs, the captured 52-source graph and a completed v8 cleanup receipt.
The earlier RIS negative is unchanged. Four new reply-based collector outcomes
(18t, DA3 and Kitty transmit/placement acknowledgements) produce a separate
82/0/173 raw summary; the frozen 78/0/177 baseline and ungraded reverse-wrap stay
unchanged. Acknowledgements do not qualify image pixels. This run's focused
window leaves notification delivery inconclusive; the earlier inactive-window
positive remains retained.

The [owned routed-input suite](terminfo/owned-input.md) follows metadata restoration.
It adds 39 declared framework events with independent raw local-child bytes,
pre-action mode controls, paired full-state reads and strict restoration. Source
policy v2/schema 9 binds a fixed 60-file graph and all 14 suites. This implementation
adds no runtime result or frozen-grade change; historical v8 scopes remain fixed.

## Retained generated-input measurement

The [completed v9 input run](terminfo/2026-11-04-input-observations.md) retains 39
passing generated-event cases,81 paired frames and 80 raw child-input challenges,
with all 14 receipt bindings and 60 source/input files. Both the initial request-
order failure and the corrected complete collection are archived separately.
Its25 passing text-pixel comparisons are a new observation; historical negative
files and the original stock78/0/177 baseline remain frozen. Hardware/native
keyboard or pointer input, remote/mux and Windows/Linux runtime are not qualified.

The [calibrated v10 static-image measurement](terminfo/2026-11-04-image-observations.md)
retains 23 image stages (21 passing, one calibrated native-raster negative and one
causal inconclusive), 31 paired frames and 62 original image PNGs. Its 255-file archive
binds all 15 suites and 67 sources; both older failed/uncalibrated preparation archives
remain separate. Current raw stock 84/0/171 stays incomplete, and historical grades
are unchanged. This publishes evidence, not a renderer fix or full #770 completion.

The [completed v11 exact-query measurement](terminfo/2026-11-05-query-observations.md)
retains 40 exact contracts (38 matched, two mismatched), 44 original actions and
90 state captures, with all 16 suite bindings and a fixed 74-file source graph.
Its complete 266-file archive preserves the original SGR/cursor-style expectation
mismatches and cursor/pixel negatives. Matched negative replies and silence do
not qualify support; stock 85/0/170 remains incomplete. Earlier grades stay frozen.

The [owned App-private clipboard collection](owned-private-clipboard.md) adds a
separate schema 12 private-storage suffix under fixed policy v5. It preserves
original Q11 grades and denied reads; fresh runtime evidence remains required.

The [Neovim capability check](terminfo/2026-11-05-neovim-capability-check.md) is #844's
manual application check. Neovim 0.12.6, run in a build of `main`, learns truecolor and the
table's capabilities through XTGETTCAP, DECRQSS `m` and the `xterm-ntilde` entry. The run
covers direct and persistent panes and both OSC 52 settings, with 0.13.0 as the baseline.
It is a manual QA run and changes no grade.

### SGR underline-style qualification

The [SGR underline-style batch](terminfo/2026-11-08-sgr-underline-qualification.md) qualifies exactly
`sgr.underline.double` (SGR 21), `sgr.underline.curly` (4:3), `sgr.underline.dotted` (4:4) and
`sgr.underline.dashed` (4:5) through the layered `ntilde-owned-sgr-underline-grading-v1` extension over the
SGR-truecolor one. The pinned criteria already require one exact style each; the contract adds neutral-baseline,
unrelated-drift, DECSCNM, SGR 24 restoration and labeled spelling-ladder controls (SGR 4:2 and others are ungraded
controls). Claims are stored state only, not stroke appearance. Receipt schema 20 and source policy v14 (149 files)
bind it; earlier policies stay frozen.

### SGR underline-color qualification

The [SGR underline-color batch](terminfo/2026-11-08-sgr-underline-color-qualification.md) qualifies exactly
`sgr.underline-color-indexed`, `sgr.underline-color-rgb` and `sgr.underline-color-reset` through the layered
`ntilde-owned-sgr-underline-color-grading-v1` extension over the underline-style one; the overlapping aggregate
`sgr.underline.color` is not qualified. Stored underline style and color (index, literal bytes or default marker) are
observed; the resolved underline RGB is derived parent-side from the recorded palette because the App reports none;
an OSC 4 mutation of entries 4, 5 and 67 compares the whole palette at start, mutated and restored. Rendered
underline appearance is not claimed. Receipt schema 21 and source policy v15 (154 files) bind it; earlier policies stay frozen.

### App-observed underline-color resolution

The [follow-up](terminfo/2026-11-08-underline-color-resolved-qualification.md) to the underline-color batch adds the
opt-in `readScreen` flag `includeResolvedUnderlineColors` (with `includeResolvedColors`): per-cell underline color
resolved by the draw path's own underline precedence (explicit index through the palette, explicit literal, or the
default marker inheriting the presented text color), from the same coherent render snapshot. Three Ntilde extension
controls (indexed, literal, default-inheritance; OSC 4 start/mutate/restore) compare the App's value with an
independent expectation from stored state. No benchmark case changes; the pinned underline-color evidence stays
parent-derived. Theme-resolution stage only, no rendered-appearance claim. Receipt schema 22 and source policy v16
(158 files) bind it; earlier policies stay frozen.

### SGR selective-reset qualification (SGR 24 and SGR 27)

The [selective-reset batch](terminfo/2026-11-08-sgr-selective-reset-qualification.md) grades exactly `sgr.selective-reset.underline` and
`sgr.selective-reset.inverse` through a tenth layered collector extension (`ntilde-owned-sgr-selective-reset-grading-v1`), replaying the pinned
`C${setup}X${reset}Y` fixtures byte-exactly with no-op, full-reset, wrong-reset, SGR 59 and DECSCNM controls. Receipt schema 23, source policy v17
with the 163-file inventory; underline color is documented, not required; no pixel claim.

### SGR closure qualification (blink, overline, underline.color) and the pinned-SGR inventory

The [closure batch](terminfo/2026-11-08-sgr-closure-qualification.md) grades `sgr.blink`, `sgr.overline` and `sgr.underline.color` through an eleventh layered
collector extension (`ntilde-owned-sgr-closure-grading-v1`). `sgr.underline.color` shares the rgbUnderlineProbe helper with `sgr.underline-color-rgb` and is
graded from its own reads; the overline guard's unavailable-versus-false distinction is preserved. Receipt schema 24, source policy v18 (168 files). The
[pinned-SGR inventory](terminfo/2026-11-08-sgr-pinned-inventory.json) shows all 32 pinned `sgr.*` ids with extension evidence (coverage, not a stock score).

### Erase-in-line qualification (EL 0, EL 1, EL 2)

The [erase-in-line batch](terminfo/2026-11-08-erase-line-qualification.md) grades exactly `erase.line.right` (omitted-parameter spelling),
`erase.line.left` and `erase.line.all` through a twelfth layered collector extension (`ntilde-owned-erase-line-grading-v1`),
replaying the pinned setup and erase bytes and running the pinned termless `eraseRowResult` over parent-journaled stored cells, the
cursor and stored attributes. The contract adds cursor, unaffected-row/column, retained-attribute and wide-row controls; the other EL
spellings are labeled observations; background-color erase is observed, not required. The pinned terminal pixel path is capture-only
upstream and is not qualified. Receipt schema 25 and source policy v19 (173 files) bind it; earlier policies stay frozen.

### Saved-state and reset qualification (alternate screen, SGR 0, DECALN)

The [saved-state/reset batch](terminfo/2026-11-09-saved-reset-qualification.md) grades exactly `modes.alt-screen.exit`, `modes.altscreen-47`,
`modes.altscreen-1047`, `reset.sgr` and `reset.decaln` through an eighteenth layered collector extension (`ntilde-owned-saved-reset-grading-v1`).
The pinned `?47` and `?1047` verdicts are inconclusive by design and stay that way; their visible-buffer effect contracts
(`ntilde.saved-reset.effect/v1`) are graded and recorded separately. `reset.sgr` is a different case from `sgr.reset`. `reset.method`,
RIS and the pinned DECALN pixel path are not claimed. Receipt schema 31 and source policy v25 (204 files) bind it; earlier policies stay frozen.

### Erase with attributes and erase with margins qualification (EL over a background, ED under a scroll region)

The [erase attributes/margins batch](terminfo/2026-11-09-erase-attrs-region-qualification.md) grades exactly `erase.el-with-attrs` and `erase.ed-scroll-region` (both omitted-parameter
spellings) through a nineteenth layered collector extension (`ntilde-owned-erase-attrs-region-grading-v1`), replaying the pinned bytes and running the pinned termless criteria over
parent-journaled stored cells, App-resolved colors, the cursor, the margins and mode flags. The pinned EL criterion compares resolved background RGB, so a palette seed and a literal
ARGB erase of the same resolved color agree and the representation is only observed; the pen-at-erase-time controls and the ED-at-margins marker fill are observations, not requirements, and no
claim is made that ED is or is not clipped to the margins. The pinned terminal cursor-query paths never carry a verdict and are not qualified. Receipt schema 32 and source policy v26 (209
files) bind it; earlier policies stay frozen.

### Attribute-rectangle qualification (DECCARA, DECRARA, DECSACE)

The [attribute-rectangle batch](terminfo/2026-11-09-attr-rect-qualification.md) grades exactly `editing.deccara` (`ESC[1;1;3;5;7$r`), `editing.decrara`
(`ESC[1;1;3;5;7$t`) and `editing.decsace` through a twentieth layered collector extension (`ntilde-owned-attr-rect-grading-v1`). The pinned fixtures never
select an extent, and the VT510 reference makes the default the stream, so a conforming terminal changes the right-flank cells the pinned DECCARA/DECRARA control
requires to stay untouched: those pinned verdicts stay inconclusive and are never promoted. `editing.decsace` is inconclusive by design in the pinned probe. The
separately identified effect contracts (`ntilde.attr-rect.effect/v1`) are graded from an independent oracle, and the same pinned code over the same pinned bytes with
the rectangle extent selected first is a labeled experiment, not a pinned grade. The DECSACE setting is not readable, so it is inferred from the cells changed and
restored with a stream witness. Receipt schema 33 and source policy v27 (214 files) bind it; earlier policies stay frozen.

### Erase-character and saved-lines qualification (ECH, ED 3)

The [erase-character/saved-lines batch](terminfo/2026-11-09-erase-chars-history-qualification.md) grades exactly `erase.character` (ECH `ESC[3X`) and
`erase.screen.scrollback` (ED 3 `ESC[3J`) through a twenty-first layered collector extension (`ntilde-owned-erase-chars-history-grading-v1`). The cells,
cursor, margins, modes, active buffer and the actual ordered saved lines are observed independently on a disposable App's main screen; ECH is told apart from
DCH, whole-line erasure and a no-op, ED 3 from ED 2, ordinary scrolling and an already-empty history. Other ECH spellings are labeled observations. Physical
rows only; the pinned pixel/query paths never carry a verdict and are not qualified. Receipt schema 34 and source policy v28 (219 files) bind it; earlier
policies stay frozen.

### Erase-character and saved-lines qualification (ECH, ED 3)

The [erase-character/saved-lines batch](terminfo/2026-11-09-erase-chars-history-qualification.md) grades exactly `erase.character` (ECH `ESC[3X`) and
`erase.screen.scrollback` (ED 3 `ESC[3J`) through a twenty-second layered collector extension (`ntilde-owned-erase-chars-history-grading-v1`). The cells,
cursor, margins, modes, active buffer and the actual ordered saved lines are observed independently on a disposable App's main screen; ECH is told apart from
DCH, whole-line erasure and a no-op, ED 3 from ED 2, ordinary scrolling and an already-empty history. Other ECH spellings are labeled observations. Physical
rows only; the pinned pixel/query paths never carry a verdict and are not qualified. Receipt schema 35 and source policy v29 (226 files) bind it; earlier
policies stay frozen.

### Erase-in-display qualification (ED 0, ED 1, ED 2)

The [erase-in-display batch](terminfo/2026-11-09-erase-display-qualification.md) grades exactly `erase.screen.below`, `erase.screen.above` and
`erase.screen.all` (all explicit-parameter spellings) through a fourteenth layered collector extension (`ntilde-owned-erase-display-grading-v1`),
replaying the geometry-dependent pinned setup (pinned here to the owned 40x140 screen) and erase bytes and running the pinned termless
`eraseScreenResult` over parent-journaled stored cells, the cursor and the saved-line count. The contract adds whole-screen retained/erased,
cursor, mode-flag, boundary, retained-attribute and main-screen history controls; the other ED spellings are labeled observations; background-color
erase is observed, not required. The pinned pixel path is capture-only upstream and is not qualified; CSI 3J is not graded (it appears only as
disposable-screen cleanup). Receipt schema 27 and source policy v21 (183 files) bind it; earlier policies stay frozen.

### Line-editing qualification (IL, DL)

The [line-editing batch](terminfo/2026-11-09-line-editing-qualification.md) grades exactly `editing.insert-lines` (`ESC[1L`) and
`editing.delete-lines` (`ESC[1M`) through a fifteenth layered collector extension (`ntilde-owned-line-editing-grading-v1`),
replaying the pinned seed, cursor and edit bytes and running the pinned termless probes over parent-journaled stored cells of the whole
screen, the cursor and the App's stored margins, origin and insert mode. The contract adds full-screen line contents, a full-height boundary,
a DECSTBM scroll-region control (cursor inside, above and below), origin mode and moved-cell attribute controls; count spellings, new-line
background fill, cursor column and scrollback are observations. The pinned terminal pixel path never carries a verdict and is not qualified.
Receipt schema 28 and source policy v22 (188 files) bind it; earlier policies stay frozen.

### Charset qualification (UTF-8, DEC special graphics, DEC line drawing, G0/G1 switching)

The [charset batch](terminfo/2026-11-09-charsets-qualification.md) grades exactly `charsets.utf8`, `charsets.dec-special`,
`charsets.dec-line-drawing` and `charsets.g0-g1-switching` through a sixteenth layered collector extension
(`ntilde-owned-charsets-grading-v1`), replaying the pinned bytes untouched and running the pinned termless probes over parent-journaled
physical cells derived from complete stored cell details (never attribute strings or UTF-16 offsets). Controls cover the ASCII
same-source seed, designation versus invocation, persistence past SI and lowercase restoration witnesses. The pinned pixel and
cursor-query paths are not qualified. Receipt schema 29 and source policy v23 (193 files) bind it; earlier policies stay frozen.

### Scrolling and history qualification (nine scrollback cases)

The [scrolling/history batch](terminfo/2026-11-09-scrollback-qualification.md) grades exactly `scrollback.accumulate`, `.total-lines`,
`.scroll-up`, `.reverse-index`, `.scroll-down`, `.set-region`, `.alt-screen`, `.decstbm` and `.decstbm-reset` through a seventeenth layered
collector extension (`ntilde-owned-scrollback-grading-v1`) on the App's main screen. Viewport text, actual scrollback lines, margins, active
screen and cursor are observed independently and compared with a byte-interpreting model; the pinned code runs over the same observations.
Three pinned probes are null by construction, so the contract measures beyond them and the divergence is recorded; stock grades stay
inconclusive. Receipt schema 30 and source policy v24 (199 files) bind it; earlier policies stay frozen.

### Cursor qualification (CUP in a DECOM region, hide, shape, reverse wrap)

The [cursor batch](terminfo/2026-11-09-cursor-qualification.md) grades exactly `cursor.cup-scroll-region`, `cursor.hide`, `cursor.shape` and
`cursor.reverse-wrap` through a twenty-first layered collector extension (`ntilde-owned-cursor-qualification-grading-v1`). It runs the pinned termless and
app callbacks over parent-observed state, retained raw CPR exchanges (the fence gained a bounded `cursorReport`) and controlled synthetic-phase renders,
with a digest-checked geometry repair (`ntilde-owned-cursor-geometry-repair-v1`) for the undeclared-geometry defect of `cursor.reverse-wrap` and the latent
one of `cursor.shape`; the original failures are retained. The measurement first found Ntilde ignoring DECSET 45 (preserved as the before-fix attempt) and
led to the reverse-wraparound fix in #913. Receipt schema 34 and source policy v28 (221 files) bind it; earlier policies stay frozen.

### Selective-erasure qualification (DECSED, DECSERA)

The [selective-erasure batch](terminfo/2026-11-09-selective-erase-qualification.md) grades exactly `erase.selective` (DECSED `ESC[?2J`) and
`editing.decsera` (DECSERA `ESC[1;1;1;5${`) through a twenty-third layered collector extension (`ntilde-owned-selective-erase-grading-v1`). Both pinned
termless criteria are replayed byte-exactly over the seed `ESC[1;1H ESC[1"q P ESC[0"q ABCDZ` (DECSCA 1 protects `P`; DECSCA 0 leaves `ABCDZ`
unprotected) and graded from parent-observed stored cells, per-cell DECSCA protection (read through the observer's opt-in `includeProtection`, never inferred
from a surviving marker), the cursor, stored attributes, mode flags, buffer state and saved lines. Controls tell a selective erase apart from an ordinary
erase of the same seed (protected text is not read-only), a no-op, a protection-off pen, an overwrite of a protected cell, whole-screen clearing, the page
edges, bold seeding and incorrect rectangle bounds; the pinned finalizer must restore the unprotected pen. The pinned terminal pixel and cursor-query paths
never carry a verdict and are not qualified; ASCII only; stored state, not rendered appearance. Receipt schema 36 and source policy v30 (231 files) bind it;
earlier policies stay frozen.

### Rectangle-editing qualification (DECFRA, DECERA, DECCRA)

The [rectangle-editing batch](terminfo/2026-11-09-rectangle-editing-qualification.md) grades exactly `editing.decfra`, `editing.decera` and `editing.deccra`
through a twenty-fourth layered collector extension (`ntilde-owned-rectangle-editing-grading-v1`) over the selective-erasure one, replaying the pinned bytes and running the
pinned termless code over parent-journaled stored cells of the whole screen, the cursor, the App-stored margins, origin and insert mode and stored attribute tuples.
Controls cover bounds, defaults, clipping, no-ops, margins, origin mode, insert mode, the live pen (including a literal-RGB pen), attributed seed cells, DECERA over
DECSCA-marked text and, for DECCRA, overlap in every direction, a page copied onto itself, destination clipping and attribute preservation. DECCRA's unsupported
pages are an Ntilde policy that is observed and never graded. The pinned terminal cursor-query paths never carry a verdict and are not qualified. Checksums,
Unicode, the image plane and rendered appearance are not claimed; DECSERA is graded by the selective-erasure batch and DECCARA, DECRARA and DECSACE by the attribute-rectangle batch, not here. Receipt schema 37
and source policy v31 (236 files) bind it; earlier policies stay frozen.

### Column-editing qualification (SL, SR, DECIC, DECDC)

The [column-editing batch](terminfo/2026-11-10-column-editing-qualification.md) grades exactly `editing.sl`, `editing.sr`, `editing.decic` and `editing.decdc`
through a twenty-fifth layered collector extension (`ntilde-owned-column-editing-grading-v1`) over the rectangle-editing one, replaying the pinned bytes and running the
pinned termless code over parent-journaled stored cells of the whole screen, the cursor, the App-stored margins, origin and insert mode, stored attribute tuples and per-cell
DECSCA protection. An independent column model is compared with the whole screen over a seed at both page edges. Controls cover one, omitted, zero, three and oversized
(clamped) counts, the scrolling region with the cursor inside, above and below it, origin mode, insert mode, non-admitted identities, the start column (SL/SR from the left
margin whatever the cursor column; DECIC/DECDC at the first and last column), moved attributed and protected cells under a different pen, and attribute-free introduced
blanks. Related controls share a read so a suite stays within the observer's 64-capture budget. The pending-wrap state after an edit at the last column is observed, never
graded. Left/right margins (DECLRMM) are not implemented, so `modes.left-right-margin` is not claimed; DECRQCRA, Unicode, the image plane and rendered appearance are not
claimed either. The pinned terminal cursor-query paths never carry a verdict and are not qualified. The VT implementation is in
[the column-editing contract](terminfo/column-editing.md). Receipt schema 38 and source policy v32 (241 files) bind it; earlier policies stay frozen.

### Rectangle-checksum qualification (DECRQCRA)

The [rectangle-checksum batch](terminfo/2026-11-10-rectangle-checksum-qualification.md) grades exactly `editing.decrqcra` through a twenty-sixth layered collector
extension (`ntilde-owned-rectangle-checksum-grading-v1`) over the column-editing one. DECRQCRA is **off by default** (`AllowRectangleChecksumReport`), because a one-cell
checksum lets a program read the screen back; the measured run turns it on in its retained initial settings, and the receipt refuses evidence from a run that did not.
Every request is one fenced exchange: the raw DCS reply (or its explicit absence) is retained before the DA barrier, the parent journals it beside the stored cells, the
pinned termless code runs over the journaled reply, and an independent checksum model demands the exact reply of every request from the cells the request saw. Controls
cover the pinned and terminal-path spellings, each weighted attribute and attributes that must add nothing, omitted, zero, partly omitted and oversized borders, an ignored
page parameter, request ids, inverted rectangles, DECOM row offsets with page clipping, six non-admitted spellings that must stay unanswered, and unchanged state after
every request. Because the setting is on for the whole run, the exact-query suite's `q07.editing.decrqcra` witness owes the reply too (`ESC P 1 ! ~ FFB2 ESC \`, the
checksum of the `N` it binds at home); runs without the setting keep the original silence. Stored ASCII cells only: wide, combining and supplementary characters, the
persistent-session path and rendered appearance are not claimed. The VT implementation is in [the rectangle-checksum contract](terminfo/rectangle-checksum.md).
Receipt schema 39 and source policy v33 (248 files) bind it; earlier policies stay frozen.
