# OSC 7 and OSC 8 qualification: working-directory metadata and stored hyperlink cells (2026-11-11)

Qualifies two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `extensions.osc7-cwd` and `extensions.osc8`. The contract, the oracle
and the evidence boundaries are in [osc78.md](osc78.md). Both cases sit outside the frozen 80-row Core catalogue, so **no Core percentage changes** and no comparison
with another terminal is established. This is a qualification of existing behavior: **no production code changed** (`src/` is byte-identical to `main` at `926199a6`).

## Result

| Case | Pinned verdict (preserved) | Effect contract | Experiments (reads) | Stock grade (re-run in this run) |
|---|---|---|---|---|
| `extensions.osc7-cwd` | **inconclusive by design**: the pinned probe carries no fixture bytes | **supported** (`ntilde-osc7-cwd-metadata-effect-v1`) | 6 (10) | inconclusive (evidence `none`) |
| `extensions.osc8` | termless code over stored cells and link runs: **supported**; terminal path: inconclusive by design | **supported** (`ntilde-osc8-hyperlink-cells-effect-v1`) | 8 (13) | inconclusive (evidence `consumed`) |

The effect contracts are separately identified and never attributed to the pinned probes. Divergences recorded: `pinned-inconclusive-by-design` for OSC 7 (pinned inconclusive,
effect supported) and `terminal-path-inconclusive-by-design` for OSC 8 (termless supported, terminal path inconclusive; the pinned termless verdict and the effect contract agree).
Twenty-three reads were journaled as 26 paired frames (start, bind, 23 reads, finish); none needed settling retries. The stock collector, re-run in the same owned run, still grades
both ids inconclusive (167 inconclusive / 87 supported / 1 unsupported, as before), so this qualification **does not raise the stock compatibility score**. Counts of this suite:
`inconclusive/supported` 1, `supported/supported` 1.

## Pinned definitions

`extensions.osc7-cwd` has **no fixture to replay**: its termless function only reports whether the backend declares `osc7` (inconclusive, evidence `legacy`) and its terminal function
resolves inconclusive with evidence `none` without writing or reading anything. There are no pinned bytes to run unchanged; the OSC 7 bytes below belong to the effect contract.
`extensions.osc8` feeds `ESC[1;1H ESC[2K A ESC]8;;https://example.com/osc8-proof BEL LINK ESC]8;; BEL Z`, reads the stored cells (0,0)–(0,5) and their hyperlink metadata and passes on
`ALINKZ` with links `[null, uri, uri, uri, uri, null]`; its terminal path asks for the cursor position and always returns inconclusive. The retained code (both functions of both
probes, plus `readHyperlinkMetadata`) ran through a digest-checked derivation over the left/right-margin extension; the worker executed it over the journaled stock-context reads and
the parent re-derived the same results from the retained journal with an independent reimplementation (both OSC 8 paths on `ALINKZ` / cursor (0,6)). The retained pinned code was also
executed against the real bundle in a test over seven served variants (the pinned effect, a leaked link, a short link, a wrong URI, missing metadata, wrong text, a link on the witness
only), matching the reimplementation on both paths every time.

## What the App did (disposable direct-local pane, 40×140)

Each experiment starts from the same baseline (a completed `D;0` then `A`, links closed, title `LK-base-<run>`, cwd `/ntilde-qa/osc78-base`, a run nonce on row 9, a runtime palette
override). Reads are paired complete screen + pane-metadata + session-status captures; the oracle predicts the whole screen, the cursor, the title, the host cwd and every link run.
**Every one of the 23 reads matched the oracle**, including the recorded-only variants.

**OSC 7, reported working-directory metadata.** The measured baseline reported `/ntilde-qa/osc78-base`; ordinary text and an OSC 2 title change left it alone. The first
`file://localhost/ntilde-qa/osc7-<run>/first` made the pane report exactly that path with the title, cells, cursor, links and shell state unchanged; a distinguishable second URI
(`…/second`) replaced it (fresh, and different from both the baseline and the first), and the restoring URI brought the declared baseline back. The URI `file://localhost/ntilde-qa/osc7%20space/<run>`
was reported as `/ntilde-qa/osc7 space/<run>` (the encoded space decoded, no `%20` left). Recorded, never graded: a foreign authority (`file://remote-host.example/…`) was reported as the path
alone and the ST-terminated form behaved like BEL. The metadata carries the decoded path only, so **no host component is exposed**, by documented design (the authority is always dropped).
Distinct facts, never conflated: the URI the application sent, the pane's reported cwd metadata, the child process's actual working directory (**not observed**; nothing calls `chdir`)
and whether the named path exists (**not observed**; the paths are synthetic and never inspected).

