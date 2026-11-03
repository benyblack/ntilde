# Owned saved state and alternate screens — 2026-11-03

**Eight comparisons passed; one retained an explicit reset failure.** All nine
before-controls passed, with no inconclusive observations. The
[machine-readable report](2026-11-03-saved-state.json) retains the source-derived
inputs, 57 paired full-state frames and completed v7 receipt.

| Measurement | Value |
| --- | --- |
| App and adapter committed source | `4878d7abc63c1dc9f7efaeda79b075b0e3f72d01` |
| Operator-supplied version | `0.13.0+4878d7abc63c1dc9f7efaeda79b075b0e3f72d01` (not independently attested) |
| Runtime | macOS arm64; Node `v24.22.1`; pinned `terminfo.dev@4.0.0` |
| Viewport | 40 × 140 cells |
| Local evidence | `artifacts/terminfo/owned-Go7TsD`, complete 154-file copy verified in the primary checkout |
| Outcome | 8 passed / 1 failed / 0 inconclusive; completed cleanup; receipt v7 |
| Saved report bytes / SHA-256 | 417,479 / `7f493c53223c1326717469d5e3bde5c3e3d21e8ea45aeaf904aea923b6a8f5ad` |

| Cases | Outcome |
| --- | --- |
| Modes 47, 1047, 1049 | Passed entry, writes, exit, per-screen pen and re-entry witnesses; 1049 repeated entry also passed. |
| Main 1048 → DECRC, alternate DECSC → 1048, main CSI s/u | Passed position, origin/pending-wrap, current autowrap preservation, full stored rendition and G0/G1/GL output witnesses. |
| Independent saved banks | Passed distinct main/alternate saved positions and palette-pen writes. |
| DECSTR | Passed preservation of active content/current position, active saved-bank reset and inactive main-bank preservation. |
| RIS | **Failed** clearing inactive main content. Both saved-bank default witnesses and alternate clearing passed. |

The RIS `full-reset` frame (snapshot 51) retains `M` at main row 0, column 0
where a blank cell was expected. All other exposed fields and attributes match;
the four following restoration assertions pass. This is an explicit negative
for #773, not a supported reset result. Setup and final cleanup independently
sanitize both screens without relying on RIS. The final active snapshot equals
the pristine initial snapshot, with empty history and original geometry.

Each frame contains two matching complete active-screen reads: cells and stored
attributes, cursor position/presentation, exposed buffer modes and bounded
history. Restored pens and character sets are witnessed by new glyphs, rather
than inferred from old cells. Owning VT tests additionally compare every
serialized rendition field, all four charset slots and GL in both banks for
all three save aliases. The live probe does not directly inspect invisible
saved banks. [Controls and limits](owned-saved-state.md) describe these bounds.

The parent re-proved the private App/worker/TTY relationship for each request.
The original common pane and geometry matched; independent kernel geometry
agreed and output processing was disabled during probes. The previous PTY mode
returned before stock collection. All twelve suite bindings were verified,
including retained source/input hashes and exact saved-report rederivation.
The v7 receipt followed verified worker/App/socket shutdown and fixture removal.

The public context omits the executable path. Full original context, ownership
proof, dependency inputs, source archive and preceding reports remain in the
complete local run and its verified copy. Their receipt hashes cannot all be
recomputed from this public JSON alone. The 44 archived source/dependency inputs
match the committed source. Executable and assembly bytes have separate hashes;
system-tool bytes and the kernel are not attested, and these hashes do not
independently attest the binary's build source. The version is an operator label.

These measurements complete the bounded restoration-observation scope of #767.
The earlier exploratory run with RIS-based setup had invalid controls and is
retained unchanged; its inconclusive cases are not terminal failures. The prior
RGB bold pixel negative remains unchanged. Stock results stay **78 supported /
0 unsupported / 177 inconclusive**, `suiteComplete=false`, with the ungraded
reverse-wrap diagnostic. Historical reports and stock grades remain frozen.
