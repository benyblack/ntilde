# Owned saved-state and alternate-screen observations

`ntilde-owned-saved-alternate-v1` follows the restored resize suite in the disposable
macOS runner. Nine source-defined cases retain **57 paired full-state frames**.
Each frame independently reads complete active cells/attributes, cursor
position/presentation, exposed buffer modes and complete bounded history twice.

| Cases | Independent comparisons |
| --- | --- |
| Modes 47, 1047 and 1049 | Known main and alternate contents; entry, active writes, exit, per-screen pen witnesses and retained re-entry. Mode 1049 also has a repeated entry. |
| Main 1048 → DECRC, alternate DECSC → 1048, main CSI s/u | Complete combined stored rendition and explicit RGB/underline color; origin/pending wrap; current autowrap preservation; new G1 and G0 character witnesses after restoration. |
| Independent banks | Distinct saved positions and palette pens in each screen; live main cursor differs from its saved cursor. |
| DECSTR boundary | Preserve active cells/current position; default the active saved bank while preserving the inactive main bank. |
| RIS boundary | Observe main/alternate cells and both saved-bank restoration witnesses after a full reset. A reset method call alone is not a positive result. |

The live attribute tuples cover bold, faint, italic, underline style/color,
overline, blink, inverse, hidden and strikethrough, plus inherited/indexed/literal
color storage. New writes witness a restored pen; the API does not expose a raw
current pen or an inactive saved bank. G0/G1 output and GL behavior are witnessed
with decoded cells. Owning VT tests separately compare every serialized saved
rendition field, all four charset slots and GL, origin and pending wrap across
both banks and all three cursor-save spellings. They cover RGB and palette pens,
active-only soft reset and both-bank hard reset.

Autowrap is observed off immediately after restoration while the saved pending
latch is on. The probe then explicitly enables wrap before consuming that latch.
It makes no claim about consuming a pending latch while autowrap remains off.

## Independent controls and known reset limit

Setup and cleanup use explicit DECSTR/default-pen/erase/home operations on both
screens. They do **not** depend on RIS, which remains an operation under test.
The first exploratory run used RIS as setup and produced invalid controls; its
report and exact archived source remain unchanged. Revised controls distinguish
fixture setup from the observed reset behavior.

The [committed-source measurement](2026-11-03-saved-state.md) qualified eight
cases and retained one explicit
RIS cell-clearing negative: RIS while alternate is active leaves the inactive
main text visible. Its cursor-bank witnesses still passed. This limitation is
retained for #773; it is not hidden by the successful bank reset or counted as
supported. All nine independent controls passed in that measurement.

## Binding, bounds and cleanup

Only the source-selected private worker writes probe bytes. Normal user settings
and sessions are not read. The parent re-proves the App/worker/executable/user/TTY
relationship for each request, retains the existing kernel geometry and disabled
output-processing proof, and pins the original common pane/geometry. Both readers
reject later geometry changes. No new AgentHost action is introduced.

Initial grids require 10–128 rows, 48–256 columns and at most 8,192 cells. All cases
fit within the existing 64-frame limit; history remains empty and is actually read
from zero. Only an initial typed screen-details or saved-geometry refusal can be
unavailable, before fixture writes. A later failure remains a failure.

The v7 receipt requires all eleven preceding suite bindings plus this saved-state
binding. It hashes the exact retained source copies, journal and rederived report,
checks the parent pane/geometry declaration and common cell/kernel facts, and
requires verified worker/App/socket shutdown and fixture removal. Any retained
saved identity, parent marker, journal or source file requires v7 validation;
partial stripping cannot silently downgrade it. Contradictory error/status fields,
changed source bytes and incomplete or unpaired frames are refused.

A failed before-control is inconclusive. A coherent target mismatch with a passing
control is an explicit negative. Malformed evidence cannot produce a completed
report. The final active state/history must equal the pristine initial snapshot;
explicit private cleanup also clears both screens and resets their saved banks.
The prior PTY output mode returns before stock collection. Cleanup errors preserve
the original failure and cannot create a successful receipt.

These observations qualify the stated active fields and restoration witnesses.
They do not directly inspect invisible banks, pixels, desktop timing, generic
Unicode editing, history attributes, or Windows/Linux runtime. Existing stock
results remain 78 supported / 0 unsupported / 177 inconclusive,
`suiteComplete=false`, with the ungraded reverse-wrap diagnostic. Historical
reports and the earlier RGB bold pixel negative remain unchanged.