**OSC 8, stored hyperlink-bearing cells.** The neutral baseline had no links. The same text with no OSC 8 stored `ALINKZ` with no link metadata (the no-link control). The exact pinned
fixture stored `ALINKZ` with exactly one run: row 0, columns 1–4 (length 4), `https://example.com/osc8-proof`, no id; the leading `A` and the closed-link witness `Z` were unlinked, the
cursor stood at (0,6), and later text on another row (`later`, row 4) stayed unlinked with the run unchanged. A close-only control (no open) produced no link. An `id=` link reported its
URI and id exactly on columns 1–4 of row 1. Two distinct adjacent links (switching without a close) stayed two runs (columns 1–3 and 4–6) with distinct snapshot-local identities. A plain
overwrite of the pinned cells left no stale link, and a second fixture with another URI on the same cells reported only that URI. Recorded, never graded: two separated runs with the same id
and URI share one identity, and the ST-terminated fixture behaved like BEL. **Identity**: the API's numeric identity is snapshot-local (assigned in order of first appearance within one
read; here 0 for the single link, 0 and 1 for two links); it was compared only as a same-link relation inside a snapshot, never across reads and never to the OSC id parameter.
Not qualified: click handling, browser launch, URI safety policy, URL opening (no URL is requested; the URIs use `example.com` and `example.invalid`) and rendered underline appearance.

**Restoration.** The first observation of this suite is the state the preceding suite left (title `SH-base-…`, cwd `/ntilde-qa/osc133-base`), not the original null metadata, which the API cannot
show. The suite restored its **prepared** baseline (title `LK-base-<run>`, cwd `/ntilde-qa/osc78-base`, no links), the initial cells and screen state and the original palette; neither the
as-found nor the original metadata is claimed restored. The App and worker were disposed and the private fixture removed; disposal is not offered as proof of metadata restoration.

## Controls and refusals

Controls that must hold before a mismatch can establish unsupported: the measured prior state, the unmarked bytes (ordinary text for OSC 7; the same text without OSC 8 for OSC 8) and
the gates (an OSC 2 title change must not move the cwd; a close-only sequence must not create a link), plus the prepared-baseline read. Portable tests drive each named defect through the
grader with the controls intact and require `unsupported`: link-open ignored, correct text without link metadata, wrong URI, wrong or dropped id, a range reaching the closed-link witness,
stopping short or shifted, stale metadata after an overwrite or a second fixture, a second link merged into (or replaced by a copy of) the first, a closed link affecting later text,
an undecoded or dropped encoded space, a stale or repeated second cwd, a host left in the path and an unrestored baseline. Invalid controls give `inconclusive`. Further refusals: wrong
fixtures, a changed barrier or read kind, cross-case run-log and frame substitution, pinned results that do not match the replay, identity mismatch, edited or truncated journals, tampered
digests, incomplete restoration and an unavailable record that forges graded artifacts.

## Identities

App 0.13.0 (operator supplied) built from `ca34a1d9` (arm64 Release, executable SHA-256 `f5814e51…9bf8`, assembly `c96eb8c4…8c1f`); scripts at the same revision (the measured scripts are the
committed ones); source policy v45 (300 files, manifest `fb9fda9f…02a4`); receipt schema 51; Node v24.22.2; owned run `f1ff205e…3726`, stock run `0c069a64…a7516`, measured
2026-11-11T17:54Z on macOS 27.0.1; private settings digest `75ac3a3a…9bb0`; layered adapter `ntilde-owned-osc78-grading-v1` (bundle `03454104…1f0d`). Predecessor suites reconfirmed in the same
run through the established runner (the receipt re-derived every earlier suite's evidence before completing; the query suite and the Core suites included). Two earlier attempts are retained with
their reasons: an aborted first launch (`Unexpected input during cell collection` two seconds in; `superseded/attempt-1-aborted.zip`) and a complete exploratory run on uncommitted scripts with
the same outcome (`superseded/attempt-2-superseded.zip`), superseded because measured files were edited afterwards.

## Evidence

[summary](2026-11-11-osc7-osc8/summary.json), [run log](2026-11-11-osc7-osc8/run-log.json), [journal-common](2026-11-11-osc7-osc8/journal-common.json), one
[case file](2026-11-11-osc7-osc8/) per id and one `journal-<case>-<experiment>.json` per experiment (its paired frames). Reproduce:

```bash
node --test scripts/terminfo/osc78-publication.test.mjs
```

The test reassembles the journal and re-derives the report, the receipt digests and the pinned-probe replay from the retained bytes. A fresh measurement needs the shared desktop lock and
`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.13.0` on Node 24.

## Limits

Reported working-directory metadata and stored hyperlink cells in a disposable direct-local App only. Not qualified: the child process's real working directory, path existence, remote-host
validity, malformed or unsupported OSC 7 forms (no rejection or normalization rule is invented), click handling, browser launch, URI safety policy, rendered underline appearance, OSC title
handling, OSC 133 P, OSC 633, `extensions.semantic-prompts`, real-shell emission, SSH or mux delivery, Windows/Linux and hidden panes. Restoration is the prepared baseline, the initial cells and
the original palette.
