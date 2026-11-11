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
