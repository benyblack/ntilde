# Owned erasure and cell-editing observations

`ntilde-owned-erasure-v1` and `ntilde-owned-cell-editing-v1` run serially after
runtime colors, in the proved disposable worker and private local pane. Their
13 and 22 ASCII experiments retain 29 and 47 parent-owned logical frames.
Each frame brackets text, complete cell attributes, cursor and buffer modes
with two independent reads. Each read also obtains the actual complete bounded
history through `readScrollback`; a count alone cannot qualify ED3.

The erasure cases cover ED omitted/0/1/2/3, ECH omitted/0/3/clamped, erase outside
scroll margins and literal RGB background erasure. Editing covers ICH/DCH and
IL/DL omitted/0/2/clamped, IL/DL outside margins, REP omitted/0/3 and DECALN.
REP writes its graphic immediately before the repeat command in one fenced
sequence. ECMA-48 section 8.3.103 leaves REP undefined after an intervening control
function, so a DA barrier must not separate that graphic from REP. See
[ECMA-48](https://ecma-international.org/wp-content/uploads/ECMA-48_5th_edition_june_1991.pdf)
and the [xterm control sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html).

Before writes, the fixture requires an empty default main viewport, full margins,
normal origin/wrap/insert/pending-wrap modes and zero actual history. Each batch
seeds two known history lines independently of ED2/ED3, then overwrites every
physical viewport cell with CUP and padded ASCII rows. Setup never uses display
or saved-line erasure to establish its own controls. Every operation must preserve
the known history except ED3, whose target is an empty history with an unchanged
viewport and cursor. IL/DL targets remain bounded by recorded margins.

The parent generates all expected text and relevant attributes from the known
inputs. It checks complete before controls and after states, including unaffected
rows, cursor, margins, modes and history. Background-erased blanks qualify only
the literal background and its flags; foreground and decoration remain recorded
without a normative claim. Failed controls are inconclusive; a qualified mismatch
fails a local comparison. Neither changes a stock collector grade.

Each batch has its own nonce, independent 64-attempt observers and fixed plan.
The final state and actual history must equal the initial state. A finally block
exits alternate screen, restores modes, clears the private viewport/history and
restores cursor visibility. After a fatal fence, bounded teardown output accepts
no replies and cannot qualify completion. Missing requested facts are fatal;
only an initial typed `screenDetailsUnavailable` may yield a marker before writes.

`erase-snapshots.jsonl` / `editing-snapshots.jsonl` and their `*-observations.json`
reports retain inputs, paired reads, hashes and comparisons. Receipt v4 adds both
bindings while retaining all seven previous suites. It re-derives both reports,
compares journals/counts, and permits only empty journals for typed unavailable
markers, after verified worker exit, app shutdown and fixture removal. Exact
scene/state/observer/report sources are archived and hashed. Historical v1-v3
receipts and published reports remain reproducible.

This qualifies bounded ASCII stored cells and actual history text. Unicode editing,
rendered pixels, resize/reflow, full saved state and Windows/Linux runtime behavior
remain separate work. The frozen stock result remains 78 supported, 177 inconclusive,
zero unsupported, `suiteComplete=false`, with an ungraded reverse-wrap diagnostic.
